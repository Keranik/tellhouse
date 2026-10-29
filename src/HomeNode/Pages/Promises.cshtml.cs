using HomeNode.Data;
using HomeNode.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace HomeNode.Pages;

// The household's promises, as first-class records: who promised what,
// in their words, and how it closed. Closing a promise happens here
// the way closing works: a person says the closing words into
// the record (a HouseholdNote of their own), and the dispatcher links
// that telling to the promise. The house never closes a promise on a
// clock, and an open promise past its time reads "still open", never
// "broken".
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
    public string? Note => TempData["PromiseNote"] as string;

    public async Task OnGetAsync()
    {
        Promises = await _db.Promises
            .Include(p => p.PromisorPerson)
            .Include(p => p.PromiseePerson)
            .Include(p => p.ClosedByPerson)
            .OrderByDescending(p => p.Id)
            .ToListAsync();
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
            TempData["PromiseNote"] = "A promise closes in the closer's own words, so the words are needed.";
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
            TempData["PromiseNote"] = "Closed by a person, in their words, and linked to the promise.";
        }
        catch (InvalidOperationException ex)
        {
            TempData["PromiseNote"] = ex.Message;
        }
        return RedirectToPage();
    }
}
