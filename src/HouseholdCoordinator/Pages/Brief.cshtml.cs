using HouseholdCoordinator.Data;
using HouseholdCoordinator.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace HouseholdCoordinator.Pages;

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

    public async Task OnGetAsync()
    {
        People = await _db.Persons.OrderBy(p => p.Id).ToListAsync();
        var id = PersonId ?? People.FirstOrDefault()?.Id;
        if (id is not int viewerId)
        {
            return;
        }
        Viewer = People.First(p => p.Id == viewerId);
        Lines = await _briefs.BuildBriefAsync(viewerId, DateOnly.FromDateTime(DateTime.Now));
    }

    public async Task<IActionResult> OnPostLookupAsync(int askerPersonId, int subjectPersonId)
    {
        TempData["LookupAnswer"] = await _lookup.AnswerWhereaboutsAsync(askerPersonId, subjectPersonId);
        return RedirectToPage(new { PersonId = askerPersonId });
    }
}
