using HomeNode.Data;
using HomeNode.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace HomeNode.Pages;

// The household's claims, as records in their own right: who took
// what on, in their words, and how it settled, listed as the named
// reader may see them under each claim's stored audience. Settling
// a claim happens here the way settling works: a person says the
// closing words into the record (a HouseholdNote of their own), and
// the dispatcher links that telling to the claim. The house never
// settles a claim on a clock, and an open claim past its time reads
// "still open"; nothing on this surface reads "broken".
public class PromisesModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly ConfirmationService _confirmations;

    public PromisesModel(AppDbContext db, ConfirmationService confirmations)
    {
        _db = db;
        _confirmations = confirmations;
    }

    public List<Promise> Promises { get; private set; } = new();
    public List<Person> People { get; private set; } = new();

    // The household zone claim times are shown in.
    public TimeZoneInfo HouseholdZone { get; private set; } =
        TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");

    [BindProperty(SupportsGet = true)]
    public int? ViewerId { get; set; }
    public string? Note => TempData["PromiseNote"] as string;

    // Where each listed claim came from: its source entry (the message
    // it was read out of), and that message's trace when one is on
    // file, so the card can say what read the words and link to it.
    public Dictionary<int, Event> Sources { get; private set; } = new();
    public Dictionary<int, ActivityItem> SourceTraces { get; private set; } = new();

    public async Task OnGetAsync()
    {
        // The list follows the stored audience of each promise's
        // source telling, through the same rule the
        // brief uses: a reader sees the promises that reader may
        // see, and with no reader named only house-audience
        // promises show. The closing forms below post back with no
        // reader named; closing itself is governed by the
        // confirmation service, not by this list.
        var household = await _db.Households.FirstOrDefaultAsync();
        if (household is not null)
        {
            HouseholdZone = HouseholdTime.ZoneFor(household.TimeZoneId);
        }
        var all = await _db.Promises
            .Include(p => p.PromisorPerson)
            .Include(p => p.PromiseePerson)
            .Include(p => p.ClosedByPerson)
            .Include(p => p.ClosedByEvent)
            .OrderByDescending(p => p.Id)
            .ToListAsync();
        var sources = await _db.Events
            .Where(e => all.Select(p => p.SourceEventId).Contains(e.Id))
            .ToDictionaryAsync(e => e.Id);
        Promises = all
            .Where(p => ViewerId is int viewer
                ? BriefService.PromiseVisibleTo(p, sources.GetValueOrDefault(p.SourceEventId), viewer)
                : BriefService.PromiseVisibleWithNoReader(p, sources.GetValueOrDefault(p.SourceEventId)))
            .ToList();
        People = await _db.Persons.OrderBy(p => p.Id).ToListAsync();
        Sources = sources;
        // The traces behind the listed claims, under the same reader:
        // a claim the reader may see has a source the reader may see,
        // and a source whose message the reader may not see in full
        // simply has no trace link.
        var feed = await new ActivityService(_db).BuildAsync(ViewerId, take: 300);
        foreach (var item in feed.Items)
        {
            foreach (var entry in item.Entries)
            {
                SourceTraces[entry.Event.Id] = item;
            }
        }
    }

    public async Task<IActionResult> OnPostCloseAsync(int promiseId, int? closerPersonId, string outcome, string? closingWords)
    {
        // With no reader named there is nobody to default the
        // closer to: an unnamed closer is refused, never silently
        // read as the first person in the list.
        if (closerPersonId is not int closerId)
        {
            TempData["PromiseNote"] = "Choose who closed the claim.";
            return RedirectToPage();
        }
        // A promise closes in the closer's own words, so the words are
        // required: with no words the close does not happen, no event
        // is written, and no promise state changes. The house never
        // writes closing words of its own in a person's name.
        if (string.IsNullOrWhiteSpace(closingWords))
        {
            TempData["PromiseNote"] = "Add a few words about how it went.";
            return RedirectToPage();
        }
        var promise = await _db.Promises.FirstAsync(p => p.Id == promiseId);
        // The page mirrors the Released rule before writing anything,
        // so a refused release leaves no closing event behind. The
        // governing enforcement sits in ConfirmationService, which
        // every caller goes through; this check only keeps the page
        // from recording a close that service will refuse.
        if (outcome == nameof(PromiseState.Released)
            && closerId != promise.PromiseePersonId
            && !(promise.PromiseePersonId is null && closerId == promise.PromisorPersonId))
        {
            var promisee = await _db.Persons.FirstOrDefaultAsync(p => p.Id == promise.PromiseePersonId);
            var promisor = await _db.Persons.FirstOrDefaultAsync(p => p.Id == promise.PromisorPersonId);
            TempData["PromiseNote"] = promisee is not null
                ? $"Only {NameWording.First(promisee.DisplayName)}, who it was for, can mark this as no longer needed."
                : $"Only {NameWording.First(promisor?.DisplayName)}, who took it on, can mark this as no longer needed.";
            return RedirectToPage();
        }
        var closingEvent = new Event
        {
            HouseholdId = promise.HouseholdId,
            Type = EventType.HouseholdNote,
            Body = closingWords.Trim(),
            SubjectPersonId = promise.PromisorPersonId,
            AuthorPersonId = closerId,
            CreatedAtUtc = DateTime.UtcNow,
            SensitivityClass = SensitivityClass.Household,
            AudienceRule = """{"Class":"Household"}"""
        };
        await _confirmations.WriteStatementAsync(closingEvent, "tap");
        try
        {
            await _confirmations.ClosePromiseAsync(promiseId, closingEvent.Id, closerId, outcome, "tap");
            TempData["PromiseNote"] = "Claim closed.";
        }
        catch (InvalidOperationException ex)
        {
            // The service's own wording never reaches the page: a
            // claim that has already closed gets the plain sentence,
            // and every other refusal gets the generic one.
            TempData["PromiseNote"] = ex.Message.Contains("no longer open")
                ? "This claim is already closed."
                : "That claim could not be closed.";
        }
        return RedirectToPage();
    }
}
