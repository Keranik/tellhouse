using HouseholdCoordinator.Data;
using HouseholdCoordinator.Services.Extraction;
using Microsoft.EntityFrameworkCore;

namespace HouseholdCoordinator.Services;

// One item the house may propose for dinner, with the telling behind
// it: what was stated, who stated it, and on what day. Kind is
// "UsualMeal" (a meal a person stated as one of the household's
// usuals) or "Fridge" (a fridge content a person stated). An item
// with no telling behind it has no ProvenanceItem, by construction:
// items are read out of record events, never invented.
public record ProvenanceItem(
    int SourceEventId,
    string ItemName,
    string Kind,
    int? AuthorPersonId,
    string AuthorName,
    DateOnly ToldOn,
    string Body);

// Beat 3's provenance rule, in code: the house proposes dinner only
// from the household's stated usual meals and stated fridge
// contents. A telling marks itself as provenance in its Details:
// MealProvenance is "UsualMeal" or "Fridge", and ItemName names the
// item in plain words. This service reads those tellings, each with
// its author and its day, and builds a meal proposal element whose
// SourceEventIds are exactly those tellings, so every word of the
// proposal traces to a telling a person actually made. A proposal
// asked for with no usual meal telling, with an event that is not
// a provenance telling, or with an event from another household is
// refused outright: it is never shown with no telling behind it.
public class DinnerProvenanceService
{
    public const string UsualMealKind = "UsualMeal";
    public const string FridgeKind = "Fridge";

    private readonly AppDbContext _db;
    private readonly PlanAssemblyService _plans;

    public DinnerProvenanceService(AppDbContext db, PlanAssemblyService plans)
    {
        _db = db;
        _plans = plans;
    }

    public async Task<List<ProvenanceItem>> GetItemsAsync(int householdId)
    {
        var events = await _db.Events
            .Include(e => e.AuthorPerson)
            .Where(e => e.HouseholdId == householdId && e.Details != null)
            .OrderBy(e => e.CreatedAtUtc)
            .ToListAsync();
        var items = new List<ProvenanceItem>();
        foreach (var ev in events)
        {
            var kind = ExtractionDetailsReader.GetString(ev.Details, "MealProvenance");
            var name = ExtractionDetailsReader.GetString(ev.Details, "ItemName");
            if (kind is not (UsualMealKind or FridgeKind) || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }
            items.Add(new ProvenanceItem(
                ev.Id,
                name,
                kind,
                ev.AuthorPersonId,
                ev.AuthorPerson?.DisplayName ?? "Told to the house",
                DateOnly.FromDateTime(ev.CreatedAtUtc),
                ev.Body));
        }
        return items;
    }

    // The proposal's words, from its provenance items alone: the
    // usual meal, plus the fridge contents it draws on when any are
    // named. Deterministic, so a settled landing can be checked word
    // for word.
    public static string ProposalText(IReadOnlyList<ProvenanceItem> items)
    {
        var meal = items.First(i => i.Kind == UsualMealKind);
        var fridge = items.Where(i => i.Kind == FridgeKind).Select(i => i.ItemName).ToList();
        return fridge.Count == 0
            ? meal.ItemName
            : $"{meal.ItemName} (from the fridge: {string.Join(", ", fridge)})";
    }

    // Builds one meal proposal element from provenance tellings. The
    // element's SourceEventIds are the tellings themselves, in the
    // order given, so the Plans surface can render each item's
    // telling, author, and day beside the proposal.
    public async Task<PlanElement> ProposeDinnerAsync(
        int planId, IReadOnlyList<int> sourceEventIds, IEnumerable<int> requiredDisposerIds)
    {
        if (sourceEventIds.Count == 0)
        {
            throw new InvalidOperationException(
                "A dinner proposal needs at least one telling behind it; none was given.");
        }
        var plan = await _db.Plans.FirstOrDefaultAsync(p => p.Id == planId)
            ?? throw new InvalidOperationException($"Plan {planId} does not exist.");
        var items = new List<ProvenanceItem>();
        var all = await GetItemsAsync(plan.HouseholdId);
        foreach (var id in sourceEventIds)
        {
            var item = all.FirstOrDefault(i => i.SourceEventId == id)
                ?? throw new InvalidOperationException(
                    $"Event {id} is not a stated usual meal or fridge telling in this household; a proposal item is never shown with no telling behind it.");
            items.Add(item);
        }
        if (!items.Any(i => i.Kind == UsualMealKind))
        {
            throw new InvalidOperationException(
                "A dinner proposal needs a stated usual meal among its tellings.");
        }
        return await _plans.AddElementAsync(
            planId, PlanElementKind.MealProposal, ProposalText(items),
            requiredDisposerIds, sourceEventIds: items.Select(i => i.SourceEventId));
    }
}
