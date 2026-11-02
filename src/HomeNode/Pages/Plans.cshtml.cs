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

    // The record events the plan elements on this page were built
    // from, loaded once per read so each element's text can be
    // fenced by the audience stored on its sources.
    private readonly Dictionary<int, Event> _sourceEvents = new();

    [BindProperty(SupportsGet = true)]
    public int? ViewerId { get; set; }
    public List<Event> OpenDecisions { get; private set; } = new();
    public string? Note => TempData["PlanNote"] as string;

    // The trace behind each open decision, when the reader may see
    // its message: the decision was flagged by the reading of a
    // message, and the page says which engine flagged it.
    public Dictionary<int, ActivityItem> DecisionTraces { get; private set; } = new();
    public TimeZoneInfo HouseholdZone { get; private set; } = HouseholdTime.ZoneFor(null);

    public async Task OnGetAsync()
    {
        Plans = await _db.Plans
            .Include(p => p.Elements).ThenInclude(e => e.Agreements).ThenInclude(a => a.Person)
            .Include(p => p.RunnerPerson)
            .OrderByDescending(p => p.Id)
            .ToListAsync();
        People = await _db.Persons.OrderBy(p => p.Id).ToListAsync();
        var sourceIds = Plans
            .SelectMany(p => p.Elements)
            .SelectMany(e => PlanAssemblyService.SourceEventIds(e))
            .Distinct()
            .ToList();
        if (sourceIds.Count > 0)
        {
            foreach (var source in await _db.Events.Where(e => sourceIds.Contains(e.Id)).ToListAsync())
            {
                _sourceEvents[source.Id] = source;
            }
        }
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
        var household = await _db.Households.FirstOrDefaultAsync();
        HouseholdZone = HouseholdTime.ZoneFor(household?.TimeZoneId);
        if (OpenDecisions.Count > 0)
        {
            var decisionIds = OpenDecisions.Select(e => e.Id).ToHashSet();
            var feed = await new ActivityService(_db).BuildAsync(ViewerId, take: 300);
            foreach (var item in feed.Items)
            {
                foreach (var entry in item.Entries.Where(e => decisionIds.Contains(e.Event.Id)))
                {
                    DecisionTraces[entry.Event.Id] = item;
                }
            }
        }
    }

    // Whether this page's reader may see one plan element's text.
    // An element's words stand on the record it was built from, so
    // they follow the stored audience of every source event: an
    // element derived from a narrowed source shows only to readers
    // inside that source's audience, and to no reader when any
    // source is narrowed. An element with no source event was
    // written by hand on this page and behaves as it always has.
    // The view renders nothing of a hidden element, its agreements
    // and dispose form and its re-time form with the element's
    // words pre-filled included.
    public bool ElementVisibleToViewer(PlanElement element)
    {
        foreach (var sourceId in PlanAssemblyService.SourceEventIds(element))
        {
            if (!_sourceEvents.TryGetValue(sourceId, out var source))
            {
                return false;
            }
            var visible = ViewerId is int viewer
                ? BriefService.IsVisibleTo(source, viewer)
                : BriefService.IsVisibleWithNoReader(source);
            if (!visible)
            {
                return false;
            }
        }
        return true;
    }

    // One behavior change rides with the wording batch, ruled with it:
    // a blank field is refused and nothing is created, on all
    // three create handlers below. The record holds only what a
    // person wrote; it never fills in words nobody entered.
    private const string BlankWordsNote =
        "Type something first. Tellhouse never fills in words for you.";

    public async Task<IActionResult> OnPostCreatePlanAsync(string label, string? anchorTime)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            TempData["PlanNote"] = BlankWordsNote;
            return RedirectToPage();
        }
        var household = await _db.Households.FirstAsync();
        DateTime? anchor = null;
        if (TimeOnly.TryParse(anchorTime, out var time))
        {
            anchor = DateTime.Today.Add(time.ToTimeSpan());
        }
        await _plans.CreatePlanAsync(household.Id, label.Trim(), anchor);
        TempData["PlanNote"] = "Plan created. Now add what it needs.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostAddDinnerAsync(int planId, int? sourceEventId, string mealText, int[] disposerIds, int? viewerId = null)
    {
        if (string.IsNullOrWhiteSpace(mealText))
        {
            TempData["PlanNote"] = BlankWordsNote;
            return RedirectToPage(new { ViewerId = viewerId });
        }
        // The source selector above only ever offers decisions the
        // reader named on the page may see, but a posted form can
        // name any event id: the choice is checked here, against
        // the reader the form carried, and a source that reader
        // may not see is refused whole rather than attached.
        if (sourceEventId is int sourceId)
        {
            var source = await _db.Events.FirstOrDefaultAsync(e => e.Id == sourceId);
            var sourceVisible = source is not null
                && (viewerId is int reader
                    ? BriefService.IsVisibleTo(source, reader)
                    : BriefService.IsVisibleWithNoReader(source));
            if (!sourceVisible)
            {
                TempData["PlanNote"] = "That decision is not visible to the reader named here, so it was not used.";
                return RedirectToPage(new { ViewerId = viewerId });
            }
        }
        await _plans.AddElementAsync(
            planId, PlanElementKind.MealProposal,
            mealText.Trim(),
            disposerIds,
            sourceEventIds: sourceEventId is int id ? new[] { id } : null);
        TempData["PlanNote"] = "Dinner suggested. It's settled when everyone you named agrees.";
        return RedirectToPage(new { ViewerId = viewerId });
    }

    public async Task<IActionResult> OnPostAddListAsync(int planId, string listText, int runnerPersonId)
    {
        if (string.IsNullOrWhiteSpace(listText))
        {
            TempData["PlanNote"] = BlankWordsNote;
            return RedirectToPage();
        }
        await _plans.AddElementAsync(
            planId, PlanElementKind.ShoppingList,
            listText.Trim(),
            Array.Empty<int>(),
            assigneePersonId: runnerPersonId);
        TempData["PlanNote"] = "Shopping list added.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostAddLeaveByAsync(int planId, string durationsText)
    {
        var plan = await _db.Plans.FirstAsync(p => p.Id == planId);
        if (plan.AnchorAtLocal is not { } anchor)
        {
            TempData["PlanNote"] = "Add a start time to calculate a leave time.";
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
            ? "Add at least one time, like \"Drive to the store, 25\"."
            : "Leave time added.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDisposeAsync(int elementId, int personId, string disposal, string? counterText)
    {
        try
        {
            await _plans.RecordDisposalAsync(elementId, personId, Enum.Parse<PlanDisposal>(disposal), counterText);
            TempData["PlanNote"] = "Answer saved.";
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("already answered"))
        {
            // The already-answered path names the person who
            // answered, in plain words; the service's own wording,
            // which carries record ids and round numbers, never
            // reaches the page.
            var person = await _db.Persons.FirstOrDefaultAsync(p => p.Id == personId);
            TempData["PlanNote"] = $"{NameWording.First(person?.DisplayName)} has already answered this.";
        }
        catch (InvalidOperationException)
        {
            TempData["PlanNote"] = "This plan can't take that change right now.";
        }
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
            TempData["PlanNote"] = "Updated. The person it affects has been told.";
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostAcknowledgeAsync(int planId, int runnerPersonId)
    {
        try
        {
            await _plans.AcknowledgeAsync(planId, runnerPersonId);
            var runner = await _db.Persons.FirstOrDefaultAsync(p => p.Id == runnerPersonId);
            TempData["PlanNote"] = $"{NameWording.First(runner?.DisplayName)} has the plan.";
        }
        catch (InvalidOperationException ex)
        {
            // Every refusal is restated in plain words; the service's
            // own wording never reaches the page.
            TempData["PlanNote"] = ex.Message switch
            {
                var m when m.Contains("cannot acknowledge yet") =>
                    "Not everyone has answered yet.",
                var m when m.Contains("no task") || m.Contains("no runner to bind") =>
                    "Add a shopping list or errand with someone going first.",
                _ => "This plan can't take that change right now."
            };
        }
        return RedirectToPage();
    }
}
