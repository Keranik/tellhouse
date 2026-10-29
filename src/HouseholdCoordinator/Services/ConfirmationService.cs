using System.Text.Json;
using HouseholdCoordinator.Data;
using Microsoft.EntityFrameworkCore;

namespace HouseholdCoordinator.Services;

// The one place events change state. Every transition validates
// against the transition table, moves the state, and writes exactly
// one append-only audit row. Silence is
// never a yes: nothing here is called by a timer except the lapse, and
// the lapse only ever moves a proposal to NotConfirmed.
//
// The dispatcher half fires a confirmed action's effect: a NotifyPerson
// writes an AlertDelivery carrying the event Body verbatim and nothing
// else, addressed to the route person named in the event's own stored
// AudienceRule, the audience computed once at write time. The
// household's current alert level is never consulted for the
// recipient: routing from current state could deliver to a person
// the write-time audience never named, and widening a stored
// audience is the one move the discretion rule forbids. The stored
// rule's backup person is used only when the route person cannot be
// delivered to at all; when the stored rule names nobody reachable,
// no delivery is written and the event stays Confirmed, owed and
// visible, with the failure on the audit trail.
// Other action types complete through the executor entry point; this
// build has no media player, reminder engine, or relay transport wired
// behind them yet, and the service does not pretend otherwise.
public class ConfirmationService
{
    private readonly AppDbContext _db;

    public ConfirmationService(AppDbContext db)
    {
        _db = db;
    }

    // A statement-only event is written already Confirmed: the act of
    // stating it to the house is the confirmation, and the audit says so
    // (actor is the author, channel is the input's source type). A
    // person-free statement has no author to stand as confirmer, so it
    // is written Proposed with RecordPending set and no audit row yet;
    // the first person who closes it becomes the actor of record.
    public async Task<Event> WriteStatementAsync(Event ev, string channel)
    {
        if (ev.AuthorPersonId is not null)
        {
            ev.ConfirmationState = ConfirmationState.Confirmed;
        }
        else
        {
            ev.ConfirmationState = ConfirmationState.Proposed;
            ev.RecordPending = true;
        }
        _db.Events.Add(ev);
        await _db.SaveChangesAsync();
        if (ev.AuthorPersonId is not null)
        {
            await WriteAuditAsync(ev, null, ev.ConfirmationState, ev.AuthorPersonId, channel, ev.CreatedAtUtc);
        }
        return ev;
    }

    // An event carrying a proposed action enters as Proposed, with its
    // action row attached; the confirmation prompt happens on a surface,
    // not here.
    public async Task<Event> ProposeAsync(Event ev, EventAction action, string channel)
    {
        ev.ConfirmationState = ConfirmationState.Proposed;
        _db.Events.Add(ev);
        await _db.SaveChangesAsync();
        action.EventId = ev.Id;
        _db.EventActions.Add(action);
        await WriteAuditAsync(ev, null, ConfirmationState.Proposed, ev.AuthorPersonId, channel, ev.CreatedAtUtc);
        await _db.SaveChangesAsync();
        return ev;
    }

    // Proposed to Confirmed: a named person decided, by voice or tap.
    public async Task ConfirmAsync(int eventId, int actorPersonId, string channel)
    {
        var ev = await LoadEventAsync(eventId);
        RequireState(ev, ConfirmationState.Proposed);
        ev.ConfirmationState = ConfirmationState.Confirmed;
        ev.RecordPending = false;
        await WriteAuditAsync(ev, ConfirmationState.Proposed, ConfirmationState.Confirmed, actorPersonId, channel, DateTime.UtcNow);
        await _db.SaveChangesAsync();
        await DispatchAsync(ev);
    }

    // Proposed to NotConfirmed: a named person declined. Terminal; the
    // action is discarded and never retried on its own.
    public async Task DeclineAsync(int eventId, int actorPersonId, string channel)
    {
        var ev = await LoadEventAsync(eventId);
        RequireState(ev, ConfirmationState.Proposed);
        ev.ConfirmationState = ConfirmationState.NotConfirmed;
        ev.RecordPending = false;
        await WriteAuditAsync(ev, ConfirmationState.Proposed, ConfirmationState.NotConfirmed, actorPersonId, channel, DateTime.UtcNow);
        await _db.SaveChangesAsync();
    }

    // Any circle member may close a person-free, record-pending event:
    // the confirmation lands as Confirmed and the audit names the person
    // who actually closed it.
    public async Task CloseRecordPendingAsync(int eventId, int actorPersonId, string channel)
    {
        var ev = await LoadEventAsync(eventId);
        if (!ev.RecordPending)
        {
            throw new InvalidOperationException($"Event {eventId} is not record-pending.");
        }
        await ConfirmAsync(eventId, actorPersonId, channel);
    }

    // Confirmed to Done: the executor reports completion. Called by the
    // dispatcher for NotifyPerson, and by executor entry points for the
    // other action types once their transports exist.
    public async Task CompleteAsync(int eventId)
    {
        var ev = await LoadEventAsync(eventId);
        RequireState(ev, ConfirmationState.Confirmed);
        ev.ConfirmationState = ConfirmationState.Done;
        await WriteAuditAsync(ev, ConfirmationState.Confirmed, ConfirmationState.Done, null, "executor", DateTime.UtcNow);
        await _db.SaveChangesAsync();
    }

    // The addressee's closing tap: the person a delivery landed with,
    // and only that person, closes it on the receiving surface (for
    // the trash turn, the tap that says the bin is out). The tap
    // stamps the delivery's acknowledgement time and writes one
    // audit row on the delivery's event naming the addressee as the
    // actor, so the record shows who closed it and when. A tap by
    // anyone else, or a second tap on a delivery already closed,
    // changes nothing and writes nothing.
    public async Task<AlertDelivery> CloseDeliveryAsync(int deliveryId, int actorPersonId, string channel)
    {
        var delivery = await _db.AlertDeliveries
            .FirstOrDefaultAsync(d => d.Id == deliveryId)
            ?? throw new InvalidOperationException($"Delivery {deliveryId} does not exist.");
        if (delivery.ToPersonId != actorPersonId)
        {
            throw new InvalidOperationException(
                $"Only the person this delivery landed with can close it; person {actorPersonId} is not that person.");
        }
        if (delivery.AcknowledgedAtUtc is not null)
        {
            throw new InvalidOperationException($"Delivery {deliveryId} is already closed.");
        }
        delivery.AcknowledgedAtUtc = DateTime.UtcNow;
        var ev = await LoadEventAsync(delivery.EventId);
        _db.EventAudits.Add(new EventAudit
        {
            ObjectKind = "Event",
            EventId = ev.Id,
            FromState = ev.ConfirmationState,
            ToState = "Acknowledged",
            ActorPersonId = actorPersonId,
            Channel = channel,
            AtUtc = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
        return delivery;
    }

    // A person closes a promise by saying so, in their own words, in a
    // closing telling that is itself on the record. Kept, Broken, and
    // Released are set only by a person's confirmed closing link, never
    // by the clock and never by inference. The closing event must
    // already be written; this transition links it
    // and moves the promise, with the audit row shaped like the seed's.
    public async Task ClosePromiseAsync(int promiseId, int closingEventId, int closerPersonId, string outcome, string channel)
    {
        if (outcome is not (nameof(PromiseState.Kept) or nameof(PromiseState.Broken) or nameof(PromiseState.Released)))
        {
            throw new InvalidOperationException(
                $"A closing link sets Kept, Broken, or Released; '{outcome}' is not a closing outcome.");
        }
        var promise = await _db.Promises.FirstOrDefaultAsync(p => p.Id == promiseId)
            ?? throw new InvalidOperationException($"Promise {promiseId} does not exist.");
        if (promise.State != PromiseState.Made)
        {
            throw new InvalidOperationException(
                $"Promise {promiseId} is {promise.State}; only a Made promise can be closed.");
        }
        var closingEvent = await LoadEventAsync(closingEventId);
        promise.State = Enum.Parse<PromiseState>(outcome);
        promise.ClosedByEventId = closingEvent.Id;
        promise.ClosedByPersonId = closerPersonId;
        promise.ClosedAtUtc = DateTime.UtcNow;
        _db.EventAudits.Add(new EventAudit
        {
            ObjectKind = "Promise",
            EventId = closingEvent.Id,
            FromState = null,
            ToState = outcome,
            ActorPersonId = closerPersonId,
            Channel = channel,
            AtUtc = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
    }

    // Execution failed: the event stays Confirmed, owed and visible, and
    // the failure is on the audit trail. It never silently
    // becomes Done.
    public async Task RecordExecutionFailureAsync(int eventId)
    {
        var ev = await LoadEventAsync(eventId);
        RequireState(ev, ConfirmationState.Confirmed);
        await WriteAuditAsync(ev, ConfirmationState.Confirmed, ConfirmationState.Confirmed, null, "executor", DateTime.UtcNow);
        await _db.SaveChangesAsync();
    }

    // The 24-hour lapse: an unanswered proposal lapses to
    // NotConfirmed with the system as actor. A record-pending fact is
    // never discarded by lapse; it stays visible as unconfirmed.
    // Called by the LapseService timer; nothing else calls it.
    public async Task<int> LapseExpiredAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var cutoff = nowUtc.AddHours(-24);
        var expired = await _db.Events
            .Where(e => e.ConfirmationState == ConfirmationState.Proposed
                        && !e.RecordPending
                        && e.CreatedAtUtc <= cutoff)
            .ToListAsync(ct);
        foreach (var ev in expired)
        {
            ev.ConfirmationState = ConfirmationState.NotConfirmed;
            await WriteAuditAsync(ev, ConfirmationState.Proposed, ConfirmationState.NotConfirmed, null, "lapse", nowUtc);
        }
        await _db.SaveChangesAsync(ct);
        return expired.Count;
    }

    // Fires the confirmed action's effect. Exactly one effect exists in
    // this build: a NotifyPerson action writes one AlertDelivery, Body
    // verbatim, addressed from the event's stored AudienceRule: the
    // route person the rule names, or its backup person only when the
    // route person cannot be delivered to at all. The event then
    // completes. Guarded by the deliveries table itself: at most one
    // alert per event, ever. When the stored rule names nobody
    // reachable, no delivery is written; the failure is recorded and
    // the event stays Confirmed, owed and visible.
    private async Task DispatchAsync(Event ev)
    {
        var action = await _db.EventActions.FirstOrDefaultAsync(a => a.EventId == ev.Id);
        if (action is null || action.ActionType != EventActionType.NotifyPerson)
        {
            return;
        }
        var alreadyDelivered = await _db.AlertDeliveries.AnyAsync(d => d.EventId == ev.Id);
        if (alreadyDelivered)
        {
            return;
        }
        var (routePersonId, backupPersonId) = ReadStoredRoute(ev.AudienceRule);
        var recipient = await FindAudiencePersonAsync(routePersonId, ev.HouseholdId);
        var isBackupHop = false;
        if (recipient is null)
        {
            recipient = await FindAudiencePersonAsync(backupPersonId, ev.HouseholdId);
            isBackupHop = recipient is not null;
        }
        if (recipient is null)
        {
            await RecordExecutionFailureAsync(ev.Id);
            return;
        }
        var household = await _db.Households.FirstAsync(h => h.Id == ev.HouseholdId);
        _db.AlertDeliveries.Add(new AlertDelivery
        {
            EventId = ev.Id,
            EventActionId = action.Id,
            LevelKey = household.HealthMentionLevelKey ?? "now",
            ToPersonId = recipient.Value,
            BodyText = ev.Body,
            DeliveredAtUtc = DateTime.UtcNow,
            IsBackupHop = isBackupHop
        });
        await _db.SaveChangesAsync();
        await CompleteAsync(ev.Id);
    }

    // The route stored on the event at write time: the route person
    // and the backup person, read from the AudienceRule. A rule that
    // is absent or malformed names nobody, and nobody is what
    // dispatch acts on: the absence fails closed rather than falling
    // back to any current household setting.
    private static (int?, int?) ReadStoredRoute(string? audienceRule)
    {
        if (string.IsNullOrWhiteSpace(audienceRule))
        {
            return (null, null);
        }
        try
        {
            using var doc = JsonDocument.Parse(audienceRule);
            var root = doc.RootElement;
            int? Read(string key) =>
                root.TryGetProperty(key, out var value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt32(out var n)
                    ? n
                    : null;
            return (Read("RoutePersonId"), Read("BackupPersonId"));
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    // A stored person id reaches only a person who is in the event's
    // household; an id that names nobody there cannot be delivered
    // to, which is the one case where the stored backup is used.
    private async Task<int?> FindAudiencePersonAsync(int? personId, int householdId)
    {
        if (personId is not int id)
        {
            return null;
        }
        return await _db.Persons.AnyAsync(p => p.Id == id && p.HouseholdId == householdId)
            ? id
            : null;
    }

    private async Task<Event> LoadEventAsync(int eventId) =>
        await _db.Events.FirstOrDefaultAsync(e => e.Id == eventId)
        ?? throw new InvalidOperationException($"Event {eventId} does not exist.");

    private static void RequireState(Event ev, ConfirmationState expected)
    {
        if (ev.ConfirmationState != expected)
        {
            throw new InvalidOperationException(
                $"Event {ev.Id} is {ev.ConfirmationState}; this transition requires {expected}.");
        }
    }

    private async Task WriteAuditAsync(Event ev, ConfirmationState? from, ConfirmationState to, int? actorPersonId, string channel, DateTime atUtc)
    {
        _db.EventAudits.Add(new EventAudit
        {
            ObjectKind = "Event",
            EventId = ev.Id,
            FromState = from,
            ToState = to.ToString(),
            ActorPersonId = actorPersonId,
            Channel = channel,
            AtUtc = atUtc
        });
        await _db.SaveChangesAsync();
    }
}
