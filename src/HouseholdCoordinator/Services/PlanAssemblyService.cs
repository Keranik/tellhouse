using System.Text.Json;
using HouseholdCoordinator.Data;
using Microsoft.EntityFrameworkCore;

namespace HouseholdCoordinator.Services;

// Assembles a Plan from the record: elements with
// the persons whose disposal each element requires, one Agreement row
// per disposal, rounds that restart when anyone counters, and a
// runner whose acknowledgement activates the plan. A runner's tap is
// the acknowledgement, never an agreement. Leave-by is plain
// arithmetic on durations a person stated: no stated durations, no
// leave-by element, and nothing here ever estimates a duration.
public class PlanAssemblyService
{
    private readonly AppDbContext _db;

    public PlanAssemblyService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<Plan> CreatePlanAsync(int householdId, string label, DateTime? anchorAtLocal, int? anchorEventId = null)
    {
        var plan = new Plan
        {
            HouseholdId = householdId,
            Label = label,
            AnchorAtLocal = anchorAtLocal,
            AnchorEventId = anchorEventId,
            State = PlanState.Assembling
        };
        _db.Plans.Add(plan);
        await _db.SaveChangesAsync();
        return plan;
    }

    public async Task<PlanElement> AddElementAsync(
        int planId, PlanElementKind kind, string bodyText, IEnumerable<int> requiredDisposerIds,
        int? assigneePersonId = null, IEnumerable<int>? sourceEventIds = null)
    {
        var plan = await LoadPlanAsync(planId);
        var element = new PlanElement
        {
            PlanId = plan.Id,
            Kind = kind,
            BodyText = bodyText,
            AssigneePersonId = assigneePersonId,
            RequiredDisposerIds = JsonSerializer.Serialize(requiredDisposerIds.Distinct().ToArray()),
            SourceEventIds = sourceEventIds is null ? null : JsonSerializer.Serialize(sourceEventIds.ToArray()),
            State = PlanElementState.Open
        };
        _db.PlanElements.Add(element);
        await _db.SaveChangesAsync();
        await RefreshPlanStateAsync(plan);
        return element;
    }

    // A leave-by element exists only when every duration in it was
    // stated by a person. The arithmetic is anchor minus the stated
    // durations, nothing else.
    public async Task<PlanElement?> AddLeaveByAsync(
        int planId, DateTime anchorAtLocal, IReadOnlyList<(string Label, int Minutes)> statedDurations)
    {
        if (statedDurations.Count == 0 || statedDurations.Any(d => d.Minutes <= 0))
        {
            return null;
        }
        var leaveBy = anchorAtLocal.AddMinutes(-statedDurations.Sum(d => d.Minutes));
        var parts = string.Join(", ", statedDurations.Select(d => $"{d.Label} {d.Minutes} minutes"));
        return await AddElementAsync(
            planId, PlanElementKind.LeaveBy,
            $"Leave by {leaveBy:h:mm tt}. From what was stated: anchor {anchorAtLocal:h:mm tt}, less {parts}.",
            Array.Empty<int>());
    }

    // One person's disposal of one element, in the current round. A
    // Countered starts a new round: earlier rows stay as history and
    // nobody's earlier agreement carries into the new round. A
    // counter carries the counter's own words, verbatim: the words
    // are stored on the row, they become the element's proposal text
    // for the new round, and they are carried to every other person
    // the element requires an answer from, as the revised proposal.
    // A wordless counter restarts the round and carries nothing.
    public async Task<PlanAgreement> RecordDisposalAsync(
        int elementId, int personId, PlanDisposal disposal, string? counterText = null)
    {
        var element = await _db.PlanElements
            .Include(e => e.Agreements)
            .FirstOrDefaultAsync(e => e.Id == elementId)
            ?? throw new InvalidOperationException($"Plan element {elementId} does not exist.");
        var required = RequiredDisposers(element);
        if (required.Count > 0 && !required.Contains(personId))
        {
            throw new InvalidOperationException(
                $"Person {personId} is not one of the people this element requires an answer from.");
        }
        var currentRound = element.Agreements.Count == 0 ? 1 : element.Agreements.Max(a => a.Round);
        if (element.Agreements.Any(a => a.Round == currentRound && a.Disposal == PlanDisposal.Countered))
        {
            currentRound++;
        }
        if (element.Agreements.Any(a => a.Round == currentRound && a.PersonId == personId))
        {
            throw new InvalidOperationException(
                $"Person {personId} has already answered this element in round {currentRound}.");
        }
        var words = disposal == PlanDisposal.Countered && !string.IsNullOrWhiteSpace(counterText)
            ? counterText
            : null;
        var agreement = new PlanAgreement
        {
            PlanElementId = element.Id,
            PersonId = personId,
            Disposal = disposal,
            Round = currentRound,
            AtUtc = DateTime.UtcNow,
            CounterText = words
        };
        _db.PlanAgreements.Add(agreement);
        await _db.SaveChangesAsync();

        if (words is not null)
        {
            // The counter is the new round's proposal: the element's
            // words become the counter's words, and the carry lands
            // with the other disposers, never back with the counterer
            // and never with anyone the element does not name.
            element.BodyText = words;
            await _db.SaveChangesAsync();
            foreach (var otherId in required.Where(id => id != personId))
            {
                await DeliverAsync(element, otherId, PlanDeliveryReason.CounterCarried, personId);
            }
        }

        if (IsSettled(element, currentRound))
        {
            element.State = PlanElementState.Settled;
            await _db.SaveChangesAsync();
            // The settled outcome lands in every disposer's own member
            // view, and only now: never on a partial agreement. (The
            // in-demo delivery path only; see PlanDelivery in
            // Entities.cs.)
            foreach (var disposerId in required)
            {
                await DeliverAsync(element, disposerId, PlanDeliveryReason.ElementSettled, null);
            }
        }
        var plan = await LoadPlanAsync(element.PlanId);
        await RefreshPlanStateAsync(plan);
        return agreement;
    }

    // The runner's acknowledgement is its own act: it activates the
    // plan once every element that requires agreement has settled, and
    // it never counts as anyone's agreement.
    public async Task AcknowledgeAsync(int planId, int runnerPersonId)
    {
        var plan = await _db.Plans
            .Include(p => p.Elements).ThenInclude(e => e.Agreements)
            .FirstOrDefaultAsync(p => p.Id == planId)
            ?? throw new InvalidOperationException($"Plan {planId} does not exist.");
        var blocking = plan.Elements.Any(e =>
            RequiredDisposers(e).Count > 0 && e.State != PlanElementState.Settled && e.State != PlanElementState.Dropped);
        if (blocking)
        {
            throw new InvalidOperationException(
                "The runner cannot acknowledge yet: an element is still waiting on someone's answer.");
        }
        plan.RunnerPersonId = runnerPersonId;
        plan.State = PlanState.Acknowledged;
        await _db.SaveChangesAsync();
        // The acknowledged plan lands with its runner: every element,
        // leave-by included, appears in the runner's member view and in
        // nobody else's. (The in-demo delivery path only; see
        // PlanDelivery in Entities.cs.)
        foreach (var element in plan.Elements)
        {
            await DeliverAsync(element, runnerPersonId, PlanDeliveryReason.PlanAcknowledged, null);
        }
    }

    // A person re-times an element (for example, Lisa moves dessert
    // to 8:30). The element's words change on the record, and the
    // updated element is routed to the person it affects, the element's
    // assignee, whose member view receives the new wording. Nobody else
    // receives it. The landing row keeps the new words verbatim, so the
    // record shows what the affected person actually got. The change
    // itself is written as a PlanChangeAudit row naming the actor and
    // carrying the words before and after, so the re-time is an
    // attributed change on the record, not a silent edit.
    public async Task<PlanElement> RetimeElementAsync(int elementId, string newBodyText, int actorPersonId)
    {
        var element = await _db.PlanElements.FirstOrDefaultAsync(e => e.Id == elementId)
            ?? throw new InvalidOperationException($"Plan element {elementId} does not exist.");
        var fromText = element.BodyText;
        element.BodyText = newBodyText;
        _db.PlanChangeAudits.Add(new PlanChangeAudit
        {
            PlanElementId = element.Id,
            ActorPersonId = actorPersonId,
            FromText = fromText,
            ToText = newBodyText,
            Channel = "tap",
            AtUtc = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
        if (element.AssigneePersonId is int affectedPersonId)
        {
            await DeliverAsync(element, affectedPersonId, PlanDeliveryReason.ElementRetimed, actorPersonId);
        }
        return element;
    }

    // Writes one PlanDelivery landing, guarded so the same words never
    // land twice with the same person for the same element: an
    // acknowledgement repeated, or a settlement re-checked, adds
    // nothing. Changed words are a new landing, which is what a
    // re-time needs.
    private async Task DeliverAsync(PlanElement element, int toPersonId, PlanDeliveryReason reason, int? actorPersonId)
    {
        var alreadyLanded = await _db.PlanDeliveries.AnyAsync(d =>
            d.PlanElementId == element.Id && d.ToPersonId == toPersonId && d.BodyText == element.BodyText);
        if (alreadyLanded)
        {
            return;
        }
        _db.PlanDeliveries.Add(new PlanDelivery
        {
            PlanElementId = element.Id,
            ToPersonId = toPersonId,
            Reason = reason,
            ActorPersonId = actorPersonId,
            BodyText = element.BodyText,
            DeliveredAtUtc = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
    }

    public static List<int> RequiredDisposers(PlanElement element)
    {
        if (string.IsNullOrWhiteSpace(element.RequiredDisposerIds))
        {
            return new List<int>();
        }
        try
        {
            return JsonSerializer.Deserialize<List<int>>(element.RequiredDisposerIds) ?? new List<int>();
        }
        catch (JsonException)
        {
            return new List<int>();
        }
    }

    private static bool IsSettled(PlanElement element, int round)
    {
        var required = RequiredDisposers(element);
        if (required.Count == 0)
        {
            return false;
        }
        var roundAnswers = element.Agreements
            .Where(a => a.Round == round)
            .GroupBy(a => a.PersonId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.AtUtc).First().Disposal);
        return required.All(id =>
            roundAnswers.TryGetValue(id, out var disposal) && disposal == PlanDisposal.Agreed);
    }

    private async Task RefreshPlanStateAsync(Plan plan)
    {
        if (plan.State is PlanState.Acknowledged or PlanState.Completed or PlanState.Superseded or PlanState.Dropped)
        {
            return;
        }
        var elements = await _db.PlanElements
            .Where(e => e.PlanId == plan.Id)
            .ToListAsync();
        plan.State = elements.Any(e => e.State == PlanElementState.Settled)
            ? PlanState.Proposed
            : PlanState.Assembling;
        await _db.SaveChangesAsync();
    }

    private async Task<Plan> LoadPlanAsync(int planId) =>
        await _db.Plans.FirstOrDefaultAsync(p => p.Id == planId)
        ?? throw new InvalidOperationException($"Plan {planId} does not exist.");
}
