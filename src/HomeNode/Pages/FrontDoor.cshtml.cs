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

    // Whether this person has a default at all: a stored row, or
    // an effective value (the minor's Ask). A person with neither
    // is not listed with a status of their own; the panel says so
    // once, about the settings, not about the person.
    public required bool IsSet { get; init; }
}

// A message just sent that left nothing on the record, held for the
// one view that follows the send (see FrontDoorModel.JustRead).
public class JustReadView
{
    public string Words { get; set; } = "";
    public int? SpeakerId { get; set; }
    public string Outcome { get; set; } = "";
    public string Engine { get; set; } = "";
    public string? Model { get; set; }
    public int? ElapsedMs { get; set; }
    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }
    public string? Reply { get; set; }
    public string? OutputJson { get; set; }

    public static string Serialize(JustReadView view) =>
        System.Text.Json.JsonSerializer.Serialize(view);

    public static JustReadView? Deserialize(string json)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<JustReadView>(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}

// One step of the demo's sixty-second path on Home.
public sealed record TourStep(int Number, string Who, string Title, string? Words, string Href, string Detail);

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

    // A message to start from: a Muse Wonder draft (?draft=<review
    // call>&f=<finding>) or a line the house's own rules suggested
    // (?say=...). Either only fills the message box; nothing is sent
    // until the person presses Send.
    [BindProperty(SupportsGet = true)]
    public int? Draft { get; set; }
    [BindProperty(SupportsGet = true, Name = "f")]
    public int? DraftIndex { get; set; }
    [BindProperty(SupportsGet = true)]
    public string? Say { get; set; }
    public string? StartText { get; private set; }
    public bool StartIsWonderDraft { get; private set; }

    // Voice is offered only on a Live instance with a key.
    public bool VoiceAvailable { get; private set; }

    // The two lines under the message box: what is waiting on this
    // person, and the house right now.
    public int NeedsCount { get; private set; }
    public int OpenClaimCount { get; private set; }
    public List<string> LowSupplies { get; private set; } = new();
    public int OpenPlanItems { get; private set; }

    // The newest words in the shared record, newest first: what the house
    // has confirmed, and what is still waiting on a person.
    public List<Event> RecentEvents { get; private set; } = new();

    public string? HouseReply => TempData["HouseReply"] as string;

    // The speaker chosen for the last send: the message box keeps
    // it selected after the send, so a follow-up message is
    // attributed to the same person unless the reader changes it.
    public int? LastSpeakerId => TempData.Peek("LastSpeakerId") as int?;
    public string? PipelineNote => TempData["PipelineNote"] as string;
    public int? ProposedEventId => TempData["ProposedEventId"] as int?;
    public int? ProposedAuthorId => TempData["ProposedAuthorId"] as int?;

    // The words the person just said, echoed back in the reply area
    // so they can see exactly what the house is asking about, and
    // the action-named words on the reply's two buttons.
    public string? ProposedWords => TempData["ProposedWords"] as string;
    public string ConfirmLabel => TempData["ConfirmLabel"] as string ?? "Confirm";
    public string DeclineLabel => TempData["DeclineLabel"] as string ?? "Don't send";

    // The household zone the recent list's times are shown in.
    public TimeZoneInfo HouseholdZone { get; private set; } =
        TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");

    // The model's work, as the page shows it (ActivityService): the
    // newest messages the reader may see, each as a step by step
    // trace, and the ones waiting on a person's answer.
    public List<ActivityItem> Activity { get; private set; } = new();
    public List<ActivityItem> Waiting { get; private set; } = new();

    // The message just sent, or the one whose proposal was just
    // answered, traced for the person who sent or answered it and
    // fenced to that person exactly as every other surface is.
    public ActivityItem? JustNow { get; private set; }

    // What an answer just did, in the house's words ("Sent to
    // Daniel."), shown above the answered message's trace.
    public string? AnswerNote => TempData["AnswerNote"] as string;

    // A message just sent that left nothing on the record (a
    // question, a request to clarify, a refused reading): its words,
    // the engine that read it, and its reply exist only for this one
    // view, carried in the session's own TempData, never stored.
    public JustReadView? JustRead { get; private set; }

    // Kept for the page's shape; Home no longer leads with another
    // visitor's newest message, so this stays empty.
    public ActivityItem? Featured { get; private set; }
    public bool AnyReadings { get; private set; }

    // A person's own front door: big type, a few big choices, and the
    // house's reply as the main thing on the page. Shown for the
    // people the configuration names (Home:SimpleViewFor, Margaret in
    // the demo), or for anyone who asks for it with ?view=simple;
    // ?view=full turns it off. The choice is kept in a cookie.
    public bool SimpleView { get; private set; }
    public string Greeting { get; private set; } = "Hello";

    // The sixty-second path through the demo: each step chooses the
    // person and puts the words in the message box; nothing is sent
    // until the visitor presses Send.
    public List<TourStep> Tour { get; private set; } = new();

    [BindProperty(SupportsGet = true, Name = "view")]
    public string? ViewChoice { get; set; }

    [BindProperty(SupportsGet = true, Name = "step")]
    public int? TourStepNumber { get; set; }

    public const string ViewCookie = "th-view";

    public async Task OnGetAsync()
    {
        // The switcher's choice when the link named no one (see
        // CurrentPerson); a named reader in the link still wins.
        ViewerId = CurrentPerson.Resolve(HttpContext, ViewerId, await _db.Persons.ToListAsync());
        if (HttpContext is not null && ViewChoice is "simple" or "full")
        {
            HttpContext.Response.Cookies.Append(ViewCookie, ViewChoice, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Secure = HttpContext.Request.IsHttps,
                IsEssential = true,
                MaxAge = TimeSpan.FromDays(30),
                Path = "/"
            });
        }
        await LoadAsync();
    }

    public async Task<IActionResult> OnPostTellAsync(int? speakerId, string? telling,
        bool keepPrivate = false, int? heardCallId = null, bool heardEdited = false,
        int? draftCallId = null, int? draftIndex = null, bool draftEdited = false)
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
        // A clip Muse Voice Transcribe heard for this message a moment
        // ago, not yet used: the message then came in by voice.
        var heardCall = heardCallId is int heardId
            ? await _db.ModelCalls.FirstOrDefaultAsync(c => c.Id == heardId
                && c.Purpose == ModelCallPurpose.Hear && c.ReadingId == null
                && c.AtUtc > DateTime.UtcNow.AddMinutes(-15))
            : null;
        var input = new ExtractionInput
        {
            UtteranceId = $"u-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..26],
            Transcript = telling.Trim(),
            SourceType = heardCall is null ? "tap" : "voice",
            Private = keepPrivate,
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

        if (speaker is not null)
        {
            TempData["LastSpeakerId"] = speaker.Id;
        }
        // The engine can fail (the model API refuses, times out, or
        // answers in a shape that will not parse). The page says so in
        // plain words instead of landing on the error page, and says
        // whether anything was saved by looking at the record, not by
        // assuming: the engine is called before anything is written,
        // so a failure there leaves the record untouched.
        var lastEventId = await _db.Events
            .Where(e => e.HouseholdId == household.Id)
            .MaxAsync(e => (int?)e.Id) ?? 0;
        ExtractionResult result;
        try
        {
            result = await _extraction.ProcessAsync(input);
        }
        catch (Exception ex) when (HttpContext?.RequestAborted.IsCancellationRequested != true)
        {
            _logger.LogError(ex, "The house could not read a message from speaker {SpeakerId}", speakerId);
            var wroteSomething = await _db.Events.AnyAsync(e => e.HouseholdId == household.Id && e.Id > lastEventId);
            TempData["JustRead"] = JustReadView.Serialize(new JustReadView
            {
                Words = telling.Trim(),
                SpeakerId = speaker?.Id,
                Outcome = "Failed",
                Engine = _extraction.EngineName,
                Reply = wroteSomething
                    ? "Something went wrong partway through. Check the timeline before you send it again."
                    : EngineFailureNote(ex)
            });
            return RedirectToPage(new { viewerId = speaker?.Id });
        }
        TempData["HouseReply"] = result.HouseReply;
        if (result.Reading is { } linkedReading)
        {
            // The other models' part in this message, linked to its
            // reading so the trace can show it: the clip it was heard
            // from, and the Muse Wonder finding it was drafted from.
            if (heardCall is not null)
            {
                heardCall.ReadingId = linkedReading.Id;
                heardCall.OutputJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["audioDurationMs"] = ReadDuration(heardCall.OutputJson),
                    ["edited"] = heardEdited
                });
                await _db.SaveChangesAsync();
            }
            if (draftCallId is int draftId && draftIndex is int index
                && linkedReading.Outcome == ModelReadingOutcome.Entries)
            {
                await HomeNode.Services.Models.ReviewService.MarkDraftSentAsync(_db, draftId, index, linkedReading.Id, draftEdited);
            }
        }
        if (result.Reading is { } reading)
        {
            if (reading.Outcome == ModelReadingOutcome.Entries)
            {
                TempData["ReadingId"] = reading.Id;
                if (speaker is not null)
                {
                    TempData["ReadingViewer"] = speaker.Id;
                }
            }
            else
            {
                TempData["JustRead"] = JustReadView.Serialize(new JustReadView
                {
                    Words = telling.Trim(),
                    SpeakerId = speaker?.Id,
                    Outcome = reading.Outcome,
                    Engine = reading.Engine,
                    Model = reading.Model,
                    ElapsedMs = reading.ElapsedMs,
                    PromptTokens = reading.PromptTokens,
                    CompletionTokens = reading.CompletionTokens,
                    Reply = result.HouseReply,
                    // The raw output is shown for an answer or a
                    // request to clarify; a refused reading's output
                    // stays in the server log with its errors.
                    OutputJson = reading.Outcome is ModelReadingOutcome.Answered or ModelReadingOutcome.Clarify
                        ? result.ReturnedJson
                        : null
                });
            }
        }
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
            TempData["PipelineNote"] = "Nothing is saved yet. Reply to save it.";
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
                    TempData["DeclineLabel"] = "Don't send";
                    break;
                case EventActionType.CreateReminder:
                    TempData["ConfirmLabel"] = "Set reminder";
                    TempData["DeclineLabel"] = "Don't send";
                    break;
                case EventActionType.NotifyPerson or EventActionType.RelayMessage:
                    var target = pendingAction.TargetPersonId is int targetId
                        ? await _db.Persons.FirstOrDefaultAsync(p => p.Id == targetId)
                        : null;
                    TempData["ConfirmLabel"] = target is null
                        ? "Tell them"
                        : $"Tell {NameWording.First(target.DisplayName)}";
                    TempData["DeclineLabel"] = "Don't send";
                    break;
                default:
                    TempData["ConfirmLabel"] = "Confirm";
                    TempData["DeclineLabel"] = "Don't send";
                    break;
            }
        }
        else if (result.WrittenEvents.Count > 0 && result.PromisesBorn.Count == 0)
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
        // The page comes back as the person who spoke, so the
        // message's own entries show in the activity below under that
        // person's view, not only in the just-sent trace.
        return RedirectToPage(new { viewerId = speaker?.Id });
    }

    // The Test Send control: a remote trigger only. This handler
    // sends no telling text anywhere; it asks the named stand-in to
    // send its own sealed telling through the real ingress, and
    // reports what the stand-in answered, in the stand-in's own
    // terms. If the relay is stopped, the stand-in answers
    // queued-unrecorded and nothing lands here.
    public async Task<IActionResult> OnPostTestSendAsync(string standInName, string? returnTo = null)
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
                $"{personFirst}'s assistant sent a note to the house.",
            "queued-unrecorded" or "unreachable" =>
                $"{personFirst}'s assistant couldn't reach the house. Nothing was sent. Try again in a minute.",
            _ =>
                $"{personFirst}'s assistant isn't connected to this household."
        };
        // The Assistants page carries the control; it asks to come back
        // there. Any other caller comes back to Home, as before.
        return returnTo == "assistants" ? RedirectToPage("/Connections") : RedirectToPage();
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
            TempData["HouseReply"] = "Already answered.";
            return RedirectToPage();
        }
        var ev = await _db.Events.FirstAsync(e => e.Id == eventId);
        var action = await _db.EventActions.FirstOrDefaultAsync(a => a.EventId == eventId);
        var delivery = await _db.AlertDeliveries
            .Include(d => d.ToPerson)
            .FirstOrDefaultAsync(d => d.EventId == eventId);
        // The result line says what actually happened, once, in the
        // shape of the action that was confirmed (ruled Nov 1,
        // 2026): a delivery names where it went, a reminder names
        // when it is set for, and only an action with no visible
        // effect yet keeps the bare word.
        if (delivery?.ToPerson is not null)
        {
            var first = NameWording.First(delivery.ToPerson.DisplayName);
            var viaAssistant = await _db.HouseConnections.AnyAsync(c =>
                c.PersonId == delivery.ToPersonId && c.Standing == ConnectionStanding.Active);
            if (action?.ActionType == EventActionType.RelayMessage)
            {
                TempData["HouseReply"] = viaAssistant
                    ? $"Sent to {first}'s assistant."
                    : $"Sent to {first}.";
            }
            else if (ev.ConfirmationState == ConfirmationState.Done)
            {
                TempData["HouseReply"] = SpokenTemplates.Done;
                TempData["PipelineNote"] = $"Sent to {first}.";
            }
            else
            {
                TempData["HouseReply"] = viaAssistant
                    ? $"Sent to {first}'s assistant."
                    : $"Sent to {first}.";
            }
        }
        else if (action?.ActionType == EventActionType.CreateReminder)
        {
            var remindOn = ReminderText(action);
            TempData["HouseReply"] = remindOn is not null
                ? $"Reminder set for {remindOn}."
                : "Reminder set.";
        }
        else
        {
            TempData["HouseReply"] = "Confirmed.";
        }
        await KeepTraceAsync(eventId, actorPersonId);
        return RedirectToPage(new { viewerId = actorPersonId });
    }

    // After an answer, the page shows the answered message's trace
    // again, now carrying the answer and what it set off, for the
    // person who answered and fenced to them.
    private async Task KeepTraceAsync(int eventId, int actorPersonId)
    {
        var readingId = await new ActivityService(_db).ReadingIdForEventAsync(eventId);
        if (readingId is int id)
        {
            TempData["ReadingId"] = id;
            TempData["ReadingViewer"] = actorPersonId;
            TempData["AnswerNote"] = TempData.Peek("HouseReply") as string;
        }
    }

    // The day words a reminder action carries, read from its stored
    // payload; null when the payload does not name them.
    private static string? ReminderText(EventAction action)
    {
        if (string.IsNullOrWhiteSpace(action.Payload))
        {
            return null;
        }
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(action.Payload);
            return doc.RootElement.TryGetProperty("RemindOnText", out var value)
                && value.ValueKind == System.Text.Json.JsonValueKind.String
                && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public async Task<IActionResult> OnPostDeclineAsync(int eventId, int actorPersonId)
    {
        try
        {
            await _confirmations.DeclineAsync(eventId, actorPersonId, "tap");
        }
        catch (InvalidOperationException)
        {
            TempData["HouseReply"] = "Already answered.";
            return RedirectToPage();
        }
        TempData["HouseReply"] = "Not sent.";
        TempData["PipelineNote"] = "Not sent. It stays in the record, marked Not sent.";
        await KeepTraceAsync(eventId, actorPersonId);
        return RedirectToPage(new { viewerId = actorPersonId });
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
                IsSet = row is not null || effective is not null,
                Label = row is not null
                    ? DefaultLabel(row.Value)
                    : effective == AudienceDefaultValue.Ask
                        ? "Ask me each time (default for under-18s)"
                        : effective is not null
                            ? DefaultLabel(effective.Value)
                            : ""
            });
        }
        if (ViewerId is int reader)
        {
            PendingAsks = await _audience.PendingAsksForAsync(reader, DateTime.UtcNow);
        }

        // The model's work: the newest traces this reader may see,
        // and the ones a person still has to answer.
        var activity = new ActivityService(_db);
        // TempData is absent when the page model runs outside a request
        // (the suites construct it directly); there is then nothing just
        // sent to show.
        var tempData = TempData;
        if (tempData?["ReadingId"] is int justReadingId)
        {
            JustNow = await activity.ForReadingAsync(justReadingId, tempData["ReadingViewer"] as int?);
        }
        if (tempData?["JustRead"] is string justRead)
        {
            JustRead = JustReadView.Deserialize(justRead);
        }
        var feed = await activity.BuildAsync(ViewerId, take: 60);
        Waiting = feed.Items.Where(i => i.NeedsDecision).Take(6).ToList();
        // Home leads with the visitor's own conversation, never with
        // the newest message someone else sent: the demo is shared, and
        // the next visitor should not land on the last one's words.
        Featured = null;
        Activity = feed.Items
            .Where(i => i.Key != JustNow?.Key)
            .Take(4)
            .ToList();
        AnyReadings = feed.Items.Any(i => i.Engine.HasReading);

        var localNow = HouseholdTime.ToLocal(DateTime.UtcNow, HouseholdZone);
        Greeting = localNow.Hour < 12 ? "Good morning" : localNow.Hour < 17 ? "Good afternoon" : "Good evening";
        var viewer = People.FirstOrDefault(p => p.Id == ViewerId);
        var viewCookie = HttpContext?.Request.Cookies[ViewCookie];
        var choice = ViewChoice is "simple" or "full" ? ViewChoice : viewCookie;
        var configured = (HttpContext?.RequestServices?.GetService(typeof(IConfiguration)) as IConfiguration)?
            .GetSection("Home:SimpleViewFor").Get<string[]>() ?? new[] { "Margaret Ellis" };
        SimpleView = viewer is not null && (choice == "simple"
            || (choice != "full" && configured.Any(name => string.Equals(name, viewer.DisplayName, StringComparison.OrdinalIgnoreCase))));
        Tour = BuildTour(People);

        var services = HttpContext?.RequestServices;
        if (services?.GetService(typeof(HomeNode.Services.Models.MetaApi)) is HomeNode.Services.Models.MetaApi api)
        {
            VoiceAvailable = api.Callable;
        }
        if (ViewerId is int needsViewer && services?.GetService(typeof(NeedsYouService)) is NeedsYouService needs)
        {
            NeedsCount = await needs.CountAsync(needsViewer);
        }
        var today = DateOnly.FromDateTime(HouseholdTime.ToLocal(DateTime.UtcNow, HouseholdZone));
        LowSupplies = (await _db.SupplyItems.ToListAsync())
            .Where(i => i.GetOrderByDate(today) <= today)
            .Select(i => i.Name).ToList();
        var openPromises = await _db.Promises.Where(pr => pr.State == PromiseState.Made).ToListAsync();
        var sourceIds = openPromises.Select(pr => pr.SourceEventId).ToList();
        var sources = await _db.Events.Where(e => sourceIds.Contains(e.Id)).ToListAsync();
        OpenClaimCount = openPromises.Count(pr =>
        {
            var source = sources.FirstOrDefault(e => e.Id == pr.SourceEventId);
            return ViewerId is int claimViewer
                ? BriefService.PromiseVisibleTo(pr, source, claimViewer)
                : BriefService.PromiseVisibleWithNoReader(pr, source);
        });
        OpenPlanItems = await _db.PlanElements.CountAsync(e => e.State == PlanElementState.Open
            && e.Plan != null && e.Plan.State != PlanState.Completed && e.Plan.State != PlanState.Dropped
            && e.Plan.State != PlanState.Superseded);

        // The message box's starting words, if the person came here to
        // send a draft. A Muse Wonder draft is offered only to the
        // person the review was made for.
        if (Draft is int draftId && DraftIndex is int draftIndex)
        {
            var call = await _db.ModelCalls.FirstOrDefaultAsync(c => c.Id == draftId
                && c.Purpose == ModelCallPurpose.Review && c.PersonId == ViewerId);
            var finding = call is null ? null : HomeNode.Services.Models.ReviewService.Read(call)?.Findings
                .ElementAtOrDefault(draftIndex);
            if (finding?.DraftMessage is string draftText)
            {
                var to = People.FirstOrDefault(p => p.Id == finding.DraftTo);
                var toFirst = NameWording.First(to?.DisplayName);
                StartText = to is null
                    || draftText.StartsWith("Tell " + toFirst, StringComparison.OrdinalIgnoreCase)
                    || draftText.StartsWith("Ask " + toFirst, StringComparison.OrdinalIgnoreCase)
                    ? draftText
                    : $"Tell {toFirst}: {draftText}";
                StartIsWonderDraft = true;
            }
        }
        else if (!string.IsNullOrWhiteSpace(Say))
        {
            StartText = Say.Trim().Length > 400 ? Say.Trim()[..400] : Say.Trim();
        }
    }

    // The demo's path, built from the household's own people so every
    // link names the right person. A household without these people
    // gets no tour.
    private static List<TourStep> BuildTour(IReadOnlyList<Person> people)
    {
        int? Id(string first) => people.FirstOrDefault(p => NameWording.First(p.DisplayName) == first)?.Id;
        if (Id("Margaret") is not int margaret || Id("Daniel") is not int daniel)
        {
            return new List<TourStep>();
        }
        string Say(int person, string words, int step) =>
            CurrentPerson.SwitchHref(person, $"/?say={Uri.EscapeDataString(words)}&step={step}");
        var steps = new List<TourStep>
        {
            new(1, "Margaret", "Ask for something", "Ask Daniel to pick up milk on his way home",
                Say(margaret, "Ask Daniel to pick up milk on his way home", 1),
                "The house writes it down, then asks before it tells Daniel."),
            new(2, "Daniel", "See it arrive", null,
                CurrentPerson.SwitchHref(daniel, "/Member"),
                "It's in Daniel's inbox, in Margaret's own words."),
            new(3, "Margaret", "Say something private", "My knee hurts when I stand up",
                Say(margaret, "My knee hurts when I stand up", 3),
                "Anything about health is kept from every model and handed to a person."),
            new(4, "Daniel", "Take something on", "I'll get Mia at 3",
                Say(daniel, "I'll get Mia at 3", 4),
                "The house remembers who said they'd do what, until they close it."),
        };
        if (Id("Timmy") is int)
        {
            steps.Add(new(5, "Margaret", "Tell a child", "Ask Timmy to come down for dinner at 6:30",
                Say(margaret, "Ask Timmy to come down for dinner at 6:30", 5),
                "Then open Timmy's inbox: Muse Image draws him a picture card."));
        }
        return steps;
    }

    private static int? ReadDuration(string? json)
    {
        if (json is null)
        {
            return null;
        }
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("audioDurationMs", out var d) && d.TryGetInt32(out var ms) ? ms : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    // Hearing: the browser's push-to-talk clip goes to Muse Voice
    // Transcribe and the words come back for the person to read,
    // change and send. Nothing reaches the record here. The audio is
    // checked first (WavCheck) and never stored; the call is kept as
    // timing and the clip's length only.
    public async Task<IActionResult> OnPostTranscribeAsync(IFormFile? audio, int? speakerId)
    {
        var services = HttpContext?.RequestServices;
        var api = services?.GetService(typeof(HomeNode.Services.Models.MetaApi)) as HomeNode.Services.Models.MetaApi;
        var voice = services?.GetService(typeof(HomeNode.Services.Models.IVoiceClient)) as HomeNode.Services.Models.IVoiceClient;
        if (api is null || voice is null || !api.Callable)
        {
            return new JsonResult(new { ok = false, note = "Voice needs the live Meta Model API, which this instance is not using." });
        }
        var household = await _db.Households.FirstOrDefaultAsync();
        if (household is null || audio is null || audio.Length == 0)
        {
            return new JsonResult(new { ok = false, note = "Nothing was recorded." });
        }
        if (audio.Length > HomeNode.Services.Models.WavCheck.MaxBytes)
        {
            return new JsonResult(new { ok = false, note = "That recording was longer than a minute." });
        }
        byte[] wav;
        using (var buffer = new MemoryStream())
        {
            await audio.CopyToAsync(buffer);
            wav = buffer.ToArray();
        }
        if (HomeNode.Services.Models.WavCheck.Problem(wav) is string problem)
        {
            return new JsonResult(new { ok = false, note = problem });
        }
        var people = await _db.Persons.OrderBy(p => p.Id).ToListAsync();
        var supplies = await _db.SupplyItems.Select(s => s.Name).ToListAsync();
        var keywords = people.Select(p => NameWording.First(p.DisplayName))
            .Concat(supplies).Distinct().Take(40).ToList();
        var call = new ModelCall
        {
            HouseholdId = household.Id,
            Purpose = ModelCallPurpose.Hear,
            Model = api.Models.VoiceModel,
            Engine = "live",
            PersonId = speakerId,
            AtUtc = DateTime.UtcNow
        };
        try
        {
            var heard = await voice.TranscribeAsync(wav, keywords, HttpContext?.RequestAborted ?? default);
            call.ElapsedMs = heard.ElapsedMs;
            call.Outcome = heard.Transcript.Length == 0 ? ModelCallOutcome.Empty : ModelCallOutcome.Done;
            call.OutputJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["audioDurationMs"] = heard.AudioDurationMs
            });
            _db.ModelCalls.Add(call);
            await _db.SaveChangesAsync();
            return new JsonResult(new
            {
                ok = heard.Transcript.Length > 0,
                transcript = heard.Transcript,
                callId = call.Id,
                ms = heard.ElapsedMs,
                // The house's own rule, run on the words before any
                // reader sees them: health-shaped words switch Private
                // on, so no model reads the message.
                sensitive = HomeNode.Services.Extraction.PrivacyRule.WordsArePrivate(heard.Transcript),
                note = heard.Transcript.Length == 0 ? "Muse Voice Transcribe heard no words." : null
            });
        }
        catch (Exception ex) when (HttpContext?.RequestAborted.IsCancellationRequested != true)
        {
            _logger.LogWarning(ex, "Muse Voice Transcribe failed");
            call.Outcome = ModelCallOutcome.Failed;
            _db.ModelCalls.Add(call);
            await _db.SaveChangesAsync();
            return new JsonResult(new { ok = false, note = "Muse Voice Transcribe did not answer. Nothing was sent. Type it instead, or try again." });
        }
    }

    // A failed reading in plain words. The exception's own text goes
    // to the server log only; the page names what kind of failure it
    // was and that nothing was saved.
    private static string EngineFailureNote(Exception ex)
    {
        var message = ex.Message ?? "";
        if (message.Contains("MODEL_API_KEY", StringComparison.Ordinal))
        {
            return "The house could not reach Muse Spark: no Meta Model API key is set on this server. Nothing was saved.";
        }
        var status = System.Text.RegularExpressions.Regex.Match(message, @"Meta Model API returned (\d{3})");
        if (status.Success)
        {
            return status.Groups[1].Value == "429"
                ? "Muse Spark is busy right now. Nothing was saved. Try again in a moment."
                : $"The Meta Model API answered with an error ({status.Groups[1].Value}). Nothing was saved. Try again in a moment.";
        }
        return ex switch
        {
            TaskCanceledException or TimeoutException =>
                "Muse Spark took too long to answer. Nothing was saved. Try again in a moment.",
            HttpRequestException =>
                "The house could not reach the Meta Model API. Nothing was saved. Try again in a moment.",
            System.Text.Json.JsonException =>
                "The model's answer did not come back in the expected shape. Nothing was saved. Try again.",
            _ => "The house could not read that message. Nothing was saved. Try again in a moment."
        };
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
