using System.Security.Cryptography;
using System.Text.Json;
using HomeNode.Data;
using HomeNode.Protocol;
using HomeNode.Services.Extraction;
using Microsoft.EntityFrameworkCore;

namespace HomeNode.Services;

// Sealed intake on the frozen protocol contract.
//
// A wire packet is exactly ciphertext, envelope, signature. The
// envelope is exactly the eight relay-visible fields. Intake runs in the
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
// traffic, flagged in the open). After opening, the
// declared class is checked again against the packet's actual
// kind, the standing, and the session state, and a
// question that resolves to record entries needs the telling
// permission to write them.
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
    private readonly PacketAgeBound _ageBound;

    public SealedIntakeService(
        AppDbContext db,
        NodeKeyStore nodeKeys,
        ExtractionContextBuilder contextBuilder,
        ExtractionService extraction,
        ConfirmationService confirmations,
        PlanAssemblyService plans,
        SealedDeliveryService deliveries,
        PacketAgeBound? ageBound = null)
    {
        _db = db;
        _nodeKeys = nodeKeys;
        _contextBuilder = contextBuilder;
        _extraction = extraction;
        _confirmations = confirmations;
        _plans = plans;
        _deliveries = deliveries;
        // The stale-issue bound. Program.cs registers the
        // instance built from configuration (Intake:MaxPacketAgeDays);
        // a caller that constructs this service by hand gets the
        // shipped default, which is also what the configuration
        // falls back to.
        _ageBound = ageBound ?? PacketAgeBound.ShippedDefault;
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
        catch (Exception ex) when (ex is ProtocolException or JsonException or FormatException or CryptographicException or InvalidOperationException)
        {
            // InvalidOperationException is the JSON reader's kind
            // mismatch signal: a malformed packet is a
            // rejection with a receipt, never a throw, because the
            // relay loop's caller has already been handed (and the
            // relay has already dropped) the whole batch.
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
        // the first copy is still queued unopened, with one
        // exception: a packet whose first copy is
        // still sitting in this node's own inbox, queued and
        // unrecorded, has recorded nothing yet, so a re-delivery
        // of that same packet is the same in-flight packet, not a
        // replay. It returns the existing queue entry and adds no
        // second one (the inbox packet_id index is unique). Once
        // the packet is recorded or rejected, or if it never
        // reached the inbox, a second copy is a replay, full stop.
        if (await _db.PacketSeen.AnyAsync(s => s.PacketId == envelope.PacketId))
        {
            var existing = await _db.PacketInbox
                .FirstOrDefaultAsync(p => p.PacketId == envelope.PacketId);
            if (existing is { Status: "QueuedUnrecorded" })
            {
                return new IntakeReceipt(true, envelope.PacketId, null);
            }
            return await Reject("Replay rejected: packet_id already seen.");
        }
        _db.PacketSeen.Add(new PacketSeen
        {
            PacketId = envelope.PacketId,
            ConnectionId = envelope.ConnectionId,
            SeenAtUtc = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        // The stale-issue bound, a node policy next to the
        // seen registry (the packet_id is consumed either way, like
        // every other rejection past this point, so a re-posted
        // copy of a stale packet meets the replay rule rather than
        // landing). The check sits before the cryptographic steps:
        // an era-old packet is refused without paying for its
        // verification, and the reason goes on the trail like every
        // other intake refusal. When the bound is disabled this is
        // a no-op and intake behaves exactly as before. See
        // PacketAgeBound for the one named value and its basis.
        if (_ageBound.Rejects(envelope.IssuedAt, DateTime.UtcNow))
        {
            return await Reject("Stale packet rejected: issued_at is older than the intake bound of "
                + _ageBound.Describe() + " (Intake:MaxPacketAgeDays).");
        }

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

        async Task<IntakeReceipt> Reject(string reason)
        {
            inbox.Status = "Rejected";
            inbox.RejectionReason = reason;
            await _db.SaveChangesAsync();
            await TrailAsync(packetId, inbox.ConnectionId, "Rejected", reason);
            return new IntakeReceipt(false, packetId, reason);
        }

        // The stored wire is re-parsed here, and a parse failure
        // is a terminal rejection like any other malformed packet:
        // the row was validated before it was stored, so wire that
        // no longer parses was damaged after intake (a corrupted
        // store), and leaving it queued would retry the same throw
        // on every processing pass forever, never landing and never
        // closing. The hand-crafted shape is already refused this
        // way at the open step below; the stored shape gets the
        // same ending.
        Packet packet;
        try
        {
            packet = Packet.Parse(inbox.WireJson);
        }
        catch (Exception ex) when (ex is ProtocolException or JsonException or FormatException or CryptographicException or InvalidOperationException)
        {
            return await Reject("The stored wire no longer parses as a contract packet; the queue row was damaged after intake: " + ex.Message);
        }

        // Step 6 and the plain half of step 7: Packet.Open runs
        // the seal with AAD Envelope.AadBytes(), parses the plain
        // packet, and validates its ids against the envelope.
        PlainPacket plain;
        try
        {
            plain = packet.Open(_nodeKeys.Keypair);
        }
        catch (Exception ex) when (ex is ProtocolException or JsonException or FormatException or CryptographicException or InvalidOperationException)
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
        // connection, checked after opening. The check runs here,
        // but the use is only COMMITTED once the packet has been
        // recorded. The old order wrote the nonce
        // before dispatch, so a transient failure downstream (a
        // model API error during extraction, say) burned the
        // nonce of a packet that had recorded nothing, and its
        // retry was rejected as a replay of itself: the telling
        // was lost for good. Replay protection is unchanged for
        // anything that WAS recorded: a recorded packet's nonce
        // is on file, and any other packet carrying it is
        // rejected here. A nonce row naming this same packet_id
        // can only be a leftover of this packet's own earlier
        // attempt; it is not a replay either.
        var existingNonceUse = await _db.PacketNonceUses.FirstOrDefaultAsync(n =>
            n.ConnectionId == connection.ConnectionId && n.PacketNonce == plain.PacketNonce);
        if (existingNonceUse is not null && existingNonceUse.PacketId != packetId)
        {
            return await Reject("Replay rejected: packet nonce already used on this connection.");
        }

        inbox.ToldAtUtc = DateTimeOffset.FromUnixTimeSeconds(plain.ToldAt).UtcDateTime;

        // The declared envelope class is re-checked
        // here, after opening, against what the packet actually
        // is and against the connection's standing and session
        // state at processing time. At intake (ReceiveWireAsync)
        // the class can only be compared to the envelope; the
        // packet is still sealed, so a telling declared as knock
        // class sailed past the session gate, a KnockOnly
        // connection recorded tellings with the same trick, and a
        // session class telling was recorded when a challenge had
        // merely been issued with no proof ever sent. The rules:
        // knock class is acceptable only for the session open
        // exchange itself; session class requires a session
        // opened by a verified proof, the one exception being the
        // proof response itself (verified in
        // ProcessSessionProofAsync against the outstanding
        // challenge); store_and_forward requires a completed
        // session open at least once; and a KnockOnly connection
        // may run nothing but the knock exchange that
        // re-confirms it, whatever class it declares.
        var classFailure = await CheckPacketClassAsync(packet.Envelope.Class, connection, plain);
        if (classFailure is not null)
        {
            return await Reject(classFailure);
        }

        var receipt = plain.Type switch
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
        if (receipt.Accepted && existingNonceUse is null)
        {
            _db.PacketNonceUses.Add(new PacketNonceUse
            {
                ConnectionId = connection.ConnectionId,
                PacketNonce = plain.PacketNonce,
                PacketId = packetId,
                UsedAtUtc = DateTime.UtcNow
            });
            await _db.SaveChangesAsync();
        }
        return receipt;
    }

    // The packet class check, run after opening (see
    // ProcessPacketAsync). Returns the rejection reason, or null
    // when the declared class matches the packet's actual work
    // and the connection's standing and session state allow it.
    private async Task<string?> CheckPacketClassAsync(
        EnvelopeClass envelopeClass, HouseConnection connection, PlainPacket plain)
    {
        var isSessionOpen = plain.Type == PacketType.Telling
            && plain.Body.TryGetProperty("kind", out var kindNode)
            && kindNode.GetString() == Protocol.SessionChallenge.OpenKind;
        if (connection.Standing == ConnectionStanding.KnockOnly)
        {
            return isSessionOpen && envelopeClass == EnvelopeClass.Knock
                ? null
                : "Connection stands KnockOnly; after opening, only the knock (session open) exchange is acceptable from it.";
        }
        switch (envelopeClass)
        {
            case EnvelopeClass.Knock:
                return isSessionOpen
                    ? null
                    : "Knock class is acceptable only for the session open exchange; this packet is not one.";
            case EnvelopeClass.Session:
                if (await HasOpenSessionAsync(connection.ConnectionId))
                {
                    return null;
                }
                // The session open response itself: an
                // acknowledgement carrying a proof, verified
                // against the outstanding challenge when it is
                // processed. Anything else in session class with
                // no verified session (a challenge merely issued
                // opens nothing) is rejected.
                return plain.Type == PacketType.Acknowledgement && plain.Proof is not null
                    ? null
                    : "No open session for this connection; a session class packet requires a session opened by a verified proof.";
            case EnvelopeClass.StoreAndForward:
                return await HasEverOpenedSessionAsync(connection.ConnectionId)
                    ? null
                    : "This connection has never completed a session open; queued traffic waits for one.";
            default:
                return $"Envelope class {envelopeClass} is not one this house takes in.";
        }
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
            // Per-packet isolation: one
            // packet's failure, transient or otherwise, must not
            // abort the rest of the batch. A packet whose
            // processing throws (a model API error during
            // extraction is the live case) stays QueuedUnrecorded,
            // so a later pass retries it; the failure is written
            // to the trail, never silently dropped, and the
            // remaining packets are still processed in this pass.
            try
            {
                var receipt = await ProcessPacketAsync(packetId);
                if (receipt.Accepted)
                {
                    recorded++;
                }
            }
            catch (Exception ex)
            {
                try
                {
                    await TrailAsync(packetId, null, "ProcessingFailed",
                        "Processing failed and the packet stays queued for a later pass: " + ex.Message);
                }
                catch
                {
                    // The trail write shares the store that may
                    // have caused the failure; failing to log must
                    // not lose the rest of the batch either.
                }
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
        // The extraction clock is the household's clock.
        // The front door converts the telling instant through the
        // household zone; this path used to hand extraction the
        // UTC instant with a zero offset, so a federated "by six"
        // told at 1:17 PM Chicago was resolved against 6:17 PM
        // UTC and rolled to tomorrow. The told instant is the
        // same; the frame it is read in is the household's zone,
        // exactly as the front door frames it.
        var householdRow = await _db.Households.FirstAsync(h => h.Id == connection.HouseholdId);
        TimeZoneInfo householdZone;
        try
        {
            householdZone = TimeZoneInfo.FindSystemTimeZoneById(householdRow.TimeZoneId ?? "America/Chicago");
        }
        catch (TimeZoneNotFoundException)
        {
            householdZone = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
        }
        var input = new ExtractionInput
        {
            UtteranceId = $"pkt-{inbox.PacketId}"[..Math.Min(26, inbox.PacketId.Length + 4)],
            Transcript = text.Trim(),
            SourceType = "federated",
            // Two clocks, kept apart.
            // OccurredAtLocal is the TOLD instant on the household
            // clock: due times in the words ("by six") resolve
            // against the household's own day at the moment of
            // telling. RecordedAtUtc is the node's
            // RECEIPT instant: events are written with it as their
            // record time, so the timeline order and the 24 hour
            // proposal lapse run from when the house received the
            // telling, never from the sender's clock.
            // The sender's own time is kept exactly where it
            // belongs: on the event's ToldAtUtc, written from the
            // packet below.
            OccurredAtLocal = TimeZoneInfo.ConvertTime(
                new DateTimeOffset(
                    DateTime.SpecifyKind(inbox.ToldAtUtc!.Value, DateTimeKind.Utc), TimeSpan.Zero),
                householdZone),
            RecordedAtUtc = inbox.ReceivedAtUtc,
            Speaker = new ExtractionPersonRef
            {
                PersonId = person.Id,
                DisplayName = person.DisplayName,
                Role = person.Role.ToString()
            },
            Context = context
        };

        // The Question valve admits asking,
        // not writing. The same words the OneLiner valve rejects
        // as a telling ("Toilet paper is low." with that Consent
        // closed) wrote a supply record entry when they arrived
        // as a question, because extraction writes as it goes.
        // A question therefore runs inside a transaction: if
        // extraction resolves it to record entries and the person
        // holds no OneLiner Consent in force, the transaction
        // rolls back, nothing is written, and the packet is
        // rejected whole, the same shape the valve gives a
        // telling (a closed valve for a material class rejects
        // the packet whole for that class). A question that
        // resolves to no entries (a lookup, a clarification) is
        // answered as before, and a question from a person who
        // does hold the telling permission may write under it.
        ExtractionResult result;
        if (kind == "question")
        {
            var preExisting = _db.ChangeTracker.Entries()
                .Select(e => e.Entity).ToHashSet();
            await using var questionTx = await _db.Database.BeginTransactionAsync();
            try
            {
                result = await _extraction.ProcessAsync(input);
            }
            catch
            {
                await questionTx.RollbackAsync();
                throw;
            }
            if (result.WrittenEvents.Count > 0)
            {
                var writingFailure = await CheckValveAsync(connection, "OneLiner");
                if (writingFailure is not null)
                {
                    await questionTx.RollbackAsync();
                    // Extraction's rows live on this context inside
                    // the transaction; after a rollback they must
                    // not ride a later save. Everything extraction
                    // created is detached (a saved row reads as
                    // Unchanged in the tracker, so state alone
                    // cannot find them; the pre-extraction snapshot
                    // can), and anything pre-existing it modified
                    // (a supply count) is reloaded from the record.
                    // The inbox row is kept: Reject below records
                    // its outcome.
                    foreach (var entry in _db.ChangeTracker.Entries().ToList())
                    {
                        if (entry.Entity is PacketInbox)
                        {
                            continue;
                        }
                        if (!preExisting.Contains(entry.Entity))
                        {
                            entry.State = EntityState.Detached;
                        }
                        else if (entry.State == EntityState.Modified)
                        {
                            await entry.ReloadAsync();
                        }
                    }
                    return await Reject(
                        "Valve closed: this question resolves to a record entry, and writing requires the OneLiner permission. "
                        + writingFailure);
                }
            }
            await questionTx.CommitAsync();
        }
        else
        {
            result = await _extraction.ProcessAsync(input);
        }

        var recordedAt = DateTime.UtcNow;
        foreach (var ev in result.WrittenEvents)
        {
            // The record's provenance fields (names and
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
        // An acknowledged alert is the moment its entry
        // completes. The dispatcher queued the alert and left the
        // entry Confirmed, owed and visible; only this
        // acknowledgement, from the member the alert was sealed
        // to, moves it to Done, through the same confirmation
        // service every other transition uses.
        if (item.SourceKind == "Alert" && item.SourceEventId is int alertEventId)
        {
            var alertEvent = await _db.Events.FirstOrDefaultAsync(e => e.Id == alertEventId);
            if (alertEvent is { ConfirmationState: ConfirmationState.Confirmed })
            {
                await _confirmations.CompleteAsync(alertEventId);
            }
        }
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
