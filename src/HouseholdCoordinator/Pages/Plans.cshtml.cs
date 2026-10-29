using HouseholdCoordinator.Data;
using HouseholdCoordinator.Services;
using HouseholdCoordinator.Services.Extraction;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace HouseholdCoordinator.Pages;

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
    private readonly DinnerProvenanceService _dinner;

    public PlansModel(AppDbContext db, PlanAssemblyService plans)
    {
        _db = db;
        _plans = plans;
        _dinner = new DinnerProvenanceService(db, plans);
    }

    public List<Plan> Plans { get; private set; } = new();
    public List<Person> People { get; private set; } = new();
    public List<Event> OpenDecisions { get; private set; } = new();

    // Dinner provenance, surfaced: the stated usual meals and the
    // stated fridge contents, each with its author and its day. The
    // proposal forms below draw only from this list.
    public List<ProvenanceItem> ProvenanceItems { get; private set; } = new();

    // Each meal element's tellings, by element id, resolved from the
    // element's SourceEventIds. A meal element with no entry here
    // has no telling behind it and is not shown as a proposal.
    public Dictionary<int, List<ProvenanceItem>> ElementProvenance { get; private set; } = new();
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
        OpenDecisions = notes
            .Where(e => ExtractionDetailsReader.GetString(e.Details, "DecisionState") == "Undecided")
            .ToList();
        var household = await _db.Households.FirstOrDefaultAsync();
        if (household is not null)
        {
            ProvenanceItems = await _dinner.GetItemsAsync(household.Id);
            var byEvent = ProvenanceItems.ToDictionary(i => i.SourceEventId);
            foreach (var element in Plans.SelectMany(p => p.Elements))
            {
                if (element.Kind != PlanElementKind.MealProposal || element.SourceEventIds is null)
                {
                    continue;
                }
                List<int> sourceIds;
                try
                {
                    sourceIds = System.Text.Json.JsonSerializer.Deserialize<List<int>>(element.SourceEventIds) ?? new List<int>();
                }
                catch (System.Text.Json.JsonException)
                {
                    continue;
                }
                var resolved = sourceIds.Where(byEvent.ContainsKey).Select(id => byEvent[id]).ToList();
                if (resolved.Count > 0)
                {
                    ElementProvenance[element.Id] = resolved;
                }
            }
        }
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

    // A dinner proposal is built only from tellings: the meal words
    // must name a stated usual meal (or the chosen source must be
    // one of the provenance tellings), and the proposal element
    // carries that telling as its source. Words with no telling
    // behind them are refused here, on the surface, and no element
    // is written.
    public async Task<IActionResult> OnPostAddDinnerAsync(int planId, int? sourceEventId, string mealText, int[] disposerIds)
    {
        var plan = await _db.Plans.FirstAsync(p => p.Id == planId);
        var items = await _dinner.GetItemsAsync(plan.HouseholdId);
        ProvenanceItem? meal = null;
        if (sourceEventId is int id)
        {
            meal = items.FirstOrDefault(i => i.SourceEventId == id && i.Kind == DinnerProvenanceService.UsualMealKind);
        }
        meal ??= items.FirstOrDefault(i =>
            i.Kind == DinnerProvenanceService.UsualMealKind
            && i.ItemName.Equals((mealText ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
        if (meal is null)
        {
            TempData["PlanNote"] = "That dinner has no telling behind it in this house, so it was not proposed. The house proposes only from stated usual meals.";
            return RedirectToPage();
        }
        await _dinner.ProposeDinnerAsync(planId, new[] { meal.SourceEventId }, disposerIds);
        TempData["PlanNote"] = "Dinner is proposed, from a telling on the record. It settles only when everyone named has agreed.";
        return RedirectToPage();
    }

    // The provenance form: a proposal drawn from the listed
    // tellings themselves (a stated usual meal, plus any stated
    // fridge contents it draws on), each carried on the element as
    // its source.
    public async Task<IActionResult> OnPostProposeFromProvenanceAsync(int planId, int[] sourceEventIds, int[] disposerIds)
    {
        try
        {
            await _dinner.ProposeDinnerAsync(planId, sourceEventIds, disposerIds);
            TempData["PlanNote"] = "Dinner is proposed, from the tellings chosen. It settles only when everyone named has agreed.";
        }
        catch (InvalidOperationException ex)
        {
            TempData["PlanNote"] = ex.Message;
        }
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
