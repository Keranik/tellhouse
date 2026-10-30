using HomeNode.Data;
using HomeNode.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace HomeNode.Pages;

// One person's brief: what the record holds for them, filtered by the
// audience stored on each entry when it was written. Asking the house
// where someone is goes through the fenced lookup, never through the
// raw record.
public class BriefModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly BriefService _briefs;
    private readonly LookupService _lookup;

    public BriefModel(AppDbContext db, BriefService briefs, LookupService lookup)
    {
        _db = db;
        _briefs = briefs;
        _lookup = lookup;
    }

    public List<Person> People { get; private set; } = new();
    public Person? Viewer { get; private set; }
    public List<BriefLine> Lines { get; private set; } = new();

    [BindProperty(SupportsGet = true)]
    public int? PersonId { get; set; }

    public string? LookupAnswer => TempData["LookupAnswer"] as string;

    public async Task<IActionResult> OnGetAsync()
    {
        People = await _db.Persons.OrderBy(p => p.Id).ToListAsync();
        // A brief belongs to one named person. With no reader
        // named there is no brief at all: the page shows the
        // reader picker and a plain note, never the first person's
        // brief. That default handed a bare visit the first
        // person's full view, Restricted entries included.
        if (PersonId is not int viewerId)
        {
            return Page();
        }
        // An id that names nobody in the house is a 404, never a
        // 500: ordinary bad input gets an ordinary
        // answer.
        Viewer = People.FirstOrDefault(p => p.Id == viewerId);
        if (Viewer is null)
        {
            return NotFound();
        }
        // "Today" for the brief is the household's
        // today, not the server clock's. The supply order-by and
        // run-out lines in the brief count days on the household
        // calendar, the same zone the service uses for due times.
        var household = await _db.Households.FirstOrDefaultAsync(h => h.Id == Viewer.HouseholdId);
        TimeZoneInfo householdZone;
        try
        {
            householdZone = TimeZoneInfo.FindSystemTimeZoneById(household?.TimeZoneId ?? "America/Chicago");
        }
        catch (TimeZoneNotFoundException)
        {
            householdZone = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
        }
        Lines = await _briefs.BuildBriefAsync(viewerId,
            DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, householdZone)));
        return Page();
    }

    public async Task<IActionResult> OnPostLookupAsync(int askerPersonId, int subjectPersonId)
    {
        TempData["LookupAnswer"] = await _lookup.AnswerWhereaboutsAsync(askerPersonId, subjectPersonId);
        return RedirectToPage(new { PersonId = askerPersonId });
    }
}
