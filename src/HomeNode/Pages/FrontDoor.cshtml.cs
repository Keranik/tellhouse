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

    // The words the person just said, echoed back in the reply area
    // so they can see exactly what the house is asking about, and
    // the action-named words on the reply's two buttons.
    public string? ProposedWords => TempData["ProposedWords"] as string;
    public string ConfirmLabel => TempData["ConfirmLabel"] as string ?? "Confirm";
    public string DeclineLabel => TempData["DeclineLabel"] as string ?? "Don't tell";

    // The household zone the recent list's times are shown in.
    public TimeZoneInfo HouseholdZone { get; private set; } =
        TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");

    public async Task OnGetAsync()
    {
        await LoadAsync();
    }

    public async Task<IActionResult> OnPostTellAsync(int? speakerId, string? message)
    {
        var household = await _db.Households.FirstOrDefaultAsync();
        if (household is null || string.IsNullOrWhiteSpace(message))
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
            Transcript = message.Trim(),
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
                "The house refused a message from speaker {SpeakerId}: {ValidationErrors}",
                speakerId, string.Join(" ", result.Validation.Errors));
            TempData["PipelineNote"] =
                "I couldn't make sense of that, so nothing was saved. Try one short sentence.";
        }
        else if (result.Output.ClarificationNeeded)
        {
            TempData["PipelineNote"] = "Nothing is saved yet. Reply in the box below.";
        }
        else if (result.ProposedEvent is not null)
        {
            TempData["ProposedEventId"] = result.ProposedEvent.Id;
            TempData["ProposedAuthorId"] = result.ProposedEvent.AuthorPersonId;
            TempData["ProposedWords"] = result.ProposedEvent.Body;
            TempData["PipelineNote"] = "It's in the record either way.";
            // The confirm button names the action it takes: the
            // pending action the dispatcher recorded for this entry
            // decides whether it tells a person, puts media on, or
            // sets a reminder.
            var pendingAction = await _db.EventActions
                .FirstOrDefaultAsync(a => a.EventId == result.ProposedEvent.Id);
            switch (pendingAction?.ActionType)
            {
                case EventActionType.MediaPlay:
                    TempData["ConfirmLabel"] = "Put it on";
                    TempData["DeclineLabel"] = "No, don't";
                    break;
                case EventActionType.CreateReminder:
                    TempData["ConfirmLabel"] = "Set reminder";
                    TempData["DeclineLabel"] = "No, don't";
                    break;
                case EventActionType.NotifyPerson or EventActionType.RelayMessage:
                    var target = pendingAction.TargetPersonId is int targetId
                        ? await _db.Persons.FirstOrDefaultAsync(p => p.Id == targetId)
                        : null;
                    TempData["ConfirmLabel"] = target is null
                        ? "Tell them"
                        : $"Tell {NameWording.First(target.DisplayName)}";
                    TempData["DeclineLabel"] = "Don't tell";
                    break;
                default:
                    TempData["ConfirmLabel"] = "Confirm";
                    TempData["DeclineLabel"] = "Don't tell";
                    break;
            }
        }
        else if (result.WrittenEvents.Count > 0)
        {
            TempData["PipelineNote"] = result.WrittenEvents.Count == 1
                ? "Saved."
                : $"Saved as {result.WrittenEvents.Count} entries.";
        }
        if (result.AsksCreated.Count > 0)
        {
            // No content is named here: the ask itself is presented
            // only to the person entitled to answer it, when they
            // read as themselves below.
            TempData["PipelineNote"] = (TempData["PipelineNote"] as string ?? "")
                + " One entry stays private until the right person decides who sees it.";
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
        // The note reports the result in the page's own
        // plain words; the packet id and the stand-in's raw status and
        // note stay off the visitor surface. The sent wording is the
        // finding's rewrite verbatim; the other statuses get the same
        // form in plain words, because "done" for a send that did not
        // happen would be a false claim.
        var personFirst = NameWording.First(
            string.IsNullOrWhiteSpace(result.PersonName)
                ? standInName.Replace("StandIn", "", StringComparison.OrdinalIgnoreCase)
                : result.PersonName);
        TempData["TestSendNote"] = result.Status switch
        {
            "sent" =>
                $"{personFirst}'s assistant sent a note. It's at the top of Recent.",
            "queued-unrecorded" or "unreachable" =>
                $"{personFirst}'s assistant couldn't reach the house. Nothing was sent. Try again in a minute.",
            _ =>
                $"{personFirst}'s assistant isn't connected to this household."
        };
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
            $"Set the sharing default to {parsed}.", "tap", DateTime.UtcNow);
        TempData["ReadBackText"] = request.ReadBackText;
        if (request.State == AudienceDefaultRequestState.PendingReadBack)
        {
            TempData["PendingDefaultRequestId"] = request.Id;
            TempData["PendingDefaultHolderId"] = holderPersonId;
            TempData["DefaultNote"] = "Check this change below. Nothing is set until you save it.";
        }
        else
        {
            var holder = await _db.Persons.FirstOrDefaultAsync(p => p.Id == holderPersonId);
            var holderFirst = NameWording.First(holder?.DisplayName);
            TempData["DefaultNote"] = $"Only {holderFirst} can change {holderFirst}'s default. We'll pass your suggestion to {holderFirst}.";
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostConfirmDefaultAsync(int requestId, int actorPersonId)
    {
        await _audience.ConfirmReadBackAsync(requestId, actorPersonId, DateTime.UtcNow);
        TempData["DefaultNote"] = "Saved. This applies to new messages only.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeclineDefaultAsync(int requestId, int actorPersonId)
    {
        await _audience.DeclineReadBackAsync(requestId, actorPersonId, DateTime.UtcNow);
        TempData["DefaultNote"] = "Not saved. The default stays as it was.";
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
                ? "Saved. Everyone in the household can now see that entry."
                : "Saved. Only the people it names can see that entry.";
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostConfirmAsync(int eventId, int actorPersonId)
    {
        try
        {
            await _confirmations.ConfirmAsync(eventId, actorPersonId, "tap");
        }
        catch (InvalidOperationException)
        {
            // A second click on an entry that has already moved on:
            // the answer is a plain note, never the error page.
            TempData["HouseReply"] = "Already confirmed.";
            return RedirectToPage();
        }
        var ev = await _db.Events.FirstAsync(e => e.Id == eventId);
        TempData["HouseReply"] = ev.ConfirmationState == ConfirmationState.Done
            ? SpokenTemplates.Done
            : "Confirmed.";
        var delivery = await _db.AlertDeliveries
            .Include(d => d.ToPerson)
            .FirstOrDefaultAsync(d => d.EventId == eventId);
        TempData["PipelineNote"] = delivery?.ToPerson is not null
            ? $"Sent to {NameWording.First(delivery.ToPerson.DisplayName)}."
            : "Confirmed.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeclineAsync(int eventId, int actorPersonId)
    {
        try
        {
            await _confirmations.DeclineAsync(eventId, actorPersonId, "tap");
        }
        catch (InvalidOperationException)
        {
            TempData["HouseReply"] = "Already confirmed.";
            return RedirectToPage();
        }
        TempData["HouseReply"] = "Not sent.";
        TempData["PipelineNote"] = "Not sent. It stays in the record, marked Not sent.";
        return RedirectToPage();
    }

    private async Task LoadAsync()
    {
        Household = await _db.Households.FirstOrDefaultAsync();
        if (Household is null)
        {
            return;
        }
        HouseholdZone = HouseholdTime.ZoneFor(Household.TimeZoneId);
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
                    ? DefaultLabel(row.Value)
                    : effective == AudienceDefaultValue.Ask
                        ? "Ask each time (default for under-18s)"
                        : effective is not null
                            ? DefaultLabel(effective.Value)
                            : "Not set yet"
            });
        }
        if (ViewerId is int reader)
        {
            PendingAsks = await _audience.PendingAsksForAsync(reader, DateTime.UtcNow);
        }
    }

    // The plain words a stored audience default is shown in.
    private static string DefaultLabel(AudienceDefaultValue value) => value switch
    {
        AudienceDefaultValue.Private => "Just me and anyone I name",
        AudienceDefaultValue.Household => "Everyone in the household",
        AudienceDefaultValue.Ask => "Ask me each time",
        _ => value.ToString()
    };
}
