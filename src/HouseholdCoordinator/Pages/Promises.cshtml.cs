using HouseholdCoordinator.Data;
using HouseholdCoordinator.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace HouseholdCoordinator.Pages;

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
        var promise = await _db.Promises.FirstAsync(p => p.Id == promiseId);
        var closer = await _db.Persons.FirstAsync(p => p.Id == closerPersonId);
        var closingEvent = new Event
        {
            HouseholdId = promise.HouseholdId,
            Type = EventType.HouseholdNote,
            Body = string.IsNullOrWhiteSpace(closingWords)
                ? $"{closer.DisplayName.Split(' ')[0]} closed the promise."
                : closingWords.Trim(),
            SubjectPersonId = promise.PromisorPersonId,
            AuthorPersonId = closer.Id,
            CreatedAtUtc = DateTime.UtcNow,
            SensitivityClass = SensitivityClass.Household,
            AudienceRule = """{"Class":"Household"}"""
        };
        await _confirmations.WriteStatementAsync(closingEvent, "tap");
        await _confirmations.ClosePromiseAsync(promiseId, closingEvent.Id, closerPersonId, outcome, "tap");
        TempData["PromiseNote"] = "Closed by a person, in their words, and linked to the promise.";
        return RedirectToPage();
    }
}
