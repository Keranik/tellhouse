using HomeNode.Data;
using Microsoft.EntityFrameworkCore;

namespace HomeNode.Services;

// Everything waiting on one person, for the Inbox ("Needs you") and
// the badge on the rail. It decides nothing and adds no rule: every
// list is read from services the pages already use, through the same
// audience fences.
//
//   - Yours to answer: proposals this person made that are still
//     waiting for a yes, the "Who should see this?" questions the
//     house asked them, things delivered to them they have not marked
//     seen, and plan items waiting on their answer.
//   - Also waiting: proposals other people made that this person may
//     see and may answer (any household member may say yes, as on the
//     Timeline), and the claims this person has taken on.
public sealed class NeedsYou
{
    public int? PersonId { get; init; }
    public List<ActivityItem> YourProposals { get; init; } = new();
    public List<ActivityItem> OtherProposals { get; init; } = new();
    public List<AudienceAsk> Asks { get; init; } = new();
    public List<MemberDeliveryItem> NewDeliveries { get; init; } = new();
    public List<MemberDeliveryItem> SeenDeliveries { get; init; } = new();
    public List<PlanQuestion> PlanQuestions { get; init; } = new();
    public List<Promise> OpenClaims { get; init; } = new();

    public int YoursCount => YourProposals.Count + Asks.Count + NewDeliveries.Count + PlanQuestions.Count;
}

public sealed record PlanQuestion(Plan Plan, PlanElement Element);

public sealed class NeedsYouService
{
    private readonly AppDbContext _db;
    private readonly AudienceDecisionService _audience;
    private readonly MemberDeliveryService _deliveries;

    public NeedsYouService(AppDbContext db, AudienceDecisionService audience, MemberDeliveryService deliveries)
    {
        _db = db;
        _audience = audience;
        _deliveries = deliveries;
    }

    public async Task<NeedsYou> ForAsync(int personId)
    {
        var feed = await new ActivityService(_db).BuildAsync(personId, take: 300);
        var waiting = feed.Items.Where(i => i.NeedsDecision).ToList();
        var received = await _deliveries.ReceivedForAsync(personId);
        var claims = (await _db.Promises
                .Where(p => p.State == PromiseState.Made && p.PromisorPersonId == personId)
                .OrderByDescending(p => p.Id).ToListAsync());
        return new NeedsYou
        {
            PersonId = personId,
            YourProposals = waiting.Where(i => i.SpeakerId == personId).ToList(),
            OtherProposals = waiting.Where(i => i.SpeakerId != personId).Take(12).ToList(),
            Asks = await _audience.PendingAsksForAsync(personId, DateTime.UtcNow),
            NewDeliveries = received.Where(d => d.AlertDeliveryId is not null && d.ClosedAtUtc is null).ToList(),
            SeenDeliveries = received.Where(d => d.AlertDeliveryId is null || d.ClosedAtUtc is not null).Take(20).ToList(),
            PlanQuestions = await PlanQuestionsForAsync(personId),
            OpenClaims = claims
        };
    }

    // The badge: the same "yours to answer" count, from direct queries
    // so it is cheap enough to show on every page.
    public async Task<int> CountAsync(int personId)
    {
        var ownProposed = await _db.Events
            .Where(e => e.ConfirmationState == ConfirmationState.Proposed && e.AuthorPersonId == personId)
            .ToListAsync();
        var proposals = ownProposed.Count(e => BriefService.IsVisibleTo(e, personId));
        // Counted without presenting them: an ask's clock starts when
        // the person opens it (the Inbox), not when a badge counts it.
        var asks = await _db.AudienceAsks.CountAsync(a => a.AskedPersonId == personId
            && (a.State == AudienceAskState.Pending || a.State == AudienceAskState.Presented));
        var deliveries = await _db.AlertDeliveries
            .CountAsync(d => d.ToPersonId == personId && d.AcknowledgedAtUtc == null);
        var plans = (await PlanQuestionsForAsync(personId)).Count;
        return proposals + asks + deliveries + plans;
    }

    private async Task<List<PlanQuestion>> PlanQuestionsForAsync(int personId)
    {
        var plans = await _db.Plans
            .Include(p => p.Elements).ThenInclude(e => e.Agreements)
            .Where(p => p.State != PlanState.Completed && p.State != PlanState.Dropped && p.State != PlanState.Superseded)
            .ToListAsync();
        var questions = new List<PlanQuestion>();
        foreach (var plan in plans)
        {
            foreach (var element in plan.Elements.Where(e => e.State == PlanElementState.Open))
            {
                if (PlanAssemblyService.RequiredDisposers(element).Contains(personId)
                    && element.Agreements.All(a => a.PersonId != personId))
                {
                    questions.Add(new PlanQuestion(plan, element));
                }
            }
        }
        return questions;
    }
}
