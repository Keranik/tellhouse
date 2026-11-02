using HomeNode.Data;
using HomeNode.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace HomeNode.Pages;

// The Inbox: "Needs you". Everything waiting on one person, each with
// one button: their own messages waiting for a yes, the questions the
// house asked them, what was delivered to them, plan items waiting on
// their answer, and the claims they have taken on. Read through
// NeedsYouService, which only gathers what the existing services
// already show; every answer posted here goes to the same service
// calls the other pages use.
//
// DEMO DELIVERY PATH: in the product a person's deliveries reach them
// through their own assistant; here the house layer stands in for
// that, as the page says.
public class MemberModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly NeedsYouService _needs;
    private readonly MemberDeliveryService _deliveries;
    private readonly ConfirmationService _confirmations;
    private readonly AudienceDecisionService _audience;
    private readonly PlanAssemblyService _plans;

    public MemberModel(AppDbContext db, NeedsYouService needs, MemberDeliveryService deliveries,
        ConfirmationService confirmations, AudienceDecisionService audience, PlanAssemblyService plans)
    {
        _db = db;
        _needs = needs;
        _deliveries = deliveries;
        _confirmations = confirmations;
        _audience = audience;
        _plans = plans;
    }

    public List<Person> People { get; private set; } = new();
    public Person? Member { get; private set; }
    public NeedsYou? Needs { get; private set; }
    public Dictionary<int, int> WaitingByPerson { get; private set; } = new();
    public Dictionary<int, ActivityItem> Traces { get; private set; } = new();
    // Picture cards Muse Image drew for deliveries, by delivery.
    public Dictionary<int, int> Pictures { get; private set; } = new();
    public TimeZoneInfo HouseholdZone { get; private set; } =
        TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");

    [TempData]
    public string? InboxNote { get; set; }

    public async Task OnGetAsync(int? personId)
    {
        People = await _db.Persons.OrderBy(p => p.Id).ToListAsync();
        var household = await _db.Households.FirstOrDefaultAsync();
        if (household is not null)
        {
            HouseholdZone = HouseholdTime.ZoneFor(household.TimeZoneId);
        }
        var id = CurrentPerson.Resolve(HttpContext, personId, People);
        Member = People.FirstOrDefault(p => p.Id == id);
        if (Member is null)
        {
            // The household view: no one's inbox, but who has
            // something waiting, so the next step is one tap away.
            foreach (var person in People)
            {
                WaitingByPerson[person.Id] = await _needs.CountAsync(person.Id);
            }
            return;
        }

        Needs = await _needs.ForAsync(Member.Id);
        var deliveryIds = Needs.NewDeliveries.Concat(Needs.SeenDeliveries)
            .Where(r => r.AlertDeliveryId is not null)
            .Select(r => r.AlertDeliveryId!.Value).ToList();
        var eventByDelivery = await _db.AlertDeliveries
            .Where(d => deliveryIds.Contains(d.Id))
            .ToDictionaryAsync(d => d.Id, d => d.EventId);
        Pictures = (await _db.ModelCalls
                .Where(c => c.Purpose == ModelCallPurpose.Draw && c.Outcome == ModelCallOutcome.Done
                    && c.DeliveryId != null && deliveryIds.Contains(c.DeliveryId.Value))
                .Select(c => new { c.Id, Delivery = c.DeliveryId!.Value })
                .ToListAsync())
            .GroupBy(c => c.Delivery).ToDictionary(g => g.Key, g => g.First().Id);
        var feed = await new ActivityService(_db).BuildAsync(Member.Id, take: 300);
        var traceByEvent = new Dictionary<int, ActivityItem>();
        foreach (var item in feed.Items)
        {
            foreach (var entry in item.Entries)
            {
                traceByEvent[entry.Event.Id] = item;
            }
        }
        foreach (var (deliveryId, eventId) in eventByDelivery)
        {
            // A delivery shows how it got here only when the member may
            // see the entry it came from: a delivery never widens what a
            // person may read.
            if (traceByEvent.TryGetValue(eventId, out var trace))
            {
                Traces[deliveryId] = trace;
            }
        }
    }

    public async Task<IActionResult> OnPostAcknowledgeAsync(int deliveryId, int personId)
    {
        await _deliveries.AcknowledgeDeliveryAsync(deliveryId, personId);
        InboxNote = "Marked as seen.";
        return RedirectToPage(new { personId });
    }

    public async Task<IActionResult> OnPostConfirmAsync(int eventId, int personId)
    {
        try
        {
            await _confirmations.ConfirmAsync(eventId, personId, "tap");
            InboxNote = "Confirmed. The house is carrying it out.";
        }
        catch (InvalidOperationException)
        {
            InboxNote = "Already answered.";
        }
        return RedirectToPage(new { personId });
    }

    public async Task<IActionResult> OnPostDeclineAsync(int eventId, int personId)
    {
        try
        {
            await _confirmations.DeclineAsync(eventId, personId, "tap");
            InboxNote = "Not sent.";
        }
        catch (InvalidOperationException)
        {
            InboxNote = "Already answered.";
        }
        return RedirectToPage(new { personId });
    }

    public async Task<IActionResult> OnPostCloseAsync(int eventId, int personId)
    {
        try
        {
            await _confirmations.CloseRecordPendingAsync(eventId, personId, "tap");
            InboxNote = "Confirmed.";
        }
        catch (InvalidOperationException)
        {
            InboxNote = "Already answered.";
        }
        return RedirectToPage(new { personId });
    }

    public async Task<IActionResult> OnPostAnswerAskAsync(int askId, int personId, string? choice)
    {
        if (choice is "Private" or "Household")
        {
            try
            {
                await _audience.AnswerAskAsync(askId, personId, choice, null, DateTime.UtcNow);
                InboxNote = choice == "Household"
                    ? "Saved. Everyone in the household can now see that entry."
                    : "Saved. Only the people it names can see that entry.";
            }
            catch (InvalidOperationException)
            {
                InboxNote = "Already answered.";
            }
        }
        return RedirectToPage(new { personId });
    }

    public async Task<IActionResult> OnPostAnswerPlanAsync(int elementId, int personId, string disposal)
    {
        if (disposal is "Agreed" or "Declined")
        {
            try
            {
                await _plans.RecordDisposalAsync(elementId, personId, Enum.Parse<PlanDisposal>(disposal));
                InboxNote = "Answer saved.";
            }
            catch (InvalidOperationException)
            {
                InboxNote = "Already answered.";
            }
        }
        return RedirectToPage(new { personId });
    }
}
