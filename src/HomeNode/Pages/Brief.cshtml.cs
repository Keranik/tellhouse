using HomeNode.Data;
using HomeNode.Services;
using HomeNode.Services.Extraction;
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
        // A briefing belongs to one named person. With no reader
        // named, the page defaults to Daniel, the household's
        // route person, so it never opens empty; naming anyone
        // else in the picker shows that person's briefing instead.
        if (PersonId is not int viewerId)
        {
            var daniel = People.FirstOrDefault(
                p => p.DisplayName.StartsWith("Daniel", StringComparison.Ordinal));
            if (daniel is null)
            {
                return Page();
            }
            viewerId = daniel.Id;
            PersonId = daniel.Id;
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
        var parts = await _lookup.AnswerWhereaboutsPartsAsync(askerPersonId, subjectPersonId);
        var asker = await _db.Persons.FirstOrDefaultAsync(p => p.Id == askerPersonId);
        var askerHousehold = asker is null
            ? null
            : await _db.Households.FirstOrDefaultAsync(h => h.Id == asker.HouseholdId);
        TimeZoneInfo zone;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(askerHousehold?.TimeZoneId ?? "America/Chicago");
        }
        catch (TimeZoneNotFoundException)
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
        }
        var todayLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone);
        TempData["LookupAnswer"] = parts is { } answer
            ? $"{answer.FirstName} · {(answer.StatedLocal.Date == todayLocal.Date ? $"{answer.StatedLocal:h:mm tt}" : $"{answer.StatedLocal:MMM d, h:mm tt}")} · \"{answer.Body}\""
            : SpokenTemplates.DoNotKnow;
        return RedirectToPage(new { PersonId = askerPersonId });
    }
}
