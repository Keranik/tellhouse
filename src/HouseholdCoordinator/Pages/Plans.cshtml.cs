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

    // The record events the plan elements and provenance items on
    // this page were built from, loaded once per read so each can
    // be fenced by the audience stored on its sources.
    private readonly Dictionary<int, Event> _sourceEvents = new();

    [BindProperty(SupportsGet = true)]
    public int? ViewerId { get; set; }
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

    // Whether a record event may be shown to this page's reader,
    // under the audience stored on it at write time: the same rule
    // the brief and the timeline use, and with no reader named only
    // the house's shared entries (Open and Household classes) show.
    private bool SourceVisible(Event source) =>
        ViewerId is int viewer
            ? BriefService.IsVisibleTo(source, viewer)
            : source.SensitivityClass is SensitivityClass.Open or SensitivityClass.Household;

    private static List<int> ElementSourceIds(PlanElement element)
    {
        if (string.IsNullOrWhiteSpace(element.SourceEventIds))
        {
            return new List<int>();
        }
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<int>>(element.SourceEventIds)
                ?? new List<int>();
        }
        catch (System.Text.Json.JsonException)
        {
            return new List<int>();
        }
    }

    // Whether this page's reader may see one plan element's text.
    // An element's words stand on the record it was built from, so
    // they follow the stored audience of every source event: an
    // element derived from a narrowed source shows only to readers
    // inside that source's audience, and to no reader when any
    // source is narrowed. An element with no source event was
    // written by hand on this page and behaves as it always has.
    // The view renders nothing of a hidden element: its agreements
    // and dispose form and its re-time form with the element's
    // words pre-filled included.
    public bool ElementVisibleToViewer(PlanElement element)
    {
        foreach (var sourceId in ElementSourceIds(element))
        {
            if (!_sourceEvents.TryGetValue(sourceId, out var source) || !SourceVisible(source))
            {
                return false;
            }
        }
        return true;
    }

    public async Task OnGetAsync()
    {
        Plans = await _db.Plans
            .Include(p => p.Elements).ThenInclude(e => e.Agreements).ThenInclude(a => a.Person)
            .Include(p => p.RunnerPerson)
            .OrderByDescending(p => p.Id)
            .ToListAsync();
        People = await _db.Persons.OrderBy(p => p.Id).ToListAsync();
        var elementSourceIds = Plans
            .SelectMany(p => p.Elements)
            .SelectMany(e => ElementSourceIds(e))
            .Distinct()
            .ToList();
        if (elementSourceIds.Count > 0)
        {
            foreach (var source in await _db.Events.Where(e => elementSourceIds.Contains(e.Id)).ToListAsync())
            {
                _sourceEvents[source.Id] = source;
            }
        }
        var notes = await _db.Events
            .Where(e => e.Type == EventType.HouseholdNote && e.Details != null)
            .OrderByDescending(e => e.CreatedAtUtc)
            .ToListAsync();
        // Open decisions are record content, so they follow the
        // stored audience of the underlying note, through the
        // same rule the brief
        // and timeline use: a narrowed note appears only for a
        // reader inside its stored audience, and with no reader
        // named only house entries show. This list also feeds the
        // dinner form's source selector below, so a narrowed note
        // cannot be picked as a plan source by a reader who may
        // not see it either.
        OpenDecisions = notes
            .Where(e => ExtractionDetailsReader.GetString(e.Details, "DecisionState") == "Undecided")
            .Where(e => SourceVisible(e))
            .ToList();
        var household = await _db.Households.FirstOrDefaultAsync();
        if (household is not null)
        {
            // The provenance list renders tellings verbatim, so it
            // is fenced by the same stored audience: an item whose
            // telling the reader may not see is not listed, and an
            // element built from it loses its provenance here and
            // is not shown as a proposal.
            var allItems = await _dinner.GetItemsAsync(household.Id);
            var itemSourceIds = allItems.Select(i => i.SourceEventId).ToList();
            if (itemSourceIds.Count > 0)
            {
                foreach (var source in await _db.Events.Where(e => itemSourceIds.Contains(e.Id)).ToListAsync())
                {
                    _sourceEvents[source.Id] = source;
                }
            }
            ProvenanceItems = allItems
                .Where(i => _sourceEvents.TryGetValue(i.SourceEventId, out var source) && SourceVisible(source))
                .ToList();
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
    public async Task<IActionResult> OnPostAddDinnerAsync(int planId, int? sourceEventId, string mealText, int[] disposerIds, int? viewerId = null)
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
            return RedirectToPage(new { ViewerId = viewerId });
        }
        // The provenance list above only ever offers tellings the
        // reader named on this page may see, but a posted form can
        // name any telling (or match one by its words): the choice
        // is checked here, against the reader the form carried,
        // and a telling that reader may not see is refused whole
        // rather than attached.
        var mealSource = await _db.Events.FirstOrDefaultAsync(e => e.Id == meal.SourceEventId);
        var mealVisible = mealSource is not null
            && (viewerId is int reader
                ? BriefService.IsVisibleTo(mealSource, reader)
                : mealSource.SensitivityClass is SensitivityClass.Open or SensitivityClass.Household);
        if (!mealVisible)
        {
            TempData["PlanNote"] = "That telling is not one the reader named here may see, so it was not used.";
            return RedirectToPage(new { ViewerId = viewerId });
        }
        await _dinner.ProposeDinnerAsync(planId, new[] { meal.SourceEventId }, disposerIds);
        TempData["PlanNote"] = "Dinner is proposed, from a telling on the record. It settles only when everyone named has agreed.";
        return RedirectToPage(new { ViewerId = viewerId });
    }

    // The provenance form: a proposal drawn from the listed
    // tellings themselves (a stated usual meal, plus any stated
    // fridge contents it draws on), each carried on the element as
    // its source.
    public async Task<IActionResult> OnPostProposeFromProvenanceAsync(int planId, int[] sourceEventIds, int[] disposerIds, int? viewerId = null)
    {
        // As with the dinner form above: a posted form can name
        // any tellings, so every chosen telling is checked against
        // the reader the form carried before anything is proposed.
        foreach (var sourceId in sourceEventIds)
        {
            var source = await _db.Events.FirstOrDefaultAsync(e => e.Id == sourceId);
            var visible = source is not null
                && (viewerId is int reader
                    ? BriefService.IsVisibleTo(source, reader)
                    : source.SensitivityClass is SensitivityClass.Open or SensitivityClass.Household);
            if (!visible)
            {
                TempData["PlanNote"] = "One of those tellings is not one the reader named here may see, so nothing was proposed.";
                return RedirectToPage(new { ViewerId = viewerId });
            }
        }
        try
        {
            await _dinner.ProposeDinnerAsync(planId, sourceEventIds, disposerIds);
            TempData["PlanNote"] = "Dinner is proposed, from the tellings chosen. It settles only when everyone named has agreed.";
        }
        catch (InvalidOperationException ex)
        {
            TempData["PlanNote"] = ex.Message;
        }
        return RedirectToPage(new { ViewerId = viewerId });
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
