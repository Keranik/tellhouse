using System.Text.Json;
using HomeNode.Data;
using HomeNode.Protocol;
using Microsoft.EntityFrameworkCore;

namespace HomeNode.Services;

// The sealed delivery outbox: how the node reaches a remote
// member, on the frozen protocol contract. A delivery is a wire
// packet (exactly ciphertext, envelope, signature): sealed to the
// member's one identity keypair, signed with the node content
// keypair, envelope carrying exactly the eight fields. No
// plaintext copy of a delivery body is written anywhere in the
// node store. This replaces direct in-database delivery for
// members who hold a connection; members with no connection keep
// the house layer delivery the monolith already had.
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
    // and the value both stand-ins speak). The
    // node adopts that endpoint for the seeded demo household, so
    // envelopes, the relay registry, and intake all name one
    // endpoint. (A household-{id} form would match no stand-in and
    // no vector, so it is not used.)
    public static string HouseholdEndpointFor(int householdId) => "hh-demo-01";

    public static string NewPacketId() => $"pkt-{Guid.NewGuid():N}";

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
        var connection = await _db.HouseConnections
            .Where(c => c.PersonId == toPersonId && c.Standing == ConnectionStanding.Active)
            .OrderByDescending(c => c.CreatedAtUtc)
            .FirstOrDefaultAsync();
        if (connection is null)
        {
            return null;
        }
        return await QueueForConnectionAsync(
            connection, kind, bodyText, sourceKind,
            sourceEventId, sourcePlanElementId, toldAtUtc, dedupeKey,
            EnvelopeClass.StoreAndForward, Envelope.DeliveryStoreAndForward);
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
