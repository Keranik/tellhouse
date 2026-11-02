using HomeNode.Data;
using HomeNode.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace HomeNode.Pages;

// A member's own view: what has landed with this one person, and
// nothing that landed with anyone else.
//
// DEMO DELIVERY PATH ONLY. On this one computer, this page stands
// in for delivery so the demo's landings can run: Daniel's plan
// reaching Daniel, a settled dinner reaching both partners, a
// confirmed alert reaching the person it names, a re-timed entry
// reaching the person it affects. The product's design delivers
// through a person's own assistant, in their own channel; that
// cross-channel delivery is not built here, and this page does not
// pretend to be it.
public class MemberModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly MemberDeliveryService _deliveries;

    public MemberModel(AppDbContext db, MemberDeliveryService deliveries)
    {
        _db = db;
        _deliveries = deliveries;
    }

    public List<Person> People { get; private set; } = new();
    public Person? Member { get; private set; }
    public List<MemberDeliveryItem> Received { get; private set; } = new();

    // How a delivered message got here, by delivery id: the trace of
    // the message behind it, when the member may see that message's
    // entries under their stored audience. A delivery carries its own
    // words to the person it names; it never widens what that person
    // may read in the record, so a source the member may not see has
    // no trace here.
    public Dictionary<int, ActivityItem> Traces { get; private set; } = new();

    // The household zone inbox times are shown in.
    public TimeZoneInfo HouseholdZone { get; private set; } =
        TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");

    public async Task OnGetAsync(int? personId)
    {
        People = await _db.Persons.OrderBy(p => p.Id).ToListAsync();
        var household = await _db.Households.FirstOrDefaultAsync();
        if (household is not null)
        {
            HouseholdZone = HouseholdTime.ZoneFor(household.TimeZoneId);
        }
        // The inbox defaults to Daniel, who holds the seeded alert,
        // so the page never opens on an empty inbox by accident.
        var id = personId
            ?? People.FirstOrDefault(
                p => p.DisplayName.StartsWith("Daniel", StringComparison.Ordinal))?.Id
            ?? People.FirstOrDefault()?.Id;
        Member = People.FirstOrDefault(p => p.Id == id);
        if (Member is not null)
        {
            Received = await _deliveries.ReceivedForAsync(Member.Id);
            var deliveryIds = Received.Where(r => r.AlertDeliveryId is not null)
                .Select(r => r.AlertDeliveryId!.Value).ToList();
            var eventByDelivery = await _db.AlertDeliveries
                .Where(d => deliveryIds.Contains(d.Id))
                .ToDictionaryAsync(d => d.Id, d => d.EventId);
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
                if (traceByEvent.TryGetValue(eventId, out var trace))
                {
                    Traces[deliveryId] = trace;
                }
            }
        }
    }

    // The addressee's closing tap on one of their own deliveries.
    // The person named here is the person whose view this is; the
    // delivery service itself enforces that only the delivery's
    // addressee can stamp it, so a tap posted from anyone else's
    // view changes nothing.
    public async Task<IActionResult> OnPostAcknowledgeAsync(int deliveryId, int personId)
    {
        await _deliveries.AcknowledgeDeliveryAsync(deliveryId, personId);
        return RedirectToPage(new { personId });
    }
}
