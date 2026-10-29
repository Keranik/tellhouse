using HouseholdCoordinator.Data;
using Microsoft.EntityFrameworkCore;

namespace HouseholdCoordinator.Services;

// One thing that has landed with one person, as that person's member
// view shows it: the words received, when, and how they arrived. An
// alert landing also carries its delivery id and its closing state,
// so the receiving surface can offer the addressee's closing tap and
// show when it was closed; a landing whose event arrived through a
// member's own channel carries that event's provenance sentence.
public record MemberDeliveryItem(
    string Source, string Text, DateTime AtUtc, string Note,
    int? AlertDeliveryId = null, DateTime? ClosedAtUtc = null, string? Provenance = null);

// Reads what has landed with a person, for that person's member view
// (the /Member page). DEMO DELIVERY PATH ONLY: this view is the
// house layer on one computer standing in for delivery. The
// product's design reaches a person through that person's own
// assistant, in their own channel, with a plain fallback lane;
// that cross-channel delivery is not built here, and nothing in
// this service pretends to be it.
//
// Two kinds of landing exist in the demo, both derived from rows the
// record already keeps, never from a second copy of the content:
//   Alerts: AlertDelivery rows, written once by the confirmation
//     dispatcher, carrying the event Body verbatim.
//   Plan landings: PlanDelivery rows, written when a plan is
//     acknowledged by its runner, when an element settles with
//     everyone's agreement, when an element is re-timed and
//     routed to the person it affects, or when a counter's words
//     are carried to the other disposers as the new proposal.
public class MemberDeliveryService
{
    private readonly AppDbContext _db;

    public MemberDeliveryService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<List<MemberDeliveryItem>> ReceivedForAsync(int personId)
    {
        var items = new List<MemberDeliveryItem>();

        var alerts = await _db.AlertDeliveries
            .Include(d => d.Event)
            .Where(d => d.ToPersonId == personId)
            .OrderBy(d => d.DeliveredAtUtc)
            .ToListAsync();
        items.AddRange(alerts.Select(d => new MemberDeliveryItem(
            "Alert",
            d.BodyText,
            d.DeliveredAtUtc,
            "A confirmed telling, delivered to you word for word.",
            AlertDeliveryId: d.Id,
            ClosedAtUtc: d.AcknowledgedAtUtc,
            Provenance: d.Event is null ? null : ProvenanceRendering.Describe(d.Event))));

        // PlanElementId is a required foreign key, so PlanElement is
        // never null on a persisted landing; the null-forgiving
        // operator in the ThenInclude states that fact instead of
        // dereferencing a possibly null navigation (CS8602). The
        // label lookup below still guards the Plan navigation
        // itself, which is read defensively.
        var planLandings = await _db.PlanDeliveries
            .Include(d => d.PlanElement).ThenInclude(e => e!.Plan)
            .Include(d => d.ActorPerson)
            .Where(d => d.ToPersonId == personId)
            .OrderBy(d => d.DeliveredAtUtc)
            .ToListAsync();
        foreach (var landing in planLandings)
        {
            var planLabel = landing.PlanElement?.Plan?.Label ?? "A plan";
            var (source, note) = landing.Reason switch
            {
                PlanDeliveryReason.PlanAcknowledged =>
                    ("Plan", $"From the plan \"{planLabel}\", which you took on as runner."),
                PlanDeliveryReason.ElementSettled =>
                    ("Settled", $"From the plan \"{planLabel}\": settled when everyone named had agreed."),
                PlanDeliveryReason.ElementRetimed =>
                    ("Change", $"From the plan \"{planLabel}\": moved by {landing.ActorPerson?.DisplayName ?? "a person in the house"}, and routed to you because it affects you."),
                PlanDeliveryReason.CounterCarried =>
                    ("Counter", $"From the plan \"{planLabel}\": {landing.ActorPerson?.DisplayName ?? "a person in the house"} countered with these words, carried to you word for word as the new proposal."),
                _ => ("Plan", $"From the plan \"{planLabel}\".")
            };
            string? provenance = null;
            if (landing.PlanElement?.SourceEventIds is { } sourceJson)
            {
                try
                {
                    var sourceIds = System.Text.Json.JsonSerializer.Deserialize<List<int>>(sourceJson) ?? new List<int>();
                    if (sourceIds.Count > 0)
                    {
                        var sources = await _db.Events.Where(e => sourceIds.Contains(e.Id)).ToListAsync();
                        provenance = sources
                            .Select(s => ProvenanceRendering.Describe(s))
                            .FirstOrDefault(p => p is not null);
                    }
                }
                catch (System.Text.Json.JsonException)
                {
                }
            }
            items.Add(new MemberDeliveryItem(source, landing.BodyText, landing.DeliveredAtUtc, note,
                Provenance: provenance));
        }

        return items.OrderBy(i => i.AtUtc).ToList();
    }
}
