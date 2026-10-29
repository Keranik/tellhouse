using System.Security.Cryptography;
using System.Text.Json;
using HomeNode.Data;
using HomeNode.Protocol;
using HomeNode.Services.Extraction;
using Microsoft.EntityFrameworkCore;

namespace HomeNode.Services;

// Sealed intake on the frozen protocol contract .
//
// A wire packet is exactly ciphertext, envelope, signature. The
// envelope is exactly the eight fields. Intake runs in the
// contract's section 8 order and rejects on the first failure,
// creating no event and writing one trail line:
//
//   1. parse the wire packet (exactly three keys);
//   2. parse the envelope (exactly the relay-visible list, canonical form);
//   3. packet_id replay registry: a seen packet_id is rejected
//      here, at the envelope step, before opening, and every new
//      packet_id is recorded here whatever happens later;
//   4. ciphertext_length and ciphertext_hash recomputed from the
//      blob (the hash is base64url SHA-256 of the blob);
//   5. packet signature verified under the enrolled connection
//      key (member packets under the member keypair);
//   6. the seal opens on the node, with AAD exactly
//      Envelope.AadBytes() (the canonical envelope without the
//      ciphertext_hash field, which is bound by the signature
//      and by step 4 instead);
//   7. the plain packet validates against the envelope, its
//      sender against the connection enrollment, and its packet
//      nonce against single use per connection;
//   8. only now does content reach any other processing.
//
// Permission enforcement sits inside that order: the connection
// must exist and stand Active (KnockOnly connections may run only
// the knock exchange that re-confirms them), the household
// endpoint must be this node's, session class packets require an
// open session (contract section 7, completed by proof), and
// store_and_forward packets require the connection to have
// completed a session open at least once (node policy for queued
// traffic, stated here as node policy).
//
// A packet that passes is queued in the inbox, sealed, marked
// unrecorded: it exists in no record table until ProcessPacket
// opens it and runs it through the same pipeline a front door
// telling takes: the Consent valve (node side, by kind, because
// the frozen plain packet carries no valve field), extraction
// behind the model role seam (ExtractionService remains the sole
// model caller), the confirmation dispatcher, and plan disposal.
// The speaker is resolved from the connection's enrollment, never
// from packet contents.
public class SealedIntakeService
{
    private readonly AppDbContext _db;
    private readonly NodeKeyStore _nodeKeys;
    private readonly ExtractionContextBuilder _contextBuilder;
    private readonly ExtractionService _extraction;
    private readonly ConfirmationService _confirmations;
    private readonly PlanAssemblyService _plans;
    private readonly SealedDeliveryService _deliveries;

    public SealedIntakeService(
        AppDbContext db,
        NodeKeyStore nodeKeys,
        ExtractionContextBuilder contextBuilder,
        ExtractionService extraction,
        ConfirmationService confirmations,
        PlanAssemblyService plans,
        SealedDeliveryService deliveries)
    {
        _db = db;
        _nodeKeys = nodeKeys;
        _contextBuilder = contextBuilder;
        _extraction = extraction;
        _confirmations = confirmations;
        _plans = plans;
        _deliveries = deliveries;
    }

    public record IntakeReceipt(bool Accepted, string PacketId, string? Reason);

    // Node policy: a completed proof opens the session for this
    // window. The contract fixes the challenge lifetime (120
    // seconds) and the proof; it names no session lifetime, so
    // this window is the node's stated choice.
    private static readonly TimeSpan SessionWindow = TimeSpan.FromMinutes(30);

    public async Task<bool> HasOpenSessionAsync(string connectionId)
    {
        var since = DateTime.UtcNow.Subtract(SessionWindow);
        return await _db.SessionChallenges.AnyAsync(c =>
            c.ConnectionId == connectionId && c.Verified && c.VerifiedAtUtc >= since);
    }

    private async Task<bool> HasEverOpenedSessionAsync(string connectionId) =>
        await _db.SessionChallenges.AnyAsync(c => c.ConnectionId == connectionId && c.Verified);

    // --- Steps 1 to 5 plus standing, then queue unrecorded. ---

    public async Task<IntakeReceipt> ReceiveWireAsync(string wireJson)
    {
        Packet packet;
        try
        {
            packet = Packet.Parse(wireJson);
        }
        catch (Exception ex) when (ex is ProtocolException or JsonException or FormatException or CryptographicException)
        {
            await TrailAsync("", null, "Rejected",
                "Wire packet or envelope is not in the contract form: " + ex.Message);
            return new IntakeReceipt(false, "", ex.Message);
        }
        var envelope = packet.Envelope;

        async Task<IntakeReceipt> Reject(string reason)
        {
            await TrailAsync(envelope.PacketId, envelope.ConnectionId, "Rejected", reason);
            return new IntakeReceipt(false, envelope.PacketId, reason);
        }

        // Step 3: the seen registry, at the envelope step, before
        // opening. A replayed packet_id is rejected here even if
        // the first copy is still queued unopened.
        if (await _db.PacketSeen.AnyAsync(s => s.PacketId == envelope.PacketId))
        {
            return await Reject("Replay rejected: packet_id already seen.");
        }
        _db.PacketSeen.Add(new PacketSeen
        {
            PacketId = envelope.PacketId,
            ConnectionId = envelope.ConnectionId,
            SeenAtUtc = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        // Step 4: length and hash recomputed from the blob.
        var blob = packet.CiphertextBytes();
        if (blob.Length != envelope.CiphertextLength
            || Base64Url.Encode(SHA256.HashData(blob)) != envelope.CiphertextHash)
        {
            return await Reject("Ciphertext does not match its stated length and hash.");
        }

        var connection = await _db.HouseConnections
            .FirstOrDefaultAsync(c => c.ConnectionId == envelope.ConnectionId);
        if (connection is null)
        {
            return await Reject("No connection by that id is enrolled at this house.");
        }
        if (envelope.HouseholdEndpoint != SealedDeliveryService.HouseholdEndpointFor(connection.HouseholdId))
        {
            return await Reject("The envelope names a different household endpoint.");
        }
        if (connection.Standing == ConnectionStanding.Revoked)
        {
            return await Reject("Connection stands Revoked; it may submit nothing.");
        }
        if (connection.Standing == ConnectionStanding.KnockOnly && envelope.Class != EnvelopeClass.Knock)
        {
            return await Reject("Connection stands KnockOnly; it may present only a knock (session open or re-confirmation).");
        }

        // Step 5: signature under the enrolled connection key.
        if (!packet.VerifySignature(connection.PublicKeyBase64Url))
        {
            return await Reject("Signature does not verify under the enrolled connection key.");
        }

        // Session gating, by envelope class. The one session class
        // packet admitted without an open session is the session
        // open response itself: it answers an outstanding
        // challenge, and the proof inside it is what opens the
        // session when the packet is processed.
        if (envelope.Class == EnvelopeClass.Session && !await HasOpenSessionAsync(connection.ConnectionId))
        {
            var outstanding = await _db.SessionChallenges.AnyAsync(c =>
                c.ConnectionId == connection.ConnectionId && !c.Verified && !c.Consumed
                && c.ExpiresAtUtc > DateTime.UtcNow);
            if (!outstanding)
            {
                return await Reject("No open session for this connection; the session open proof comes first.");
            }
        }
        if (envelope.Class == EnvelopeClass.StoreAndForward && !await HasEverOpenedSessionAsync(connection.ConnectionId))
        {
            return await Reject("This connection has never completed a session open; queued traffic waits for one.");
        }

        _db.PacketInbox.Add(new PacketInbox
        {
            PacketId = envelope.PacketId,
            ConnectionId = envelope.ConnectionId,
            WireJson = packet.ToCanonicalJson(),
            EnvelopeJson = envelope.ToCanonicalJson(),
            CiphertextBase64 = packet.CiphertextBase64Url,
            SignatureBase64 = packet.SignatureBase64Url,
            Status = "QueuedUnrecorded",
            ReceivedAtUtc = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
        await TrailAsync(envelope.PacketId, envelope.ConnectionId, "QueuedUnrecorded",
            "Taken in sealed. Nothing is recorded until the node opens it.");
        return new IntakeReceipt(true, envelope.PacketId, null);
    }

    // --- Steps 6 to 8: open inside the boundary, then pipeline. ---

    public async Task<IntakeReceipt> ProcessPacketAsync(string packetId)
    {
        var inbox = await _db.PacketInbox.FirstOrDefaultAsync(p => p.PacketId == packetId)
            ?? throw new InvalidOperationException($"Packet {packetId} is not in the inbox.");
        if (inbox.Status != "QueuedUnrecorded")
        {
            return new IntakeReceipt(false, packetId, $"Packet already {inbox.Status}.");
        }
        var connection = await _db.HouseConnections
            .FirstAsync(c => c.ConnectionId == inbox.ConnectionId);
        var packet = Packet.Parse(inbox.WireJson);

        async Task<IntakeReceipt> Reject(string reason)
        {
            inbox.Status = "Rejected";
            inbox.RejectionReason = reason;
            await _db.SaveChangesAsync();
            await TrailAsync(packetId, inbox.ConnectionId, "Rejected", reason);
            return new IntakeReceipt(false, packetId, reason);
        }

        // Step 6 and the plain half of step 7: Packet.Open runs
        // the seal with AAD Envelope.AadBytes(), parses the plain
        // packet, and validates its ids against the envelope.
        PlainPacket plain;
        try
        {
            plain = packet.Open(_nodeKeys.Keypair);
        }
        catch (Exception ex) when (ex is ProtocolException or JsonException or FormatException or CryptographicException)
        {
            return await Reject("Packet did not open as a contract packet inside the house boundary: " + ex.Message);
        }

        // Step 7: sender and recipient against the enrollment.
        if (plain.SenderId != connection.PartyId)
        {
            return await Reject("The plain packet's sender differs from the connection enrollment.");
        }
        if (plain.RecipientId != NodeKeyStore.NodePartyId)
        {
            return await Reject("The plain packet is not addressed to this node.");
        }
        // Step 7, replay half: packet nonce single use per
        // connection, checked after opening.
        if (await _db.PacketNonceUses.AnyAsync(n =>
                n.ConnectionId == connection.ConnectionId && n.PacketNonce == plain.PacketNonce))
        {
            return await Reject("Replay rejected: packet nonce already used on this connection.");
        }
        _db.PacketNonceUses.Add(new PacketNonceUse
        {
            ConnectionId = connection.ConnectionId,
            PacketNonce = plain.PacketNonce,
            PacketId = packetId,
            UsedAtUtc = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        inbox.ToldAtUtc = DateTimeOffset.FromUnixTimeSeconds(plain.ToldAt).UtcDateTime;

        return plain.Type switch
        {
            PacketType.Delivery => await Reject(
                "Delivery packets travel from the node to a member, never into intake."),
            PacketType.Acknowledgement when plain.Proof is not null =>
                await ProcessSessionProofAsync(inbox, connection, plain),
            PacketType.Acknowledgement =>
                await ProcessAcknowledgementAsync(inbox, connection, plain),
            PacketType.Telling =>
                await ProcessTellingAsync(inbox, connection, plain),
            _ => await Reject($"Packet type {plain.Type} is not one this house takes in.")
        };
    }

    public async Task<int> ProcessAllQueuedAsync()
    {
        var queued = await _db.PacketInbox
            .Where(p => p.Status == "QueuedUnrecorded")
            .OrderBy(p => p.ReceivedAtUtc)
            .Select(p => p.PacketId)
            .ToListAsync();
        var recorded = 0;
        foreach (var packetId in queued)
        {
            var receipt = await ProcessPacketAsync(packetId);
            if (receipt.Accepted)
            {
                recorded++;
            }
        }
        return recorded;
    }

    // A telling packet. Kind decides the road, and the valve is
    // checked before anything touches extraction:
    //   session_open: the knock exchange, contract section 7.
    //   raw_telling: a one liner for the record (OneLiner valve).
    //   question: an ask against the record (Question valve); the
    //     narrow answer returns as a sealed delivery.
    private async Task<IntakeReceipt> ProcessTellingAsync(
        PacketInbox inbox, HouseConnection connection, PlainPacket plain)
    {
        async Task<IntakeReceipt> Reject(string reason)
        {
            inbox.Status = "Rejected";
            inbox.RejectionReason = reason;
            await _db.SaveChangesAsync();
            await TrailAsync(inbox.PacketId, inbox.ConnectionId, "Rejected", reason);
            return new IntakeReceipt(false, inbox.PacketId, reason);
        }

        var kind = plain.Body.GetProperty("kind").GetString() ?? "";
        var text = plain.Body.GetProperty("text").GetString() ?? "";

        if (kind == Protocol.SessionChallenge.OpenKind)
        {
            return await IssueChallengeAsync(inbox, connection, text);
        }
        if (kind == Protocol.SessionChallenge.ChallengeKind)
        {
            return await Reject("A session challenge travels from the node to a member, never into intake.");
        }

        var materialClass = kind switch
        {
            "raw_telling" => "OneLiner",
            "question" => "Question",
            _ => null
        };
        if (materialClass is null)
        {
            return await Reject($"Telling kind {kind} is not a kind this house takes in.");
        }
        if (string.IsNullOrWhiteSpace(text))
        {
            return await Reject("A telling or question packet carries raw text and nothing else; this one carries none.");
        }
        var valveFailure = await CheckValveAsync(connection, materialClass);
        if (valveFailure is not null)
        {
            return await Reject(valveFailure);
        }

        var person = await _db.Persons.FirstAsync(p => p.Id == connection.PersonId);
        var context = await _contextBuilder.BuildAsync(connection.HouseholdId);
        var input = new ExtractionInput
        {
            UtteranceId = $"pkt-{inbox.PacketId}"[..Math.Min(26, inbox.PacketId.Length + 4)],
            Transcript = text.Trim(),
            SourceType = "federated",
            OccurredAtLocal = new DateTimeOffset(
                DateTime.SpecifyKind(inbox.ToldAtUtc!.Value, DateTimeKind.Utc), TimeSpan.Zero),
            Speaker = new ExtractionPersonRef
            {
                PersonId = person.Id,
                DisplayName = person.DisplayName,
                Role = person.Role.ToString()
            },
            Context = context
        };
        var result = await _extraction.ProcessAsync(input);

        var recordedAt = DateTime.UtcNow;
        foreach (var ev in result.WrittenEvents)
        {
            // The record's provenance fields (the provenance model's names and
            // semantics) are the source of truth, written here,
            // by the intake that opened the packet: the origin is
            // the member's own channel, named the way surfaces
            // render it ("Came via Lisa's assistant"), with the
            // connection and the packet that carried it.
            ev.ProvenanceOrigin = $"{person.DisplayName.Split(' ')[0]}'s assistant";
            ev.ProvenanceConnection = connection.ConnectionId;
            ev.ProvenancePacketId = inbox.PacketId;
            ev.ReportedNotObserved = true;
            ev.ToldAtUtc = inbox.ToldAtUtc;
            ev.RecordedAtUtc = recordedAt;
        }
        if (result.WrittenEvents.Count > 0)
        {
            await _db.SaveChangesAsync();
        }
        inbox.Status = "Recorded";
        inbox.RecordedAtUtc = recordedAt;
        inbox.ResultEventIds = System.Text.Json.JsonSerializer.Serialize(result.WrittenEvents.Select(e => e.Id));
        await _db.SaveChangesAsync();
        await TrailAsync(inbox.PacketId, connection.ConnectionId, "Recorded",
            result.WrittenEvents.Count > 0
                ? $"Ran the same extraction and confirmation pipeline as a front door telling; {result.WrittenEvents.Count} event(s) written."
                : "Question or clarification: nothing written, the reply is the only trace.",
            result.WrittenEvents.FirstOrDefault()?.Id);

        if (kind == "question" && result.HouseReply is not null)
        {
            await _deliveries.QueueDeliveryAsync(
                connection.HouseholdId, connection.PersonId, result.HouseReply, "Answer",
                toldAtUtc: recordedAt);
        }
        return new IntakeReceipt(true, inbox.PacketId, null);
    }

    // Contract section 7, node side of steps 1 and 2: the knock
    // carried a client nonce; the node issues a server nonce in a
    // knock class delivery sealed to the member key, keeps one
    // outstanding challenge per connection, and the challenge
    // lives 120 seconds.
    private async Task<IntakeReceipt> IssueChallengeAsync(
        PacketInbox inbox, HouseConnection connection, string clientNonce)
    {
        async Task<IntakeReceipt> Reject(string reason)
        {
            inbox.Status = "Rejected";
            inbox.RejectionReason = reason;
            await _db.SaveChangesAsync();
            await TrailAsync(inbox.PacketId, inbox.ConnectionId, "Rejected", reason);
            return new IntakeReceipt(false, inbox.PacketId, reason);
        }

        byte[] clientNonceBytes;
        try
        {
            clientNonceBytes = Base64Url.Decode(clientNonce);
        }
        catch (FormatException)
        {
            return await Reject("Session open text is not the client nonce.");
        }
        if (clientNonceBytes.Length != 16)
        {
            return await Reject("Session open text is not the client nonce.");
        }

        var stale = await _db.SessionChallenges
            .Where(c => c.ConnectionId == connection.ConnectionId && !c.Verified && !c.Consumed)
            .ToListAsync();
        foreach (var old in stale)
        {
            old.Consumed = true;
        }
        var serverNonce = Protocol.SessionChallenge.NewNonce();
        var challengeItem = await _deliveries.QueueForConnectionAsync(
            connection, Protocol.SessionChallenge.ChallengeKind, serverNonce, "SessionChallenge",
            envelopeClass: EnvelopeClass.Knock, deliveryMode: Envelope.DeliverySession);
        _db.SessionChallenges.Add(new Data.SessionChallenge
        {
            ConnectionId = connection.ConnectionId,
            ClientNonce = clientNonce,
            ServerNonce = serverNonce,
            ChallengePacketId = challengeItem.PacketId,
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.AddSeconds(Protocol.SessionChallenge.ChallengeLifetimeSeconds)
        });
        inbox.Status = "Recorded";
        inbox.RecordedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await TrailAsync(inbox.PacketId, connection.ConnectionId, "SessionChallengeIssued",
            "Knock accepted; a challenge sealed to the member key waits in the outbox. No session is open yet.");
        return new IntakeReceipt(true, inbox.PacketId, null);
    }

    // Contract section 7, steps 3 and 4: the response is an
    // acknowledgement packet whose proof object the node verifies
    // against the exchange it started: both nonces, the proof
    // signature under the member key, and the expiry. Only then
    // is the session open.
    private async Task<IntakeReceipt> ProcessSessionProofAsync(
        PacketInbox inbox, HouseConnection connection, PlainPacket plain)
    {
        async Task<IntakeReceipt> Reject(string reason)
        {
            inbox.Status = "Rejected";
            inbox.RejectionReason = reason;
            await _db.SaveChangesAsync();
            await TrailAsync(inbox.PacketId, inbox.ConnectionId, "Rejected", reason);
            return new IntakeReceipt(false, inbox.PacketId, reason);
        }

        var challenge = await _db.SessionChallenges
            .Where(c => c.ConnectionId == connection.ConnectionId && !c.Verified && !c.Consumed)
            .OrderByDescending(c => c.CreatedAtUtc)
            .FirstOrDefaultAsync();
        if (challenge is null)
        {
            return await Reject("No outstanding session challenge for this connection.");
        }
        if (challenge.ExpiresAtUtc <= DateTime.UtcNow)
        {
            challenge.Consumed = true;
            await _db.SaveChangesAsync();
            return await Reject("The session challenge has expired; knock again.");
        }
        var ackedPacketId = plain.Body.GetProperty("acked_packet_id").GetString() ?? "";
        if (ackedPacketId != challenge.ChallengePacketId)
        {
            return await Reject("The session response does not name the challenge packet it answers.");
        }
        try
        {
            Protocol.SessionChallenge.VerifyProof(
                plain.Proof!.Value, connection.ConnectionId,
                challenge.ClientNonce, challenge.ServerNonce,
                connection.PublicKeyBase64Url);
        }
        catch (Exception ex) when (ex is ProtocolException or JsonException or FormatException or CryptographicException)
        {
            return await Reject("Session proof did not verify: " + ex.Message);
        }
        challenge.Verified = true;
        challenge.VerifiedAtUtc = DateTime.UtcNow;
        challenge.Consumed = true;
        connection.LastSessionAtUtc = DateTime.UtcNow;
        inbox.Status = "Recorded";
        inbox.RecordedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await TrailAsync(inbox.PacketId, connection.ConnectionId, "SessionOpened",
            "Proof signature verified under the enrolled key; the session is open.");
        return new IntakeReceipt(true, inbox.PacketId, null);
    }

    // An acknowledgement answers a delivery the node sent this
    // connection's person. Disposition received records receipt.
    // On a proposal delivery, disposition confirmed or declined
    // disposes the proposal through the same ConfirmationService
    // or PlanAssemblyService a house surface uses, with channel
    // "federated" on the audit row (ProposalResponse valve). Every
    // acknowledgement is recorded as reported, not observed: the
    // node warrants the packet arrived on this person's confirmed
    // connection asserting it, nothing stronger.
    private async Task<IntakeReceipt> ProcessAcknowledgementAsync(
        PacketInbox inbox, HouseConnection connection, PlainPacket plain)
    {
        async Task<IntakeReceipt> Reject(string reason)
        {
            inbox.Status = "Rejected";
            inbox.RejectionReason = reason;
            await _db.SaveChangesAsync();
            await TrailAsync(inbox.PacketId, inbox.ConnectionId, "Rejected", reason);
            return new IntakeReceipt(false, inbox.PacketId, reason);
        }

        var ackedPacketId = plain.Body.GetProperty("acked_packet_id").GetString() ?? "";
        var disposition = plain.Body.GetProperty("disposition").GetString() ?? "";
        var item = await _db.SealedOutbox
            .FirstOrDefaultAsync(o => o.PacketId == ackedPacketId);
        if (item is null || item.ToPersonId != connection.PersonId)
        {
            return await Reject("No delivery by that packet id was sent to this packet's person.");
        }

        if (item.SourceKind == "Proposal" && disposition is "confirmed" or "declined")
        {
            var valveFailure = await CheckValveAsync(connection, "ProposalResponse");
            if (valveFailure is not null)
            {
                return await Reject(valveFailure);
            }
            if (item.SourceEventId is int eventId)
            {
                var action = await _db.EventActions.FirstOrDefaultAsync(a => a.EventId == eventId);
                if (action is null || action.TargetPersonId != connection.PersonId)
                {
                    return await Reject("That proposal is not addressed to this packet's person.");
                }
                if (disposition == "confirmed")
                {
                    await _confirmations.ConfirmAsync(eventId, connection.PersonId, "federated");
                }
                else
                {
                    await _confirmations.DeclineAsync(eventId, connection.PersonId, "federated");
                }
            }
            else if (item.SourcePlanElementId is int elementId)
            {
                var element = await _db.PlanElements.FirstOrDefaultAsync(e => e.Id == elementId);
                if (element is null
                    || !PlanAssemblyService.RequiredDisposers(element).Contains(connection.PersonId))
                {
                    return await Reject("That plan element is not addressed to this packet's person.");
                }
                await _plans.RecordDisposalAsync(
                    elementId, connection.PersonId,
                    disposition == "confirmed" ? PlanDisposal.Agreed : PlanDisposal.Declined);
            }
            else
            {
                return await Reject("That proposal delivery names nothing disposable.");
            }
        }

        item.Status = "Acknowledged";
        item.AcknowledgedAtUtc = DateTime.UtcNow;
        item.AckPacketId = inbox.PacketId;
        inbox.Status = "Recorded";
        inbox.RecordedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await TrailAsync(inbox.PacketId, connection.ConnectionId, "Acknowledged",
            item.SourceKind == "Proposal"
                ? $"Proposal disposed as {disposition} through the node's own services. Recorded as reported, not observed."
                : $"Delivery acknowledgement ({disposition}) recorded as reported, not observed.");
        return new IntakeReceipt(true, inbox.PacketId, null);
    }

    // The valve gate, node side under the frozen contract: a
    // standing Consent this person holds, for the kind's material
    // class, created by read back, unexpired and unrevoked. No
    // Consent in force means the valve is closed and the packet
    // is rejected whole. (The frozen plain packet carries no
    // valve field; see DoorService for the mapping and its flag.)
    private async Task<string?> CheckValveAsync(HouseConnection connection, string materialClass)
    {
        var now = DateTime.UtcNow;
        var open = await _db.ConsentRecords.AnyAsync(c =>
            c.PersonId == connection.PersonId
            && c.MaterialClass == materialClass
            && c.CreatedByReadBack
            && c.RevokedAtUtc == null
            && c.ExpiresAtUtc > now);
        return open
            ? null
            : $"Valve closed: this person holds no standing Consent in force for {materialClass}. The packet is rejected whole.";
    }

    private async Task TrailAsync(string packetId, string? connectionId, string outcome, string? reason, int? eventId = null)
    {
        _db.PacketTrailLines.Add(new PacketTrailLine
        {
            PacketId = packetId,
            ConnectionId = connectionId,
            Direction = "In",
            Outcome = outcome,
            Reason = reason,
            EventId = eventId,
            AtUtc = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
    }
}
