using HouseholdCoordinator.Data;
using HouseholdCoordinator.Services;
using HouseholdCoordinator.Services.Extraction;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace HouseholdCoordinator.Pages;

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
public class FrontDoorModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly ExtractionContextBuilder _contextBuilder;
    private readonly ExtractionService _extraction;
    private readonly ConfirmationService _confirmations;

    public FrontDoorModel(
        AppDbContext db,
        ExtractionContextBuilder contextBuilder,
        ExtractionService extraction,
        ConfirmationService confirmations)
    {
        _db = db;
        _contextBuilder = contextBuilder;
        _extraction = extraction;
        _confirmations = confirmations;
    }

    public Household? Household { get; private set; }
    public List<Person> People { get; private set; } = new();

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
            TempData["PipelineNote"] =
                "The pipeline refused that telling: " + string.Join(" ", result.Validation.Errors);
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
    }
}
