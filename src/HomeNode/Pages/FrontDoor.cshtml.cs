using HomeNode.Data;
using HomeNode.Services;
using HomeNode.Services.Extraction;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomeNode.Pages;

// The front door: the house's page, the one a person in the circle talks
// to. Large type, few words. A person states a thing, asks a question, or
// tells the house a note, and what was said lands in the shared record.
// The page anchors on the Household itself: the recipient of every
// telling is the house, so the greeting names the house, never a
// person picked out by a role.
//
// The say-box runs the extraction pipeline: a typed telling stands in
// for the spoken one (voice capture is a later step) and travels the
// same road a transcript will: context slice, extraction, validation,
// the confirmation dispatcher, the record. When the pipeline proposes
// an action, the house asks here, and a named person answers here.
//
// The recent list below the say-box honors the audience stored on
// each entry at write time, exactly as the brief does: an entry
// written for named people renders only for a reader it names. With
// no reader named, only the house's shared entries (Open and
// Household classes) render at all; an entry whose audience cannot
// be determined renders for nobody. The check fails closed, never
// open.
// One person's standing audience default as the front door shows it
// (audience decision clause 4): the stored value, or the minor's Ask until
// set (clause 8), or no default.
public class AudienceDefaultRow
{
    public required Person Person { get; init; }
    public required string Label { get; init; }
}

public class FrontDoorModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly ExtractionContextBuilder _contextBuilder;
    private readonly ExtractionService _extraction;
    private readonly ConfirmationService _confirmations;
    private readonly AudienceDecisionService _audience;
    private readonly TestSendService _testSend;
    private readonly ILogger<FrontDoorModel> _logger;

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public FrontDoorModel(
        AppDbContext db,
        ExtractionContextBuilder contextBuilder,
        ExtractionService extraction,
        ConfirmationService confirmations,
        AudienceDecisionService audience,
        TestSendService testSend,
        ILogger<FrontDoorModel>? logger = null)
    {
        _db = db;
        _contextBuilder = contextBuilder;
        _extraction = extraction;
        _confirmations = confirmations;
        _audience = audience;
        _testSend = testSend;
        _logger = logger ?? NullLogger<FrontDoorModel>.Instance;
    }

    // Construction without the Test Send control, used by the suites'
    // page model checks: no stand-ins configured, so the control
    // renders its unconfigured state. The suites' assertions are
    // untouched by the end-to-end exercises.
    public FrontDoorModel(
        AppDbContext db,
        ExtractionContextBuilder contextBuilder,
        ExtractionService extraction,
        ConfirmationService confirmations,
        AudienceDecisionService audience)
        : this(db, contextBuilder, extraction, confirmations, audience,
            new TestSendService(new HttpClient(), Array.Empty<StandInEndpoint>()))
    {
    }

    // Stand-ins a judge can trigger from here (the Test Send
    // control). Empty when this instance has none configured.
    public IReadOnlyList<StandInEndpoint> StandIns => _testSend.ConfiguredStandIns;
    public string? TestSendNote => TempData["TestSendNote"] as string;

    public Household? Household { get; private set; }
    public List<Person> People { get; private set; } = new();

    // The circle's standing audience defaults, shown plainly so a
    // person can see their own and set it by telling, below.
    public List<AudienceDefaultRow> Defaults { get; private set; } = new();

    // Audience asks waiting on the reader named above (ViewerId),
    // and only on that reader: an ask is presented to the person
    // entitled to answer it, in their own presentation moment, and
    // it names no content (clause 7). Loading the page as that
    // reader is the presentation, so the lapse clock starts here.
    public List<AudienceAsk> PendingAsks { get; private set; } = new();

    public string? DefaultNote => TempData["DefaultNote"] as string;
    public string? ReadBackText => TempData["ReadBackText"] as string;
    public int? PendingDefaultRequestId => TempData["PendingDefaultRequestId"] as int?;
    public int? PendingDefaultHolderId => TempData["PendingDefaultHolderId"] as int?;

    // Who is reading the recent list. The recent list shows the
    // record as this person may read it; with nobody named, only
    // the house's shared entries show.
    [BindProperty(SupportsGet = true)]
    public int? ViewerId { get; set; }

    // The newest words in the shared record, newest first: what the house
    // has confirmed, and what is still waiting on a person.
    public List<Event> RecentEvents { get; private set; } = new();

    public string? HouseReply => TempData["HouseReply"] as string;
    public string? PipelineNote => TempData["PipelineNote"] as string;
    public int? ProposedEventId => TempData["ProposedEventId"] as int?;
    public int? ProposedAuthorId => TempData["ProposedAuthorId"] as int?;

    public async Task OnGetAsync()
    {
        await LoadAsync();
    }

    public async Task<IActionResult> OnPostTellAsync(int? speakerId, string? telling)
    {
        var household = await _db.Households.FirstOrDefaultAsync();
        if (household is null || string.IsNullOrWhiteSpace(telling))
        {
            return RedirectToPage();
        }

        var speaker = speakerId is int id
            ? await _db.Persons.FirstOrDefaultAsync(p => p.Id == id)
            : null;
        var context = await _contextBuilder.BuildAsync(household.Id);
        var tz = TimeZoneInfo.FindSystemTimeZoneById(household.TimeZoneId ?? "America/Chicago");
        var input = new ExtractionInput
        {
            UtteranceId = $"u-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..26],
            Transcript = telling.Trim(),
            SourceType = "tap",
            OccurredAtLocal = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz),
            Speaker = speaker is null
                ? null
                : new ExtractionPersonRef
                {
                    PersonId = speaker.Id,
                    DisplayName = speaker.DisplayName,
                    Role = speaker.Role.ToString()
                },
            Context = context
        };

        var result = await _extraction.ProcessAsync(input);
        TempData["HouseReply"] = result.HouseReply;
        if (!result.Validation.IsValid)
        {
            // The validator's own words are internal: they go to the
            // server log only, never to the page a visitor reads.
            _logger.LogWarning(
                "The house refused a telling from speaker {SpeakerId}: {ValidationErrors}",
                speakerId, string.Join(" ", result.Validation.Errors));
            TempData["PipelineNote"] =
                "The house could not write that down as stated, so nothing was recorded.";
        }
        else if (result.Output.ClarificationNeeded)
        {
            TempData["PipelineNote"] = "Nothing was written down yet. The house is asking first.";
        }
        else if (result.ProposedEvent is not null)
        {
            TempData["ProposedEventId"] = result.ProposedEvent.Id;
            TempData["ProposedAuthorId"] = result.ProposedEvent.AuthorPersonId;
            TempData["PipelineNote"] = "Written down as a proposal. Nothing is sent until a person confirms it.";
        }
        else if (result.WrittenEvents.Count > 0)
        {
            TempData["PipelineNote"] = result.WrittenEvents.Count == 1
                ? "Written into the shared record."
                : $"Written into the shared record as {result.WrittenEvents.Count} entries.";
        }
        if (result.AsksCreated.Count > 0)
        {
            // No content is named here: the ask itself is presented
            // only to the person entitled to answer it, when they
            // read as themselves below.
            TempData["PipelineNote"] = (TempData["PipelineNote"] as string ?? "")
                + " One entry is held at its narrowest audience while the house asks the person it belongs to.";
        }
        return RedirectToPage();
    }

    // The Test Send control: a remote trigger only. This handler
    // sends no telling text anywhere; it asks the named stand-in to
    // send its own sealed telling through the real ingress, and
    // reports what the stand-in answered, in the stand-in's own
    // terms. If the relay is stopped, the stand-in answers
    // queued-unrecorded and nothing lands here.
    public async Task<IActionResult> OnPostTestSendAsync(string standInName)
    {
        var result = await _testSend.TriggerAsync(standInName);
        var packet = result.PacketId is null ? "" : $" Packet {result.PacketId}.";
        TempData["TestSendNote"] =
            $"Test Send from Agent ({result.StandInName}): {result.Status}.{packet} {result.Note}";
        return RedirectToPage();
    }

    // Setting a default by telling (clause 4): the telling is
    // recorded, the house reads it back, and only the holder's own
    // disposal applies it. An instruction about someone else's
    // default is recorded as a proposal to that person and never
    // applied here.
    public async Task<IActionResult> OnPostSetDefaultAsync(int holderPersonId, int actorPersonId, string? value)
    {
        var household = await _db.Households.FirstOrDefaultAsync();
        if (household is null || !Enum.TryParse<AudienceDefaultValue>(value, out var parsed))
        {
            return RedirectToPage();
        }
        var request = await _audience.ProposeByTellingAsync(
            household.Id, holderPersonId, actorPersonId, parsed,
            $"Set the audience default to {parsed}.", "tap", DateTime.UtcNow);
        TempData["ReadBackText"] = request.ReadBackText;
        if (request.State == AudienceDefaultRequestState.PendingReadBack)
        {
            TempData["PendingDefaultRequestId"] = request.Id;
            TempData["PendingDefaultHolderId"] = holderPersonId;
            TempData["DefaultNote"] = "The house read it back. Nothing is set until the holder says yes.";
        }
        else
        {
            TempData["DefaultNote"] = "Recorded as a proposal to the holder. It is not applied; only the holder can set their own default.";
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostConfirmDefaultAsync(int requestId, int actorPersonId)
    {
        await _audience.ConfirmReadBackAsync(requestId, actorPersonId, DateTime.UtcNow);
        TempData["DefaultNote"] = "Set. It applies to tellings from now on; nothing already written changes.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeclineDefaultAsync(int requestId, int actorPersonId)
    {
        await _audience.DeclineReadBackAsync(requestId, actorPersonId, DateTime.UtcNow);
        TempData["DefaultNote"] = "Not set. The default stays as it was.";
        return RedirectToPage();
    }

    // Answering an ask (clause 7): only the person the page is
    // reading as may answer, and the service enforces that the
    // answerer is the person the house asked.
    public async Task<IActionResult> OnPostAnswerAskAsync(int askId, int actorPersonId, string? choice)
    {
        if (choice is "Private" or "Household")
        {
            await _audience.AnswerAskAsync(askId, actorPersonId, choice, null, DateTime.UtcNow);
            TempData["DefaultNote"] = choice == "Household"
                ? "Answered. The entry takes the audience it would have had."
                : "Answered. The entry stays at its narrowest audience.";
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostConfirmAsync(int eventId, int actorPersonId)
    {
        await _confirmations.ConfirmAsync(eventId, actorPersonId, "tap");
        var ev = await _db.Events.FirstAsync(e => e.Id == eventId);
        TempData["HouseReply"] = ev.ConfirmationState == ConfirmationState.Done
            ? SpokenTemplates.Done
            : "Confirmed.";
        TempData["PipelineNote"] = "Confirmed by a person, in the open. The record shows who and when.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeclineAsync(int eventId, int actorPersonId)
    {
        await _confirmations.DeclineAsync(eventId, actorPersonId, "tap");
        TempData["HouseReply"] = "Not sent.";
        TempData["PipelineNote"] = "Declined by a person. It stays in the record as not confirmed, and nothing was sent.";
        return RedirectToPage();
    }

    private async Task LoadAsync()
    {
        Household = await _db.Households.FirstOrDefaultAsync();
        if (Household is null)
        {
            return;
        }
        People = await _db.Persons
            .Where(p => p.HouseholdId == Household.Id)
            .OrderBy(p => p.Id)
            .ToListAsync();
        var recent = await _db.Events
            .Include(e => e.AuthorPerson)
            .Where(e => e.HouseholdId == Household.Id)
            .OrderByDescending(e => e.CreatedAtUtc)
            .Take(100)
            .ToListAsync();
        RecentEvents = recent
            .Where(e => ViewerId is int viewer
                ? BriefService.IsVisibleTo(e, viewer)
                : e.SensitivityClass is SensitivityClass.Open or SensitivityClass.Household)
            .Take(10)
            .ToList();
        Defaults = new List<AudienceDefaultRow>();
        foreach (var person in People)
        {
            var effective = await _audience.GetEffectiveDefaultAsync(Household.Id, person.Id);
            var row = await _audience.GetDefaultRowAsync(Household.Id, person.Id);
            Defaults.Add(new AudienceDefaultRow
            {
                Person = person,
                Label = row is not null
                    ? row.Value.ToString()
                    : effective is not null
                        ? $"{effective} until set"
                        : "No default set"
            });
        }
        if (ViewerId is int reader)
        {
            PendingAsks = await _audience.PendingAsksForAsync(reader, DateTime.UtcNow);
        }
    }
}
