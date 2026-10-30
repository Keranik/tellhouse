using System.Text.Json;
using HomeNode.Data;
using HomeNode.Protocol;
using Microsoft.EntityFrameworkCore;

namespace HomeNode.Services;

// The sealed delivery outbox: how the node reaches a remote
// member, on the frozen protocol contract. A delivery is a wire
// packet (exactly ciphertext, envelope, signature): sealed to the
// member's one identity keypair, signed with the node content
// keypair, envelope carrying exactly the eight relay-visible fields. No
// plaintext copy of a delivery body is written anywhere in the
// node store. This replaces direct in-database delivery for
// members who hold a connection; members with no connection keep
// the house layer's in-database delivery, unchanged.
//
// The service also answers the routing question for the existing
// pipeline: the confirmation dispatcher and plan assembly call
// QueueDeliveryAsync first, and only fall back to their
// in-database landing when it returns null (no Active connection
// for that person).
public class SealedDeliveryService
{
    private readonly AppDbContext _db;
    private readonly NodeKeyStore _nodeKeys;

    public SealedDeliveryService(AppDbContext db, NodeKeyStore nodeKeys)
    {
        _db = db;
        _nodeKeys = nodeKeys;
    }

    // The demo household's endpoint is the frozen protocol contract's
    // own endpoint (hh-demo-01, the value in the frozen test vectors
    // and the value both stand-ins speak). The node adopts that
    // endpoint for the seeded demo household, so envelopes, the
    // relay registry, and intake all name one endpoint. (An earlier
    // home node form used a household-{id} endpoint, which no
    // stand-in speaks.)
    public static string HouseholdEndpointFor(int householdId) => "hh-demo-01";

    public static string NewPacketId() => $"pkt-{Guid.NewGuid():N}";

    // Whether a delivery to this person is blocked. Blocked is
    // different from unreachable: a person with no connection at
    // all is not blocked by their connection, and their delivery
    // takes the house layer fallback their callers already had.
    // A delivery is blocked when the person must not receive
    // this material on ANY path:
    //   the stored audience of the material's source does not
    //     name them (below), or
    //   the connection they hold may not receive this material
    //     (it stands KnockOnly or Revoked, its level is KnockOnly,
    //     or the material is health class and its level excludes
    //     health entries, as every offered level does).
    // A blocked delivery must not fall back to a plaintext row
    // either: the fallback would deliver through the member view
    // exactly what the gate was promised the person cannot hear.
    // Callers that have a fallback consult this first.
    public async Task<bool> IsDeliveryBlockedAsync(
        int toPersonId, int? sourceEventId = null, int? sourcePlanElementId = null)
    {
        if (await IsOutsideStoredAudienceAsync(toPersonId, sourceEventId, sourcePlanElementId))
        {
            return true;
        }
        var connection = await _db.HouseConnections
            .Where(c => c.PersonId == toPersonId && c.Standing != ConnectionStanding.Revoked)
            .OrderByDescending(c => c.CreatedAtUtc)
            .FirstOrDefaultAsync();
        if (connection is null)
        {
            return false;
        }
        if (!ConnectionAccess.AllowsDelivery(connection))
        {
            return true;
        }
        return await IsHealthClassAsync(sourceEventId, sourcePlanElementId)
            && !ConnectionAccess.AllowsHealthDelivery(connection);
    }

    // Health class, resolved from the record the delivery carries:
    // its source event, or any source event of its plan element.
    private async Task<bool> IsHealthClassAsync(int? sourceEventId, int? sourcePlanElementId)
    {
        if (sourceEventId is int eventId)
        {
            var ev = await _db.Events.FirstOrDefaultAsync(e => e.Id == eventId);
            if (ev is not null && ConnectionAccess.IsHealthClass(ev))
            {
                return true;
            }
        }
        if (sourcePlanElementId is int elementId)
        {
            var element = await _db.PlanElements.FirstOrDefaultAsync(e => e.Id == elementId);
            if (element is not null)
            {
                foreach (var id in PlanAssemblyService.SourceEventIds(element))
                {
                    var ev = await _db.Events.FirstOrDefaultAsync(e => e.Id == id);
                    if (ev is not null && ConnectionAccess.IsHealthClass(ev))
                    {
                        return true;
                    }
                }
            }
        }
        return false;
    }

    // The stored-audience gate for delivery. QueueProposalAsync
    // has long enforced the audience for proposal deliveries,
    // but the general delivery path never did: it
    // gated standing, access level, and health class only, so a
    // body stored for one person could be queued to another and
    // opened verbatim under their key, and a plan landing could
    // carry a narrowed entry's words to a runner outside the
    // audience. The rule now, at this choke point: when the
    // delivery carries source material (a source event, or a plan
    // element and the events it was built from), the recipient
    // must be inside the stored audience of every source event,
    // where "inside" for delivery means the read audience
    // (BriefService.IsVisibleTo) or the person the stored rule
    // names as the delivery backup. The backup is not a reader,
    // but the rule names them to receive the delivery when the
    // route cannot, so delivering to them is the rule's own
    // terms. A plan element's named participants (its required
    // disposers and its assignee) are likewise inside for that
    // element: the element's words are the proposal addressed to
    // them, and the runner still receives only the element's own
    // words, never the telling behind them (the Q38 projection
    // boundary is unchanged; a runner outside the audience of the
    // underlying telling gets no delivery of words copied from
    // it). A delivery with no source material names nobody's
    // record and is not gated here.
    public async Task<bool> IsOutsideStoredAudienceAsync(
        int toPersonId, int? sourceEventId = null, int? sourcePlanElementId = null)
    {
        var sourceEvents = new List<Event>();
        if (sourceEventId is int eventId)
        {
            var ev = await _db.Events.FirstOrDefaultAsync(e => e.Id == eventId);
            if (ev is not null)
            {
                sourceEvents.Add(ev);
            }
        }
        var elementParticipants = new List<int>();
        if (sourcePlanElementId is int elementId)
        {
            var element = await _db.PlanElements.FirstOrDefaultAsync(e => e.Id == elementId);
            if (element is not null)
            {
                elementParticipants.AddRange(PlanAssemblyService.RequiredDisposers(element));
                if (element.AssigneePersonId is int assignee)
                {
                    elementParticipants.Add(assignee);
                }
                foreach (var id in PlanAssemblyService.SourceEventIds(element))
                {
                    var ev = await _db.Events.FirstOrDefaultAsync(e => e.Id == id);
                    if (ev is not null)
                    {
                        sourceEvents.Add(ev);
                    }
                }
            }
        }
        if (sourceEvents.Count == 0 || elementParticipants.Contains(toPersonId))
        {
            return false;
        }
        return sourceEvents.Any(ev => !IsInDeliveryAudience(ev, toPersonId));
    }

    private static bool IsInDeliveryAudience(Event ev, int personId) =>
        BriefService.IsVisibleTo(ev, personId) || RuleNamesBackup(ev, personId);

    // The stored rule's backup is a delivery term, not a read
    // term: BriefService.IsVisibleTo rightly leaves the backup
    // unable to READ the entry, while the confirmation dispatcher
    // delivers to them when the route names nobody. Delivery
    // honors the rule's own naming.
    private static bool RuleNamesBackup(Event ev, int personId)
    {
        if (string.IsNullOrWhiteSpace(ev.AudienceRule))
        {
            return false;
        }
        try
        {
            using var doc = JsonDocument.Parse(ev.AudienceRule);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("BackupPersonId", out var backup)
                && backup.ValueKind == JsonValueKind.Number
                && backup.TryGetInt32(out var backupId)
                && backupId == personId;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public async Task<SealedOutboxItem?> QueueDeliveryAsync(
        int householdId, int toPersonId, string bodyText, string sourceKind,
        int? sourceEventId = null, int? sourcePlanElementId = null, DateTime? toldAtUtc = null,
        string? dedupeKey = null, string kind = "delivery")
    {
        if (dedupeKey is not null)
        {
            var existing = await _db.SealedOutbox.FirstOrDefaultAsync(o => o.DedupeKey == dedupeKey);
            if (existing is not null)
            {
                return existing;
            }
        }
        if (await IsDeliveryBlockedAsync(toPersonId, sourceEventId, sourcePlanElementId))
        {
            return null;
        }
        var connection = await _db.HouseConnections
            .Where(c => c.PersonId == toPersonId && c.Standing == ConnectionStanding.Active)
            .OrderByDescending(c => c.CreatedAtUtc)
            .FirstOrDefaultAsync();
        if (connection is null || !ConnectionAccess.AllowsDelivery(connection))
        {
            return null;
        }
        return await QueueForConnectionAsync(
            connection, kind, bodyText, sourceKind,
            sourceEventId, sourcePlanElementId, toldAtUtc, dedupeKey,
            EnvelopeClass.StoreAndForward, Envelope.DeliveryStoreAndForward);
    }

    // The /admin/propose operation: queueing a proposal
    // delivery for an event. Pass one put the house secret gate on
    // the endpoint; what remained was that the endpoint queued the
    // entry's text for the addressed person whatever state the
    // entry was in and whoever the entry's stored audience named.
    // Reproduced on this tree over HTTP before the fix: a Done
    // entry was queued as a proposal, and a Personal entry stored
    // for Daniel alone was queued to Lisa. Both checks now live
    // here, inside the operation, failing closed:
    //   the entry must still be Proposed, and
    //   the proposed recipient must be inside the entry's stored
    //     audience (BriefService.IsVisibleTo, the same stored
    //     audience read every surface uses).
    // The refusal strings are stable tokens the endpoint maps to
    // status codes; the delivery gating itself (connection level,
    // health class) stays QueueDeliveryAsync's, below.
    public async Task<(SealedOutboxItem? Item, string? Refusal)> QueueProposalAsync(
        Event ev, int targetPersonId)
    {
        if (ev.ConfirmationState != ConfirmationState.Proposed)
        {
            return (null, "not-a-proposal");
        }
        if (!BriefService.IsVisibleTo(ev, targetPersonId))
        {
            return (null, "outside-audience");
        }
        var item = await QueueDeliveryAsync(ev.HouseholdId, targetPersonId,
            ev.Body, "Proposal", sourceEventId: ev.Id, kind: "proposal");
        return item is null ? (null, "undeliverable") : (item, null);
    }

    // Builds, seals, and signs one delivery packet to one enrolled
    // connection, and stores it in the outbox. Used directly for
    // the session challenge, which must reach a connection whose
    // standing may be KnockOnly (a re-confirmation in progress).
    public async Task<SealedOutboxItem> QueueForConnectionAsync(
        HouseConnection connection, string kind, string bodyText, string sourceKind,
        int? sourceEventId = null, int? sourcePlanElementId = null, DateTime? toldAtUtc = null,
        string? dedupeKey = null,
        EnvelopeClass envelopeClass = EnvelopeClass.StoreAndForward,
        string deliveryMode = Envelope.DeliveryStoreAndForward)
    {
        var toldAt = toldAtUtc ?? DateTime.UtcNow;
        var plain = new PlainPacket(
            PacketType.Delivery,
            NewPacketId(),
            connection.ConnectionId,
            NodeKeyStore.NodePartyId,
            connection.PartyId,
            new DateTimeOffset(toldAt).ToUnixTimeSeconds(),
            Base64Url.Encode(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)),
            PlainPacket.TextBody(kind, bodyText),
            null);
        var packet = Packet.Create(
            plain,
            HouseholdEndpointFor(connection.HouseholdId),
            envelopeClass,
            deliveryMode,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            connection.PublicKeyBase64Url,
            _nodeKeys.Keypair);
        var item = new SealedOutboxItem
        {
            PacketId = packet.Envelope.PacketId,
            ToPersonId = connection.PersonId,
            ConnectionId = connection.ConnectionId,
            WireJson = packet.ToCanonicalJson(),
            EnvelopeJson = packet.Envelope.ToCanonicalJson(),
            CiphertextBase64 = packet.CiphertextBase64Url,
            SignatureBase64 = packet.SignatureBase64Url,
            SourceKind = sourceKind,
            DedupeKey = dedupeKey,
            SourceEventId = sourceEventId,
            SourcePlanElementId = sourcePlanElementId,
            Status = "Queued",
            CreatedAtUtc = DateTime.UtcNow
        };
        _db.SealedOutbox.Add(item);
        await _db.SaveChangesAsync();
        return item;
    }

    // The member side of a delivery, as a checkable operation: the
    // member verifies the node's signature against the node
    // content public key, then opens the packet with their own
    // keypair. Any other member's keypair fails to open it.
    public string OpenDelivery(SealedOutboxItem item, IdentityKeypair memberKey)
    {
        var packet = Packet.Parse(item.WireJson);
        if (!packet.VerifySignature(_nodeKeys.PublicKeyBase64Url))
        {
            throw new InvalidOperationException("Delivery signature does not verify as the node's.");
        }
        var plain = packet.Open(memberKey);
        if (plain.Type != PacketType.Delivery)
        {
            throw new InvalidOperationException("That packet is not a delivery.");
        }
        return plain.Body.GetProperty("text").GetString() ?? "";
    }

    public static JsonElement BodyOf(Packet packet, IdentityKeypair opener)
    {
        var plain = packet.Open(opener);
        return plain.Body;
    }
}
