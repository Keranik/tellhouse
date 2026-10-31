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

    [BindProperty(SupportsGet = true)]
    public int? ViewerId { get; set; }
    public string? Note => TempData["PromiseNote"] as string;

    public async Task OnGetAsync()
    {
        // The list follows the stored audience of each promise's
        // source telling, through the same rule the
        // brief uses: a reader sees the promises that reader may
        // see, and with no reader named only house-audience
        // promises show. The closing forms below post back with no
        // reader named; closing itself is governed by the
        // confirmation service, not by this list.
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
            .Where(p => ViewerId is int viewer
                ? BriefService.PromiseVisibleTo(p, sources.GetValueOrDefault(p.SourceEventId), viewer)
                : BriefService.PromiseVisibleWithNoReader(p, sources.GetValueOrDefault(p.SourceEventId)))
            .ToList();
        People = await _db.Persons.OrderBy(p => p.Id).ToListAsync();
    }

    public async Task<IActionResult> OnPostCloseAsync(int promiseId, int closerPersonId, string outcome, string? closingWords)
    {
        // A promise closes in the closer's own words, so the words are
        // required: with no words the close does not happen, no event
        // is written, and no promise state changes. The house never
        // writes closing words of its own in a person's name.
        if (string.IsNullOrWhiteSpace(closingWords))
        {
            TempData["PromiseNote"] = "A claim is settled in the closer's own words, so the words are needed.";
            return RedirectToPage();
        }
        var promise = await _db.Promises.FirstAsync(p => p.Id == promiseId);
        // The page mirrors the Released rule before writing anything,
        // so a refused release leaves no closing event behind. The
        // governing enforcement sits in ConfirmationService, which
        // every caller goes through; this check only keeps the page
        // from recording a close that service will refuse.
        if (outcome == nameof(PromiseState.Released)
            && closerPersonId != promise.PromiseePersonId
            && !(promise.PromiseePersonId is null && closerPersonId == promise.PromisorPersonId))
        {
            TempData["PromiseNote"] = ConfirmationService.ReleasedOnlyByPromiseeMessage;
            return RedirectToPage();
        }
        var closingEvent = new Event
        {
            HouseholdId = promise.HouseholdId,
            Type = EventType.HouseholdNote,
            Body = closingWords.Trim(),
            SubjectPersonId = promise.PromisorPersonId,
            AuthorPersonId = closerPersonId,
            CreatedAtUtc = DateTime.UtcNow,
            SensitivityClass = SensitivityClass.Household,
            AudienceRule = """{"Class":"Household"}"""
        };
        await _confirmations.WriteStatementAsync(closingEvent, "tap");
        try
        {
            await _confirmations.ClosePromiseAsync(promiseId, closingEvent.Id, closerPersonId, outcome, "tap");
            TempData["PromiseNote"] = "Settled by a person, in their words, and linked to the claim.";
        }
        catch (InvalidOperationException ex)
        {
            TempData["PromiseNote"] = ex.Message;
        }
        return RedirectToPage();
    }
}
