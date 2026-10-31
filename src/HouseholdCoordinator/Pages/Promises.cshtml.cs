using HouseholdCoordinator.Data;
using HouseholdCoordinator.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace HouseholdCoordinator.Pages;

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

    [BindProperty(SupportsGet = true)]
    public int? ViewerId { get; set; }
    public string? Note => TempData["PromiseNote"] as string;

    public async Task OnGetAsync()
    {
        // The list follows the stored audience of each promise's
        // source telling, through the same rule the brief and
        // the timeline use: a reader sees
        // the promises that reader may see, and with no reader
        // named only house-audience promises show. A promise whose
        // source telling cannot be found fails closed to its maker
        // and its promisee, the narrowest reading its own fields
        // support. The closing forms post back with no reader
        // named; closing itself is governed by the confirmation
        // service, not by this list.
        var all = await _db.Promises
            .Include(p => p.PromisorPerson)
            .Include(p => p.PromiseePerson)
            .Include(p => p.ClosedByPerson)
            .OrderByDescending(p => p.Id)
            .ToListAsync();
        var sources = await _db.Events
            .Where(e => all.Select(p => p.SourceEventId).Contains(e.Id))
            .ToDictionaryAsync(e => e.Id);
        Promises = all
            .Where(p => PromiseVisible(p, sources.GetValueOrDefault(p.SourceEventId)))
            .ToList();
        People = await _db.Persons.OrderBy(p => p.Id).ToListAsync();
    }

    // A promise's visibility is the stored audience of the telling
    // that borned it, read exactly as every other event is read:
    // a commitment is stored Named to its maker and its promisee,
    // so a promise with no promisee is visible to its maker alone
    // under that rule, never to everyone by virtue of having no
    // promisee. With no reader named, only a promise whose source
    // telling is a shared class (Open or Household) shows at all.
    private bool PromiseVisible(Promise promise, Event? sourceEvent)
    {
        if (ViewerId is int viewer)
        {
            return sourceEvent is not null
                ? BriefService.IsVisibleTo(sourceEvent, viewer)
                : promise.PromisorPersonId == viewer
                    || promise.PromiseePersonId == viewer;
        }
        return sourceEvent is not null
            && sourceEvent.SensitivityClass is SensitivityClass.Open or SensitivityClass.Household;
    }

    public async Task<IActionResult> OnPostCloseAsync(int promiseId, int closerPersonId, string outcome, string? closingWords)
    {
        var promise = await _db.Promises.FirstAsync(p => p.Id == promiseId);
        var closer = await _db.Persons.FirstAsync(p => p.Id == closerPersonId);
        var closingEvent = new Event
        {
            HouseholdId = promise.HouseholdId,
            Type = EventType.HouseholdNote,
            Body = string.IsNullOrWhiteSpace(closingWords)
                ? $"{closer.DisplayName.Split(' ')[0]} closed the claim."
                : closingWords.Trim(),
            SubjectPersonId = promise.PromisorPersonId,
            AuthorPersonId = closer.Id,
            CreatedAtUtc = DateTime.UtcNow,
            SensitivityClass = SensitivityClass.Household,
            AudienceRule = """{"Class":"Household"}"""
        };
        await _confirmations.WriteStatementAsync(closingEvent, "tap");
        await _confirmations.ClosePromiseAsync(promiseId, closingEvent.Id, closerPersonId, outcome, "tap");
        TempData["PromiseNote"] = "Settled by a person, in their words, and linked to the claim.";
        return RedirectToPage();
    }
}
