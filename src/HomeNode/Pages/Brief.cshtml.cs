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

    // The same reader's view of the house's activity (ActivityService):
    // the newest entries this person may see, traced, and the ones
    // still waiting on a person's answer.
    public List<ActivityItem> Recent { get; private set; } = new();
    public List<ActivityItem> Waiting { get; private set; } = new();
    public TimeZoneInfo HouseholdZone { get; private set; } = HouseholdTime.ZoneFor(null);

    // "What needs attention": the house's own checks (no model, always)
    // and Muse Wonder's review of the shared record, when asked for.
    public List<HomeNode.Services.Models.ReviewFinding> HouseFindings { get; private set; } = new();
    public ModelCall? WonderCall { get; private set; }
    public HomeNode.Services.Models.WonderResult? Wonder { get; private set; }
    public bool WonderAvailable { get; private set; }
    public int WonderMinutes { get; private set; } = 10;
    public Dictionary<int, (string Words, string Who)> Cited { get; private set; } = new();
    public Dictionary<int, string> SupplyNames { get; private set; } = new();
    public List<Plan> OpenPlans { get; private set; } = new();

    // The shelf check: SAM 3.1 counts the tracked supplies in a photo.
    public bool ShelfAvailable { get; private set; }
    public ModelCall? ShelfCall { get; private set; }
    public HomeNode.Services.Models.ShelfResult? Shelf { get; private set; }
    public List<(string Name, string Prompt)> ShelfAsks { get; private set; } = new();
    public List<string> ShelfNeverAsked { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync()
    {
        People = await _db.Persons.OrderBy(p => p.Id).ToListAsync();
        PersonId = CurrentPerson.Resolve(HttpContext, PersonId, People);
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
        HouseholdZone = householdZone;
        var feed = await new ActivityService(_db).BuildAsync(viewerId, take: 60);
        Waiting = feed.Items.Where(i => i.NeedsDecision).Take(5).ToList();
        Recent = feed.Items.Take(8).ToList();
        OpenPlans = await _db.Plans.Include(p => p.Elements)
            .Where(p => p.State != PlanState.Completed && p.State != PlanState.Dropped && p.State != PlanState.Superseded)
            .OrderByDescending(p => p.Id).Take(3).ToListAsync();

        // The review needs the request's services; a page model built
        // outside a request (the suites) has none, and shows no review.
        if (HttpContext?.RequestServices.GetService(typeof(HomeNode.Services.Models.ReviewService))
            is HomeNode.Services.Models.ReviewService reviews)
        {
            WonderAvailable = reviews.WonderAvailable;
            if (HttpContext.RequestServices.GetService(typeof(HomeNode.Services.Models.MetaApi)) is HomeNode.Services.Models.MetaApi api)
            {
                WonderMinutes = api.Models.ReviewMinutesBetween;
            }
            var slice = await reviews.SliceForAsync(viewerId);
            if (slice is not null)
            {
                HouseFindings = HomeNode.Services.Models.ReviewService.HouseFindings(slice, viewerId);
                foreach (var ev in slice.Events)
                {
                    var who = People.FirstOrDefault(p => p.Id == ev.AuthorPersonId);
                    Cited[ev.Id] = (ev.Body, NameWording.First(who?.DisplayName));
                }
                SupplyNames = slice.Supplies.ToDictionary(x => x.Id, x => x.Name);
            }
            WonderCall = await reviews.LatestWonderAsync(viewerId);
            Wonder = WonderCall is null ? null : HomeNode.Services.Models.ReviewService.Read(WonderCall);
        }
        if (HttpContext?.RequestServices.GetService(typeof(HomeNode.Services.Models.ShelfCheckService))
            is HomeNode.Services.Models.ShelfCheckService shelf
            && HttpContext.RequestServices.GetService(typeof(HomeNode.Services.Models.MetaApi)) is HomeNode.Services.Models.MetaApi shelfApi)
        {
            ShelfAvailable = shelf.Available;
            var (asked, notAsked) = HomeNode.Services.Models.ShelfCheckService.PromptsFor(
                await _db.SupplyItems.Where(x => x.HouseholdId == Viewer.HouseholdId).ToListAsync(), shelfApi.Models);
            ShelfAsks = asked.Select(a => (a.Supply.Name, a.Prompt)).ToList();
            ShelfNeverAsked = notAsked;
            ShelfCall = await shelf.LatestAsync(Viewer.HouseholdId);
            Shelf = ShelfCall is null ? null : HomeNode.Services.Models.ShelfCheckService.Read(ShelfCall);
        }
        return Page();
    }

    // Check the shelf: the photo (already shrunk by the browser) goes to
    // SAM 3.1 once per tracked supply. Nothing is written to the record.
    public async Task<IActionResult> OnPostShelfAsync(IFormFile? photo, int personId, int width, int height)
    {
        var services = HttpContext?.RequestServices;
        if (services?.GetService(typeof(HomeNode.Services.Models.ShelfCheckService)) is not HomeNode.Services.Models.ShelfCheckService shelf
            || !shelf.Available || photo is null || photo.Length == 0
            || photo.Length > HomeNode.Services.Models.ShelfCheckService.MaxBytes)
        {
            return RedirectToPage(new { PersonId = personId });
        }
        var person = await _db.Persons.FirstOrDefaultAsync(p => p.Id == personId);
        if (person is null)
        {
            return RedirectToPage();
        }
        byte[] bytes;
        using (var buffer = new MemoryStream())
        {
            await photo.CopyToAsync(buffer);
            bytes = buffer.ToArray();
        }
        if (HomeNode.Services.Models.ShelfCheckService.MediaType(bytes) is not string media)
        {
            return RedirectToPage(new { PersonId = personId });
        }
        await shelf.CheckAsync(person.HouseholdId, personId, bytes, media, width, height, HttpContext!.RequestAborted);
        return Redirect($"/Brief?PersonId={personId}#shelf");
    }

    // Ask Muse Wonder to look over the shared record as this person.
    // A review from the last few minutes is shown again rather than
    // asked again. Nothing in a review is acted on by the house.
    public async Task<IActionResult> OnPostReviewAsync(int personId)
    {
        if (HttpContext?.RequestServices.GetService(typeof(HomeNode.Services.Models.ReviewService))
            is HomeNode.Services.Models.ReviewService reviews && reviews.WonderAvailable
            && await _db.Persons.AnyAsync(p => p.Id == personId))
        {
            var slice = await reviews.SliceForAsync(personId);
            if (slice is not null)
            {
                await reviews.AskWonderAsync(slice, personId, HttpContext.RequestAborted);
            }
        }
        return RedirectToPage(new { PersonId = personId });
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
