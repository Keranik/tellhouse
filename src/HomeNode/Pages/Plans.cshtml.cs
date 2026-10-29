using HomeNode.Data;
using HomeNode.Services;
using HomeNode.Services.Extraction;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace HomeNode.Pages;

// Plan assembly: the house proposes, people dispose, in their own
// channels, and the runner's acknowledgement is its own act. The
// dinner element starts from the open decision the record already
// holds (a HouseholdNote whose decision is undecided); both partners
// answer it here; a leave-by element appears only from durations a
// person stated. Nothing on this page is decided by the house.
public class PlansModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly PlanAssemblyService _plans;

    public PlansModel(AppDbContext db, PlanAssemblyService plans)
    {
        _db = db;
        _plans = plans;
    }

    public List<Plan> Plans { get; private set; } = new();
    public List<Person> People { get; private set; } = new();

    [BindProperty(SupportsGet = true)]
    public int? ViewerId { get; set; }
    public List<Event> OpenDecisions { get; private set; } = new();
    public string? Note => TempData["PlanNote"] as string;

    public async Task OnGetAsync()
    {
        Plans = await _db.Plans
            .Include(p => p.Elements).ThenInclude(e => e.Agreements).ThenInclude(a => a.Person)
            .Include(p => p.RunnerPerson)
            .OrderByDescending(p => p.Id)
            .ToListAsync();
        People = await _db.Persons.OrderBy(p => p.Id).ToListAsync();
        var notes = await _db.Events
            .Where(e => e.Type == EventType.HouseholdNote && e.Details != null)
            .OrderByDescending(e => e.CreatedAtUtc)
            .ToListAsync();
        // Open decisions are record content, so they follow the
        // stored audience of the underlying note,
        // through the same rule the brief and timeline use: a
        // Private note appears only for a reader inside its stored
        // audience (its author, for a Personal entry), and with no
        // reader named only house entries show. This list also
        // feeds the dinner form's source selector below, so a
        // narrowed note cannot be picked as a plan source by a
        // reader who may not see it either.
        OpenDecisions = notes
            .Where(e => ExtractionDetailsReader.GetString(e.Details, "DecisionState") == "Undecided")
            .Where(e => ViewerId is int viewer
                ? BriefService.IsVisibleTo(e, viewer)
                : BriefService.IsVisibleWithNoReader(e))
            .ToList();
    }

    public async Task<IActionResult> OnPostCreatePlanAsync(string label, string? anchorTime)
    {
        var household = await _db.Households.FirstAsync();
        DateTime? anchor = null;
        if (TimeOnly.TryParse(anchorTime, out var time))
        {
            anchor = DateTime.Today.Add(time.ToTimeSpan());
        }
        await _plans.CreatePlanAsync(
            household.Id,
            string.IsNullOrWhiteSpace(label) ? "A plan" : label.Trim(),
            anchor);
        TempData["PlanNote"] = "Plan started. Add what it needs, piece by piece.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostAddDinnerAsync(int planId, int? sourceEventId, string mealText, int[] disposerIds)
    {
        await _plans.AddElementAsync(
            planId, PlanElementKind.MealProposal,
            string.IsNullOrWhiteSpace(mealText) ? "Dinner" : mealText.Trim(),
            disposerIds,
            sourceEventIds: sourceEventId is int id ? new[] { id } : null);
        TempData["PlanNote"] = "Dinner is proposed. It settles only when everyone named has agreed.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostAddListAsync(int planId, string listText, int runnerPersonId)
    {
        await _plans.AddElementAsync(
            planId, PlanElementKind.ShoppingList,
            string.IsNullOrWhiteSpace(listText) ? "The list" : listText.Trim(),
            Array.Empty<int>(),
            assigneePersonId: runnerPersonId);
        TempData["PlanNote"] = "The list is on the plan for the runner.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostAddLeaveByAsync(int planId, string durationsText)
    {
        var plan = await _db.Plans.FirstAsync(p => p.Id == planId);
        if (plan.AnchorAtLocal is not { } anchor)
        {
            TempData["PlanNote"] = "This plan has no anchor time, so no leave-by can be worked out.";
            return RedirectToPage();
        }
        var durations = new List<(string, int)>();
        foreach (var line in (durationsText ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(',');
            if (parts.Length == 2 && int.TryParse(parts[1].Trim(), out var minutes))
            {
                durations.Add((parts[0].Trim(), minutes));
            }
        }
        var element = await _plans.AddLeaveByAsync(planId, anchor, durations);
        TempData["PlanNote"] = element is null
            ? "No leave-by was added: it is worked out only from durations a person stated."
            : "Leave-by added, from the stated durations only.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDisposeAsync(int elementId, int personId, string disposal, string? counterText)
    {
        await _plans.RecordDisposalAsync(elementId, personId, Enum.Parse<PlanDisposal>(disposal), counterText);
        TempData["PlanNote"] = "Answer recorded. An element settles only when everyone named has agreed in the same round.";
        return RedirectToPage();
    }

    // A person re-times an entry, in their own new words: the
    // element's words change on the record, and the new wording is
    // routed to the person the entry affects (its assignee) and to
    // nobody else, through the same delivery rows the member views
    // read. Blank words change nothing.
    public async Task<IActionResult> OnPostRetimeAsync(int elementId, string? newText, int actorPersonId)
    {
        if (!string.IsNullOrWhiteSpace(newText))
        {
            await _plans.RetimeElementAsync(elementId, newText.Trim(), actorPersonId);
            TempData["PlanNote"] = "The entry was re-timed in the new words, and the new wording went to the person it affects.";
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostAcknowledgeAsync(int planId, int runnerPersonId)
    {
        try
        {
            await _plans.AcknowledgeAsync(planId, runnerPersonId);
            TempData["PlanNote"] = "The runner has the plan. Prepared and routed are not done; the plan says what is still owed.";
        }
        catch (InvalidOperationException ex)
        {
            TempData["PlanNote"] = ex.Message;
        }
        return RedirectToPage();
    }
}
