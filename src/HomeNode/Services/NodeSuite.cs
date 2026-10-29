using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HomeNode.Data;
using HomeNode.Protocol;
using HomeNode.Services.Extraction;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SessionChallenge = HomeNode.Protocol.SessionChallenge;

namespace HomeNode.Services;

// The home node suite, run with: dotnet run -- --node-suite
// It exercises the home node's own work against the frozen
// protocol contract and a scratch slice:
//   - the shared test vectors, reproduced and opened byte for
//     byte from the vectors file;
//   - the door (invite, knock, inviter card, check phrase,
//     explicit disposal, registry fingerprint in the protocol
//     format: hex SHA-256 of the 65 byte public point);
//   - the session open exchange (knock, challenge, proof) and
//     its failures;
//   - sealed intake in the contract's section 8 order (seen
//     registry at the envelope step, hash recomputation, enrolled
//     key signature, AAD Envelope.AadBytes, nonce single use
//     after opening), queuing unrecorded and then running the
//     same pipeline as a front door telling, with the record's
//     provenance fields written by the intake;
//   - the Consent valve as standing node side state (the frozen
//     plain packet carries no valve field), both ways;
//   - proposal disposal riding acknowledgement dispositions
//     against node proposal deliveries;
//   - the sealed delivery outbox replacing in-database delivery
//     for remote members, per reader sealing, acknowledgements
//     recorded as reported, not observed;
//   - told time versus recorded time;
//   - the relay client wire rebuild (the relay's poll shape);
//   - the seeded record's clock: a fresh database seeded
//     through the real DbSeeder, checked for its pristine counts,
//     for the closing whereabouts stored as its true UTC instant,
//     and for the whereabouts lookup naming the scenario's clock
//     time for that closing statement.
// The regression harness is the separate --demo-suite run (the
// house layer's 103 checks unmodified, plus the 30 audience
// default checks); this suite adds to it, never edits it.
public static class NodeSuite
{
    private static int _passed;
    private static int _failed;
    private static readonly List<string> _failures = new();

    public static async Task<int> RunAsync()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"tellhouse-node-{Guid.NewGuid():N}.db");
        var keyDir = Path.Combine(Path.GetTempPath(), $"tellhouse-node-keys-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable("HOME_NODE_KEYSTORE", keyDir);
        var nodeKeys = new NodeKeyStore(keyDir);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));
        services.AddScoped<ConfirmationService>();
        services.AddScoped<AudienceDecisionService>();
        services.AddSingleton<IExtractionClient>(new MockExtractionClient());
        services.AddScoped<ExtractionContextBuilder>();
        services.AddScoped<ExtractionService>();
        services.AddScoped<PlanAssemblyService>();
        services.AddScoped<MemberDeliveryService>();
        services.AddScoped<BriefService>();
        services.AddScoped<LookupService>();
        services.AddSingleton(nodeKeys);
        services.AddScoped<DoorService>();
        services.AddScoped<SealedDeliveryService>();
        services.AddScoped<SealedIntakeService>();

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        await db.Database.EnsureCreatedAsync();
        var householdId = SeedSlice(db);
        var endpoint = SealedDeliveryService.HouseholdEndpointFor(householdId);
        Console.WriteLine($"Node scratch slice seeded in {dbPath} (household {householdId}).");

        var door = sp.GetRequiredService<DoorService>();
        var intake = sp.GetRequiredService<SealedIntakeService>();
        var deliveries = sp.GetRequiredService<SealedDeliveryService>();
        var confirmations = sp.GetRequiredService<ConfirmationService>();
        var plans = sp.GetRequiredService<PlanAssemblyService>();
        var extraction = sp.GetRequiredService<ExtractionService>();
        var builder = sp.GetRequiredService<ExtractionContextBuilder>();
        var audience = sp.GetRequiredService<AudienceDecisionService>();

        await VectorChecksAsync();
        await SeededRecordChecksAsync();

        // ---------- The node key file is owner only ----------
        if (!OperatingSystem.IsWindows())
        {
            var nodeKeyPath = Path.Combine(keyDir, "node-content-key.txt");
            Check("Key file: the node key file is created owner read/write only (0600)",
                File.Exists(nodeKeyPath)
                && File.GetUnixFileMode(nodeKeyPath) == (UnixFileMode.UserRead | UnixFileMode.UserWrite));
            var looseKeyDir = Path.Combine(Path.GetTempPath(), $"tellhouse-node-keys-loose-{Guid.NewGuid():N}");
            Directory.CreateDirectory(looseKeyDir);
            var looseKeyPath = Path.Combine(looseKeyDir, "node-content-key.txt");
            File.WriteAllLines(looseKeyPath,
                new[] { nodeKeys.Keypair.PrivateKeyBase64Url, nodeKeys.Keypair.PublicKeyBase64Url });
            File.SetUnixFileMode(looseKeyPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            using var looseKeys = new NodeKeyStore(looseKeyDir);
            Check("Key file: a node key file left group/world readable is tightened to 0600 on load, and still loads",
                looseKeys.PublicKeyBase64Url == nodeKeys.PublicKeyBase64Url
                && File.GetUnixFileMode(looseKeyPath) == (UnixFileMode.UserRead | UnixFileMode.UserWrite));
        }

        // ---------- helpers ----------
        static bool ThrowsSync(Action act)
        {
            try { act(); return false; }
            catch (Exception ex) when (ex is ProtocolException or CryptographicException or InvalidOperationException) { return true; }
        }
        async Task<bool> ThrowsAsync(Func<Task> act)
        {
            try { await act(); return false; }
            catch (InvalidOperationException) { return true; }
        }

        string Wire(HouseConnection connection, IdentityKeypair keys, PlainPacket plain,
            EnvelopeClass cls = EnvelopeClass.Session, string mode = Envelope.DeliverySession)
        {
            var packet = Packet.Create(plain, endpoint, cls, mode,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(), nodeKeys.PublicKeyBase64Url, keys);
            return packet.ToCanonicalJson();
        }

        PlainPacket TellingPlain(HouseConnection connection, string senderPartyId, string kind, string text,
            long toldAt, string? nonce = null, string? packetId = null) =>
            new(PacketType.Telling, packetId ?? SealedDeliveryService.NewPacketId(), connection.ConnectionId,
                senderPartyId, NodeKeyStore.NodePartyId, toldAt,
                nonce ?? Base64Url.Encode(RandomNumberGenerator.GetBytes(16)),
                PlainPacket.TextBody(kind, text), null);

        async Task<HouseConnection> JoinAsync(int personId, string name, string partyId, IdentityKeypair keys)
        {
            var code = await door.CreateInviteAsync(householdId, 1, personId, "Standard");
            var request = await door.KnockAsync(code, name, partyId, "Stand-in Assistant", keys.PublicKeyBase64Url);
            return await door.ConfirmJoinAsync(request.Id, 1, personId, request.CheckPhrase);
        }

        async Task<bool> OpenSessionAsync(HouseConnection connection, IdentityKeypair keys, string senderPartyId)
        {
            var clientNonce = SessionChallenge.NewNonce();
            var knock = TellingPlain(connection, senderPartyId, SessionChallenge.OpenKind, clientNonce,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            var knockReceipt = await intake.ReceiveWireAsync(
                Wire(connection, keys, knock, EnvelopeClass.Knock, Envelope.DeliverySession));
            if (!knockReceipt.Accepted) return false;
            await intake.ProcessPacketAsync(knockReceipt.PacketId);
            var challengeItem = await db.SealedOutbox
                .Where(o => o.ConnectionId == connection.ConnectionId && o.SourceKind == "SessionChallenge")
                .OrderByDescending(o => o.CreatedAtUtc)
                .FirstOrDefaultAsync();
            if (challengeItem is null) return false;
            var challengePacket = Packet.Parse(challengeItem.WireJson);
            var challengePlain = challengePacket.Open(keys);
            var serverNonce = challengePlain.Body.GetProperty("text").GetString() ?? "";
            var proof = SessionChallenge.BuildProof(connection.ConnectionId, clientNonce, serverNonce, keys);
            var ack = new PlainPacket(PacketType.Acknowledgement, SealedDeliveryService.NewPacketId(),
                connection.ConnectionId, senderPartyId, NodeKeyStore.NodePartyId,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Base64Url.Encode(RandomNumberGenerator.GetBytes(16)),
                PlainPacket.AckBody(challengeItem.PacketId, "received"), proof);
            var ackReceipt = await intake.ReceiveWireAsync(Wire(connection, keys, ack));
            if (!ackReceipt.Accepted) return false;
            var processed = await intake.ProcessPacketAsync(ackReceipt.PacketId);
            return processed.Accepted && await intake.HasOpenSessionAsync(connection.ConnectionId);
        }

        var toldAt = new DateTimeOffset(2026, 10, 27, 13, 5, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var nowUnix = () => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // ---------- the door ----------
        using var lisaKeys = IdentityKeypair.Generate();
        var invite = await door.CreateInviteAsync(householdId, 1, 3, "Standard");
        var lisaRequest = await door.KnockAsync(invite, "Lisa Ellis", "member-lisa", "Stand-in Assistant", lisaKeys.PublicKeyBase64Url);
        Check("Door: an invite and a knock grant nothing; the request waits pending, no connection exists",
            lisaRequest.Status == "Pending" && await db.HouseConnections.CountAsync() == 0);
        Check("Door: a knock spends the invite; the same code knocks never twice",
            await ThrowsAsync(() => door.KnockAsync(invite, "Lisa Ellis", "member-lisa", "Stand-in Assistant", lisaKeys.PublicKeyBase64Url)));
        var card = await door.GetInviterCardAsync(lisaRequest.Id);
        Check("Door: the inviter card names the joiner, the provider labeled self-asserted, the check phrase, and the protocol fingerprint",
            card.ClaimedName == "Lisa Ellis" && card.ProviderClaimLabel == "self-asserted"
            && card.CheckPhrase == lisaRequest.CheckPhrase
            && card.Fingerprint == lisaKeys.Fingerprint
            && card.Fingerprint == DoorService.FingerprintOf(lisaKeys.PublicKeyBase64Url));
        Check("Door: a wrong check phrase confirms nothing",
            await ThrowsAsync(() => door.ConfirmJoinAsync(lisaRequest.Id, 1, 3, "not the phrase"))
            && await db.HouseConnections.CountAsync() == 0);
        var lisa = await door.ConfirmJoinAsync(lisaRequest.Id, 1, 3, lisaRequest.CheckPhrase);
        Check("Door: the named inviter confirming with the right phrase creates one Active connection, one public key and its fingerprint in the registry",
            lisa.Standing == ConnectionStanding.Active && lisa.PartyId == "member-lisa"
            && lisa.PublicKeyBase64Url == lisaKeys.PublicKeyBase64Url
            && lisa.Fingerprint == lisaKeys.Fingerprint
            && lisa.EnrollingInviterPersonId == 1);

        using var danielKeys = IdentityKeypair.Generate();
        var daniel = await JoinAsync(2, "Daniel Ellis", "member-daniel", danielKeys);
        Check("Door: a second member joins through the same door with a different fingerprint",
            daniel.Standing == ConnectionStanding.Active && daniel.Fingerprint != lisa.Fingerprint);

        using var declinedKeys = IdentityKeypair.Generate();
        var declinedInvite = await door.CreateInviteAsync(householdId, 1, 6, "Standard");
        var declinedRequest = await door.KnockAsync(declinedInvite, "June Alvarez", "member-june", "Stand-in Assistant", declinedKeys.PublicKeyBase64Url);
        await door.DeclineJoinAsync(declinedRequest.Id, 1);
        var declinedRow = await db.JoinRequests.FirstAsync(r => r.Id == declinedRequest.Id);
        Check("Door: declining is an explicit recorded disposal, and no connection came from it",
            declinedRow.Status == "Declined" && declinedRow.DecidedByPersonId == 1
            && !await db.HouseConnections.AnyAsync(c => c.PersonId == 6));
        var ghostConnection = new HouseConnection { ConnectionId = "conn-never-enrolled", HouseholdId = householdId };
        var ghostPlain = TellingPlain(ghostConnection, "member-june", "raw_telling", "June here, let me in.", nowUnix());
        var ghostReceipt = await intake.ReceiveWireAsync(Wire(ghostConnection, declinedKeys, ghostPlain));
        Check("Door: a packet offered after the inviter declined meets no enrolled key and is rejected",
            !ghostReceipt.Accepted);

        // ---------- valve state and session ----------
        await door.CreateConsentAsync(householdId, 3, "OneLiner", "FreeTextOneLiners", TimeSpan.FromDays(7), createdByReadBack: true);
        await door.CreateConsentAsync(householdId, 3, "ProposalResponse", "FreeTextOneLiners", TimeSpan.FromDays(7), createdByReadBack: true);

        var earlyTelling = TellingPlain(lisa, "member-lisa", "raw_telling",
            "We're low on wipes today; there are only a few left.", toldAt);
        var earlyReceipt = await intake.ReceiveWireAsync(Wire(lisa, lisaKeys, earlyTelling));
        Check("Session: a session class packet before any session open is rejected at intake",
            !earlyReceipt.Accepted);

        // A failed proof first: right knock, wrong server nonce in the proof.
        var badClientNonce = SessionChallenge.NewNonce();
        var badKnock = TellingPlain(lisa, "member-lisa", SessionChallenge.OpenKind, badClientNonce, nowUnix());
        var badKnockReceipt = await intake.ReceiveWireAsync(Wire(lisa, lisaKeys, badKnock, EnvelopeClass.Knock));
        await intake.ProcessPacketAsync(badKnockReceipt.PacketId);
        var badChallenge = await db.SealedOutbox
            .Where(o => o.ConnectionId == lisa.ConnectionId && o.SourceKind == "SessionChallenge")
            .OrderByDescending(o => o.CreatedAtUtc).FirstAsync();
        var badProof = SessionChallenge.BuildProof(lisa.ConnectionId, badClientNonce, SessionChallenge.NewNonce(), lisaKeys);
        var badAck = new PlainPacket(PacketType.Acknowledgement, SealedDeliveryService.NewPacketId(),
            lisa.ConnectionId, "member-lisa", NodeKeyStore.NodePartyId, nowUnix(),
            Base64Url.Encode(RandomNumberGenerator.GetBytes(16)),
            PlainPacket.AckBody(badChallenge.PacketId, "received"), badProof);
        var badAckReceipt = await intake.ReceiveWireAsync(Wire(lisa, lisaKeys, badAck));
        var badAckProcessed = await intake.ProcessPacketAsync(badAckReceipt.PacketId);
        Check("Session: a proof over the wrong server nonce opens nothing",
            !badAckProcessed.Accepted && !await intake.HasOpenSessionAsync(lisa.ConnectionId));

        Check("Session: the full knock, challenge, proof exchange opens the session",
            await OpenSessionAsync(lisa, lisaKeys, "member-lisa"));
        Check("Session: the second member opens a session the same way",
            await OpenSessionAsync(daniel, danielKeys, "member-daniel"));

        // ---------- the happy telling, queued then recorded ----------
        var eventsBefore = await db.Events.CountAsync();
        var tellingNonce = Base64Url.Encode(RandomNumberGenerator.GetBytes(16));
        var telling = TellingPlain(lisa, "member-lisa", "raw_telling",
            "We're low on wipes today; there are only a few left.", toldAt, nonce: tellingNonce);
        var tellWire = Wire(lisa, lisaKeys, telling);
        var tellPacket = Packet.Parse(tellWire);
        var tellReceipt = await intake.ReceiveWireAsync(tellWire);
        var inboxRow = await db.PacketInbox.FirstAsync(p => p.PacketId == tellPacket.Envelope.PacketId);
        Check("Intake: a valid sealed telling is taken in queued and marked unrecorded; no event exists yet",
            tellReceipt.Accepted && inboxRow.Status == "QueuedUnrecorded"
            && inboxRow.RecordedAtUtc is null && await db.Events.CountAsync() == eventsBefore);
        var processReceipt = await intake.ProcessPacketAsync(tellPacket.Envelope.PacketId);
        var toldEvent = await db.Events.FirstAsync(e => e.ProvenancePacketId == tellPacket.Envelope.PacketId);
        Check("Intake: opening runs the same pipeline as a front door telling (supply resolves, author from enrollment)",
            processReceipt.Accepted && toldEvent.RelatedSupplyItemId == 10
            && toldEvent.AuthorPersonId == 3 && toldEvent.Type == EventType.Supply);
        Check("Intake: the intake writes the record's provenance fields from the packet it opened",
            toldEvent.ProvenanceOrigin == "Lisa's assistant"
            && toldEvent.ProvenanceConnection == lisa.ConnectionId
            && ProvenanceRendering.Describe(toldEvent)
                == $"Came via Lisa's assistant, connection {lisa.ConnectionId}, packet {tellPacket.Envelope.PacketId}.");
        Check("Intake: told time is the packet's told_at; recorded time is when the node took it in, and they differ",
            toldEvent.ToldAtUtc == DateTimeOffset.FromUnixTimeSeconds(toldAt).UtcDateTime
            && toldEvent.RecordedAtUtc is not null && toldEvent.RecordedAtUtc > toldEvent.ToldAtUtc
            && toldEvent.ReportedNotObserved);
        var frontDoor = new HomeNode.Pages.FrontDoorModel(db, builder, extraction, confirmations, audience) { ViewerId = 1 };
        await frontDoor.OnGetAsync();
        Check("Intake: the front door recent list surfaces the federated event with its provenance on the event itself",
            frontDoor.RecentEvents.Any(e => e.Id == toldEvent.Id
                && ProvenanceRendering.Describe(e) is not null));

        // ---------- The proposal lapse runs on receipt time ----------
        // A member's proposal arrives carrying the sender's own
        // told_at, here 25 hours old. The 24 hour lapse clock runs
        // from when a proposal is presented to the house (node
        // receipt), never from the sender's clock: at the first
        // lapse sweep after receipt the proposal must still be
        // standing, with the sender time kept as told time only.
        var oldToldAt = nowUnix() - 25 * 3600;
        var staleProposal = TellingPlain(lisa, "member-lisa", "raw_telling",
            "My ankle hurts again.", oldToldAt);
        var staleReceipt = await intake.ReceiveWireAsync(Wire(lisa, lisaKeys, staleProposal));
        var staleProcessed = await intake.ProcessPacketAsync(staleReceipt.PacketId);
        var staleEvent = await db.Events.FirstAsync(e => e.ProvenancePacketId == staleReceipt.PacketId);
        Check("Proposal lapse: a federated proposal told 25 hours ago is recorded standing as a proposal, its sender time kept as told time",
            staleProcessed.Accepted && staleEvent.ConfirmationState == ConfirmationState.Proposed
            && staleEvent.ToldAtUtc == DateTimeOffset.FromUnixTimeSeconds(oldToldAt).UtcDateTime);
        Check("Proposal lapse: the proposal's record time is the node's receipt time, not the sender's clock",
            staleEvent.CreatedAtUtc > DateTime.UtcNow.AddMinutes(-5)
            && staleEvent.CreatedAtUtc <= DateTime.UtcNow.AddMinutes(1));
        var lapsedNow = await confirmations.LapseExpiredAsync(DateTime.UtcNow);
        var staleAfterLapse = await db.Events.FirstAsync(e => e.Id == staleEvent.Id);
        Check("Proposal lapse: the first lapse sweep after receipt does not lapse a proposal nobody has seen yet",
            lapsedNow == 0 && staleAfterLapse.ConfirmationState == ConfirmationState.Proposed);

        // ---------- negatives, in contract order ----------
        var replayReceipt = await intake.ReceiveWireAsync(tellWire);
        Check("Negative: a replayed packet_id is rejected at the envelope step, before opening",
            !replayReceipt.Accepted && (replayReceipt.Reason ?? "").Contains("packet_id"));

        var nonceReuse = TellingPlain(lisa, "member-lisa", "raw_telling",
            "Lisa called about supper.", toldAt, nonce: tellingNonce);
        var nonceReceipt = await intake.ReceiveWireAsync(Wire(lisa, lisaKeys, nonceReuse));
        var nonceProcessed = await intake.ProcessPacketAsync(nonceReceipt.PacketId);
        var nonceRow = await db.PacketInbox.FirstAsync(p => p.PacketId == nonceReceipt.PacketId);
        Check("Negative: a fresh packet_id carrying a packet nonce already used on this connection is rejected after opening",
            !nonceProcessed.Accepted && nonceRow.Status == "Rejected");

        var tamperPacket = Packet.Create(
            TellingPlain(lisa, "member-lisa", "raw_telling", "A telling that will be tampered with.", toldAt),
            endpoint, EnvelopeClass.Session, Envelope.DeliverySession, nowUnix(), nodeKeys.PublicKeyBase64Url, lisaKeys);
        var tamperedBlob = tamperPacket.CiphertextBytes();
        tamperedBlob[80] ^= 0xFF;
        var tamperedWire = new JsonObject
        {
            ["ciphertext"] = Base64Url.Encode(tamperedBlob),
            ["envelope"] = JsonNode.Parse(tamperPacket.Envelope.ToCanonicalJson()),
            ["signature"] = tamperPacket.SignatureBase64Url
        }.ToJsonString();
        var tamperReceipt = await intake.ReceiveWireAsync(tamperedWire);
        Check("Negative: a bit flipped ciphertext fails the recomputed hash at intake and never reaches the inbox",
            !tamperReceipt.Accepted && !await db.PacketInbox.AnyAsync(p => p.PacketId == tamperPacket.Envelope.PacketId));

        using var attackerKeys = IdentityKeypair.Generate();
        var forgedPlain = TellingPlain(lisa, "member-lisa", "raw_telling", "Forged telling.", toldAt);
        var forgedReceipt = await intake.ReceiveWireAsync(Wire(lisa, attackerKeys, forgedPlain));
        Check("Negative: a packet signed with a non-enrolled key is rejected at the signature step",
            !forgedReceipt.Accepted && !await db.PacketInbox.AnyAsync(p =>
                p.ConnectionId == lisa.ConnectionId && p.Status == "QueuedUnrecorded"
                && p.PacketId == forgedPlain.PacketId));

        var mismatch = TellingPlain(lisa, "member-daniel", "raw_telling", "I claim to be Daniel.", toldAt);
        var mismatchReceipt = await intake.ReceiveWireAsync(Wire(lisa, lisaKeys, mismatch));
        var eventsAtMismatch = await db.Events.CountAsync();
        var mismatchProcessed = await intake.ProcessPacketAsync(mismatchReceipt.PacketId);
        Check("Negative: a plain packet whose sender differs from the connection enrollment is rejected, no event",
            !mismatchProcessed.Accepted && await db.Events.CountAsync() == eventsAtMismatch);

        var structuredWire = BuildStructuredWire(lisa, lisaKeys, nodeKeys, endpoint);
        var structuredReceipt = await intake.ReceiveWireAsync(structuredWire);
        var eventsAtStructured = await db.Events.CountAsync();
        var structuredProcessed = await intake.ProcessPacketAsync(structuredReceipt.PacketId);
        Check("Negative: a structured body beyond kind and text is rejected whole before extraction",
            !structuredProcessed.Accepted && await db.Events.CountAsync() == eventsAtStructured);

        // ---------- the valve ----------
        var danielTelling = TellingPlain(daniel, "member-daniel", "raw_telling",
            "Daniel here. The porch light is on.", toldAt);
        var danielReceipt = await intake.ReceiveWireAsync(Wire(daniel, danielKeys, danielTelling));
        var eventsAtValve = await db.Events.CountAsync();
        var danielProcessed = await intake.ProcessPacketAsync(danielReceipt.PacketId);
        Check("Valve: with no standing Consent in force for the kind, the packet is rejected whole, one trail line",
            !danielProcessed.Accepted && await db.Events.CountAsync() == eventsAtValve
            && await db.PacketTrailLines.CountAsync(t => t.PacketId == danielReceipt.PacketId && t.Outcome == "Rejected") == 1);
        var danielConsent = await door.CreateConsentAsync(householdId, 2, "OneLiner", "FreeTextOneLiners",
            TimeSpan.FromDays(7), createdByReadBack: true);
        var danielTelling2 = TellingPlain(daniel, "member-daniel", "raw_telling",
            "We're low on gauze today; there is only one left.", toldAt);
        var danielReceipt2 = await intake.ReceiveWireAsync(Wire(daniel, danielKeys, danielTelling2));
        var danielProcessed2 = await intake.ProcessPacketAsync(danielReceipt2.PacketId);
        Check("Valve: a packet under a standing Consent the person holds is admitted",
            danielProcessed2.Accepted && await db.Events.CountAsync() == eventsAtValve + 1);
        await door.RevokeConsentAsync(danielConsent.ConsentId);
        var danielTelling3 = TellingPlain(daniel, "member-daniel", "raw_telling",
            "Daniel here. The porch light is still on.", toldAt);
        var danielReceipt3 = await intake.ReceiveWireAsync(Wire(daniel, danielKeys, danielTelling3));
        var eventsAtRevoke = await db.Events.CountAsync();
        var danielProcessed3 = await intake.ProcessPacketAsync(danielReceipt3.PacketId);
        Check("Valve: after revocation the valve is closed again, forward looking only",
            !danielProcessed3.Accepted && await db.Events.CountAsync() == eventsAtRevoke);
        await door.CreateConsentAsync(householdId, 3, "Question", "FreeTextOneLiners",
            TimeSpan.FromMinutes(-1), createdByReadBack: true);
        var expiredQuestion = TellingPlain(lisa, "member-lisa", "question", "Where's Daniel?", toldAt);
        var expiredReceipt = await intake.ReceiveWireAsync(Wire(lisa, lisaKeys, expiredQuestion));
        var expiredProcessed = await intake.ProcessPacketAsync(expiredReceipt.PacketId);
        Check("Valve: an expired Consent admits nothing",
            !expiredProcessed.Accepted);

        // ---------- proposal disposal by acknowledgement ----------
        await door.CreateConsentAsync(householdId, 2, "ProposalResponse", "FreeTextOneLiners",
            TimeSpan.FromDays(7), createdByReadBack: true);
        var proposalEvent = new Event
        {
            HouseholdId = householdId,
            Type = EventType.Request,
            Body = "Remind Daniel about the depot pickup.",
            SubjectPersonId = 2,
            AuthorPersonId = 1,
            CreatedAtUtc = DateTime.UtcNow,
            SensitivityClass = SensitivityClass.Household,
            AudienceRule = """{"Class":"Household"}"""
        };
        var proposalAction = new EventAction
        {
            ActionType = EventActionType.CreateReminder,
            TargetPersonId = 2,
            CreatedAtUtc = DateTime.UtcNow
        };
        await confirmations.ProposeAsync(proposalEvent, proposalAction, "tap");
        var proposalDelivery = await deliveries.QueueDeliveryAsync(householdId, 2,
            "The house asks: shall it remind you about the depot pickup?", "Proposal",
            sourceEventId: proposalEvent.Id, kind: "proposal");
        Check("Proposal: the node's proposal reaches a remote member as a sealed proposal delivery, not a database row",
            proposalDelivery is not null && proposalDelivery.SourceKind == "Proposal");

        PlainPacket AckPlain(HouseConnection connection, string senderPartyId, string ackedPacketId, string disposition) =>
            new(PacketType.Acknowledgement, SealedDeliveryService.NewPacketId(), connection.ConnectionId,
                senderPartyId, NodeKeyStore.NodePartyId, nowUnix(),
                Base64Url.Encode(RandomNumberGenerator.GetBytes(16)),
                PlainPacket.AckBody(ackedPacketId, disposition), null);

        var lisaAck = AckPlain(lisa, "member-lisa", proposalDelivery!.PacketId, "confirmed");
        var lisaAckReceipt = await intake.ReceiveWireAsync(Wire(lisa, lisaKeys, lisaAck));
        var lisaAckProcessed = await intake.ProcessPacketAsync(lisaAckReceipt.PacketId);
        var proposalAfterLisa = await db.Events.FirstAsync(e => e.Id == proposalEvent.Id);
        Check("Proposal: an acknowledgement from a person the delivery was not sent to disposes nothing",
            !lisaAckProcessed.Accepted && proposalAfterLisa.ConfirmationState == ConfirmationState.Proposed);
        var danielAck = AckPlain(daniel, "member-daniel", proposalDelivery.PacketId, "confirmed");
        var danielAckReceipt = await intake.ReceiveWireAsync(Wire(daniel, danielKeys, danielAck));
        var danielAckProcessed = await intake.ProcessPacketAsync(danielAckReceipt.PacketId);
        var proposalAfterDaniel = await db.Events.FirstAsync(e => e.Id == proposalEvent.Id);
        Check("Proposal: the addressed person's confirmed acknowledgement disposes it, audit row names the actor and the federated channel",
            danielAckProcessed.Accepted && proposalAfterDaniel.ConfirmationState == ConfirmationState.Confirmed
            && await db.EventAudits.CountAsync(a => a.EventId == proposalEvent.Id
                && a.Channel == "federated" && a.ActorPersonId == 2
                && a.ToState == nameof(ConfirmationState.Confirmed)) == 1);

        var plan = await plans.CreatePlanAsync(householdId, "Node plan", null);
        var element = await plans.AddElementAsync(plan.Id, PlanElementKind.MealProposal, "Soup", new[] { 2, 3 });
        var elementProposalDaniel = await deliveries.QueueDeliveryAsync(householdId, 2,
            "Dinner proposal: Soup. Agree?", "Proposal", sourcePlanElementId: element.Id, kind: "proposal");
        var elementProposalLisa = await deliveries.QueueDeliveryAsync(householdId, 3,
            "Dinner proposal: Soup. Agree?", "Proposal", sourcePlanElementId: element.Id, kind: "proposal");
        var danielElAck = AckPlain(daniel, "member-daniel", elementProposalDaniel!.PacketId, "confirmed");
        var danielElReceipt = await intake.ReceiveWireAsync(Wire(daniel, danielKeys, danielElAck));
        await intake.ProcessPacketAsync(danielElReceipt.PacketId);
        var elementAfterOne = await db.PlanElements.FirstAsync(e => e.Id == element.Id);
        Check("Proposal: one partner's packet agreement does not settle the element",
            elementAfterOne.State == PlanElementState.Open
            && await db.PlanAgreements.CountAsync(a => a.PlanElementId == element.Id && a.PersonId == 2) == 1);
        var lisaElAck = AckPlain(lisa, "member-lisa", elementProposalLisa!.PacketId, "confirmed");
        var lisaElReceipt = await intake.ReceiveWireAsync(Wire(lisa, lisaKeys, lisaElAck));
        await intake.ProcessPacketAsync(lisaElReceipt.PacketId);
        var elementAfterBoth = await db.PlanElements.FirstAsync(e => e.Id == element.Id);
        Check("Proposal: both partners' packets settle it, and the settlement lands as sealed outbox packets, not plaintext rows",
            elementAfterBoth.State == PlanElementState.Settled
            && await db.PlanDeliveries.CountAsync(d => d.PlanElementId == element.Id) == 0
            && await db.SealedOutbox.CountAsync(o => o.SourcePlanElementId == element.Id && o.SourceKind == "Plan") == 2);

        // ---------- sealed delivery, per reader ----------
        const string canary = "Canary for Daniel only: the blue kettle sings at dawn.";
        var outboxItem = await deliveries.QueueDeliveryAsync(householdId, 2, canary, "Delivery");
        Check("Delivery: a remote member's delivery is queued sealed; the canary appears nowhere in the stored packet",
            outboxItem is not null
            && !Encoding.UTF8.GetString(Base64Url.Decode(outboxItem!.CiphertextBase64)).Contains("blue kettle")
            && !outboxItem.EnvelopeJson.Contains("blue kettle"));
        Check("Delivery: the addressed member opens it with their own keypair, body verbatim",
            deliveries.OpenDelivery(outboxItem!, danielKeys) == canary);
        Check("Delivery: it does not open under another member's keypair",
            ThrowsSync(() => deliveries.OpenDelivery(outboxItem!, lisaKeys)));
        Check("Delivery: a packet sealed to Daniel does not open under the node's own keypair either (per reader sealing)",
            ThrowsSync(() => Packet.Parse(outboxItem!.WireJson).Open(nodeKeys.Keypair)));

        var receiptAck = AckPlain(daniel, "member-daniel", outboxItem!.PacketId, "received");
        var receiptAckWire = await intake.ReceiveWireAsync(Wire(daniel, danielKeys, receiptAck));
        var receiptAckProcessed = await intake.ProcessPacketAsync(receiptAckWire.PacketId);
        var outboxAfterAck = await db.SealedOutbox.FirstAsync(o => o.Id == outboxItem.Id);
        Check("Delivery: the member's received acknowledgement marks the delivery acknowledged, recorded as reported, not observed",
            receiptAckProcessed.Accepted && outboxAfterAck.Status == "Acknowledged"
            && outboxAfterAck.AckPacketId == receiptAckWire.PacketId
            && await db.PacketTrailLines.CountAsync(t => t.PacketId == receiptAckWire.PacketId
                && t.Outcome == "Acknowledged" && (t.Reason ?? "").Contains("reported, not observed")) == 1);

        // ---------- alert routing replaces in-database delivery ----------
        var alertEvent = new Event
        {
            HouseholdId = householdId,
            Type = EventType.HealthMention,
            Body = "Margaret's ankle is swollen and it hurts when she stands.",
            SubjectPersonId = 1,
            AuthorPersonId = 1,
            CreatedAtUtc = DateTime.UtcNow,
            SensitivityClass = SensitivityClass.Restricted,
            AudienceRule = """{"Class":"Restricted","RoutePersonId":2,"BackupPersonId":3}"""
        };
        var alertAction = new EventAction
        {
            ActionType = EventActionType.NotifyPerson,
            TargetPersonId = 2,
            CreatedAtUtc = DateTime.UtcNow
        };
        await confirmations.ProposeAsync(alertEvent, alertAction, "tap");
        await confirmations.ConfirmAsync(alertEvent.Id, 1, "tap");
        var alertOutbox = await db.SealedOutbox.FirstOrDefaultAsync(o => o.SourceEventId == alertEvent.Id);
        var alertAfter = await db.Events.FirstAsync(e => e.Id == alertEvent.Id);
        // Connection level: Daniel's connection is Standard, and the
        // inviter card for Standard says it cannot hear health
        // related entries. The earlier form of this check asserted
        // the leak: it expected the health alert to open under
        // Daniel's keypair. The fenced expectation is the opposite:
        // no delivery in either form, and the event stays Confirmed,
        // owed and visible, exactly as a delivery that names nobody
        // reachable does.
        Check("Connection level: a confirmed health alert is NOT delivered to a Standard connection (no sealed packet, no plaintext row, event stays Confirmed)",
            alertOutbox is null
            && await db.AlertDeliveries.CountAsync(d => d.EventId == alertEvent.Id) == 0
            && alertAfter.ConfirmationState == ConfirmationState.Confirmed);
        Check("Connection level: a direct health-class queue attempt to a Standard connection queues nothing",
            await deliveries.QueueDeliveryAsync(householdId, 2, alertEvent.Body, "Alert", sourceEventId: alertEvent.Id) is null
            && await db.SealedOutbox.CountAsync(o => o.SourceEventId == alertEvent.Id) == 0);

        // What Standard still allows: a non-health alert routed to
        // the same connection leaves sealed and completes.
        var taskAlertEvent = new Event
        {
            HouseholdId = householdId,
            Type = EventType.Request,
            Body = "Connection level non-health alert: it is Daniel's turn to take the trash out.",
            SubjectPersonId = 2,
            AuthorPersonId = 1,
            CreatedAtUtc = DateTime.UtcNow,
            SensitivityClass = SensitivityClass.Household,
            AudienceRule = """{"Class":"Household","RoutePersonId":2,"BackupPersonId":3}"""
        };
        var taskAlertAction = new EventAction
        {
            ActionType = EventActionType.NotifyPerson,
            TargetPersonId = 2,
            CreatedAtUtc = DateTime.UtcNow
        };
        await confirmations.ProposeAsync(taskAlertEvent, taskAlertAction, "tap");
        await confirmations.ConfirmAsync(taskAlertEvent.Id, 1, "tap");
        var taskAlertOutbox = await db.SealedOutbox.FirstOrDefaultAsync(o => o.SourceEventId == taskAlertEvent.Id);
        var taskAlertAfter = await db.Events.FirstAsync(e => e.Id == taskAlertEvent.Id);
        // Note: this check used to assert the event was
        // Done as soon as the sealed alert was queued. That was
        // a bug in the earlier expectation, not the design: a sealed alert
        // completes only when the member acknowledges it, which
        // the two checks here now assert in order.
        Check("Connection level: a Standard connection still receives a non-health alert as a sealed packet, event still Confirmed until acknowledged",
            taskAlertOutbox is not null && taskAlertOutbox.SourceKind == "Alert"
            && taskAlertAfter.ConfirmationState == ConfirmationState.Confirmed
            && deliveries.OpenDelivery(taskAlertOutbox, danielKeys) == taskAlertEvent.Body);
        var taskAlertAck = AckPlain(daniel, "member-daniel", taskAlertOutbox!.PacketId, "received");
        var taskAlertAckReceipt = await intake.ReceiveWireAsync(Wire(daniel, danielKeys, taskAlertAck));
        var taskAlertAckProcessed = await intake.ProcessPacketAsync(taskAlertAckReceipt.PacketId);
        var taskAlertDone = await db.Events.FirstAsync(e => e.Id == taskAlertEvent.Id);
        Check("Alert acknowledgement: the addressee's received acknowledgement completes the sealed alert's event",
            taskAlertAckProcessed.Accepted && taskAlertDone.ConfirmationState == ConfirmationState.Done);

        // ---------- question and sealed answer ----------
        await door.CreateConsentAsync(householdId, 3, "Question", "FreeTextOneLiners",
            TimeSpan.FromDays(7), createdByReadBack: true);
        var whereEvent = new Event
        {
            HouseholdId = householdId,
            Type = EventType.Whereabouts,
            Body = "I'm at Domino's, heading home after the store.",
            SubjectPersonId = 2,
            AuthorPersonId = 2,
            CreatedAtUtc = DateTime.UtcNow,
            SensitivityClass = SensitivityClass.Household,
            AudienceRule = """{"Class":"Household"}""",
            Details = """{"PlaceText":"Domino's","Motion":"HeadingHome","NextText":"the store"}"""
        };
        await confirmations.WriteStatementAsync(whereEvent, "tap");
        var eventsAtQuestion = await db.Events.CountAsync();
        var question = TellingPlain(lisa, "member-lisa", "question", "Where's Daniel?", toldAt);
        var questionReceipt = await intake.ReceiveWireAsync(Wire(lisa, lisaKeys, question));
        var questionProcessed = await intake.ProcessPacketAsync(questionReceipt.PacketId);
        var answerItem = await db.SealedOutbox
            .Where(o => o.ToPersonId == 3 && o.SourceKind == "Answer")
            .OrderByDescending(o => o.CreatedAtUtc)
            .FirstOrDefaultAsync();
        Check("Question: a question packet writes nothing and its narrow answer returns sealed to the asker only",
            questionProcessed.Accepted && await db.Events.CountAsync() == eventsAtQuestion
            && answerItem is not null
            && deliveries.OpenDelivery(answerItem, lisaKeys).Contains("Domino's"));

        // Audience fence on the stand-in path: the speaker for a packet is
        // the connection's enrolled person, and the pipeline fences
        // the question's slice by that person's stored audience.
        // A Restricted whereabouts of Margaret's (route Daniel;
        // Lisa is only the backup, outside the audience until a
        // delivery hops) is asked about by both.
        var fenceWhereEvent = new Event
        {
            HouseholdId = householdId,
            Type = EventType.Whereabouts,
            Body = "I'm at the clinic annex for my ankle.",
            SubjectPersonId = 1,
            AuthorPersonId = 1,
            CreatedAtUtc = DateTime.UtcNow,
            SensitivityClass = SensitivityClass.Restricted,
            AudienceRule = """{"Class":"Restricted","RoutePersonId":2,"BackupPersonId":3}"""
        };
        await confirmations.WriteStatementAsync(fenceWhereEvent, "tap");
        var fenceLisaQuestion = TellingPlain(lisa, "member-lisa", "question", "Where's Margaret?", toldAt);
        var fenceLisaReceipt = await intake.ReceiveWireAsync(Wire(lisa, lisaKeys, fenceLisaQuestion));
        var fenceLisaProcessed = await intake.ProcessPacketAsync(fenceLisaReceipt.PacketId);
        var fenceLisaAnswer = await db.SealedOutbox
            .Where(o => o.ToPersonId == 3 && o.SourceKind == "Answer")
            .OrderByDescending(o => o.Id)
            .FirstOrDefaultAsync();
        Check("Audience fence: a stand-in question from a person outside a Restricted audience is answered without its body text",
            fenceLisaProcessed.Accepted && fenceLisaAnswer is not null
            && !deliveries.OpenDelivery(fenceLisaAnswer, lisaKeys).Contains("clinic annex"));
        await door.CreateConsentAsync(householdId, 2, "Question", "FreeTextOneLiners",
            TimeSpan.FromDays(7), createdByReadBack: true);
        var fenceDanielQuestion = TellingPlain(daniel, "member-daniel", "question", "Where's Margaret?", toldAt);
        var fenceDanielReceipt = await intake.ReceiveWireAsync(Wire(daniel, danielKeys, fenceDanielQuestion));
        var fenceDanielProcessed = await intake.ProcessPacketAsync(fenceDanielReceipt.PacketId);
        var fenceDanielAnswer = await db.SealedOutbox
            .Where(o => o.ToPersonId == 2 && o.SourceKind == "Answer")
            .OrderByDescending(o => o.Id)
            .FirstOrDefaultAsync();
        Check("Audience fence: a stand-in question from the route person inside the audience is answered from the Restricted record",
            fenceDanielProcessed.Accepted && fenceDanielAnswer is not null
            && deliveries.OpenDelivery(fenceDanielAnswer, danielKeys).Contains("clinic annex"));

        // ---------- Live mode authorship ----------
        // A stub extraction client stands in for the Live model
        // (mock/local only, no live call): Lisa's assistant sends
        // "Daniel said he'll get the milk by five." and the stub
        // attributes the commitment to Daniel. Authorship is the
        // authenticated speaker (the
        // connection's enrolled person, Lisa), set in code; model
        // attribution may inform content (the subject) but never
        // authorship, the promise's maker, or the audit rows.
        var danielAttributionExtraction = new ExtractionService(
            db, new DanielAttributionClient(), confirmations, audience);
        var danielAttributionIntake = new SealedIntakeService(
            db, nodeKeys, builder, danielAttributionExtraction, confirmations, plans, deliveries);
        var keystoreTelling = TellingPlain(lisa, "member-lisa", "raw_telling",
            "Daniel said he'll get the milk by five.", toldAt);
        var keystoreReceipt = await danielAttributionIntake.ReceiveWireAsync(Wire(lisa, lisaKeys, keystoreTelling));
        var keystoreProcessed = await danielAttributionIntake.ProcessPacketAsync(keystoreReceipt.PacketId);
        var keystoreEvent = await db.Events.FirstOrDefaultAsync(e => e.ProvenancePacketId == keystoreReceipt.PacketId);
        Check("Live authorship: a telling the model attributes to Daniel is recorded with the authenticated speaker (Lisa) as its author, never the model's named author",
            keystoreProcessed.Accepted && keystoreEvent is not null
            && keystoreEvent.AuthorPersonId == 3 && keystoreEvent.SubjectPersonId == 2);
        var keystorePromise = keystoreEvent is null ? null
            : await db.Promises.FirstOrDefaultAsync(p => p.SourceEventId == keystoreEvent.Id);
        Check("Live authorship: the promise born of that telling names the speaker as its maker, and the audit rows credit the speaker, not Daniel",
            keystorePromise is not null && keystorePromise.PromisorPersonId == 3
            && keystoreEvent is not null
            && await db.EventAudits.CountAsync(a => a.EventId == keystoreEvent.Id
                && a.ObjectKind == "Promise" && a.ActorPersonId == 3) == 1
            && await db.EventAudits.CountAsync(a => a.EventId == keystoreEvent.Id
                && a.ActorPersonId == 2) == 0);
        var keystoreBriefs = new BriefService(db);
        var keystoreToday = DateOnly.FromDateTime(DateTime.Today);
        var keystoreLisaBrief = await keystoreBriefs.BuildBriefAsync(3, keystoreToday);
        var keystoreDanielBrief = await keystoreBriefs.BuildBriefAsync(2, keystoreToday);
        Check("Live authorship: the speaker can see her own telling, and no brief reads it as Daniel's promise",
            keystoreEvent is not null && BriefService.IsVisibleTo(keystoreEvent, 3)
            && keystoreLisaBrief.Any(l => l.Text.Contains("promised:") && l.Text.Contains("get the milk"))
            && !keystoreDanielBrief.Any(l => l.Text.Contains("Daniel promised") && l.Text.Contains("get the milk"))
            && !keystoreLisaBrief.Any(l => l.Text.Contains("Daniel promised") && l.Text.Contains("get the milk")));

        // ---------- /admin/propose state and audience ----------
        // The endpoint's operation (SealedDeliveryService.
        // QueueProposalAsync) must refuse a proposal whose entry
        // is no longer Proposed, and one whose addressed person is
        // outside the entry's stored audience, and must still
        // deliver a live proposal to a person inside its audience.
        async Task<Event> S4Event(string body, SensitivityClass cls, string rule,
            int authorId, int subjectId, int targetId)
        {
            var ev = new Event
            {
                HouseholdId = householdId,
                Type = EventType.HouseholdNote,
                Body = body,
                SubjectPersonId = subjectId,
                AuthorPersonId = authorId,
                CreatedAtUtc = DateTime.UtcNow,
                SensitivityClass = cls,
                AudienceRule = rule
            };
            var action = new EventAction
            {
                ActionType = EventActionType.CreateReminder,
                TargetPersonId = targetId,
                CreatedAtUtc = DateTime.UtcNow
            };
            await confirmations.ProposeAsync(ev, action, "tap");
            return ev;
        }

        var promisePersonal = await S4Event("A note stored for Daniel alone.",
            SensitivityClass.Personal, """{"Class":"Personal","AudiencePersonIds":[2]}""", 2, 2, 3);
        var (promisePersonalItem, promisePersonalRefusal) = await deliveries.QueueProposalAsync(promisePersonal, 3);
        Check("A proposal addressed to a person outside the entry's stored audience is refused and queues nothing",
            promisePersonalItem is null && promisePersonalRefusal == "outside-audience"
            && !await db.SealedOutbox.AnyAsync(o => o.SourceEventId == promisePersonal.Id));

        var promiseDone = await S4Event("A household entry already confirmed.",
            SensitivityClass.Household, """{"Class":"Household"}""", 1, 1, 3);
        await confirmations.ConfirmAsync(promiseDone.Id, 1, "tap");
        var (promiseDoneItem, promiseDoneRefusal) = await deliveries.QueueProposalAsync(promiseDone, 3);
        Check("An entry that is no longer a proposal (Confirmed) cannot be proposed again; its text is sent nowhere",
            promiseDoneItem is null && promiseDoneRefusal == "not-a-proposal"
            && !await db.SealedOutbox.AnyAsync(o => o.SourceEventId == promiseDone.Id && o.SourceKind == "Proposal"));

        var promiseLive = await S4Event("A live household proposal for Lisa.",
            SensitivityClass.Household, """{"Class":"Household"}""", 1, 1, 3);
        var (promiseLiveItem, promiseLiveRefusal) = await deliveries.QueueProposalAsync(promiseLive, 3);
        Check("A still Proposed entry addressed to a person inside its stored audience is queued sealed and opens for that person",
            promiseLiveItem is not null && promiseLiveRefusal is null
            && promiseLiveItem.SourceKind == "Proposal"
            && deliveries.OpenDelivery(promiseLiveItem, lisaKeys) == promiseLive.Body);

        // ---------- store and forward, and the relay wire shape ----------
        var queued = TellingPlain(lisa, "member-lisa", "raw_telling",
            "Lisa called about the garden gate.", toldAt);
        var queuedWire = Wire(lisa, lisaKeys, queued, EnvelopeClass.StoreAndForward, Envelope.DeliveryStoreAndForward);
        var queuedReceipt = await intake.ReceiveWireAsync(queuedWire);
        var queuedProcessed = await intake.ProcessPacketAsync(queuedReceipt.PacketId);
        Check("Carriage: a store_and_forward telling from a connection that has opened a session is taken in and recorded",
            queuedProcessed.Accepted
            && await db.Events.AnyAsync(e => e.ProvenancePacketId == queuedReceipt.PacketId));

        var originalPacket = Packet.Parse(queuedWire);
        var flat = new JsonObject
        {
            ["packet_id"] = originalPacket.Envelope.PacketId,
            ["household_endpoint"] = originalPacket.Envelope.HouseholdEndpoint,
            ["connection_id"] = originalPacket.Envelope.ConnectionId,
            ["envelope_class"] = "store_and_forward",
            ["delivery_mode"] = "store_and_forward",
            ["ciphertext_length"] = originalPacket.Envelope.CiphertextLength,
            ["issued_at"] = originalPacket.Envelope.IssuedAt,
            ["ciphertext_hash"] = originalPacket.Envelope.CiphertextHash,
            ["received_at"] = 1793275400,
            ["expires_at"] = 1793275700,
            ["ciphertext"] = originalPacket.CiphertextBase64Url,
            ["signature"] = originalPacket.SignatureBase64Url
        };
        var rebuilt = RelayClientService.RebuildWireJson(JsonDocument.Parse(flat.ToJsonString()).RootElement);
        Check("Relay: the node's poll client rebuilds the exact wire packet from the relay's flat poll shape, relay timing stripped",
            Packet.Parse(rebuilt).ToCanonicalJson() == originalPacket.ToCanonicalJson());

        // ---------- Test Send control ----------
        var payload = TestSendService.BuildTriggerPayload(
            "ts-test-1", DateTime.UtcNow, "lisa-telling-1");
        using (var doc = JsonDocument.Parse(payload))
        {
            var names = doc.RootElement.EnumerateObject().Select(p => p.Name)
                .OrderBy(n => n, StringComparer.Ordinal).ToList();
            Check("Test Send: the trigger payload carries exactly trigger, requestId, requestedAtUtc, and the act id the real stand-in endpoint requires",
                names.SequenceEqual(new[] { "act", "requestId", "requestedAtUtc", "trigger" }
                    .OrderBy(n => n, StringComparer.Ordinal)));
            Check("Test Send: the trigger payload has no content bearing field",
                !names.Any(n => n.Contains("text", StringComparison.OrdinalIgnoreCase)
                    || n.Contains("telling", StringComparison.OrdinalIgnoreCase)
                    || n.Contains("body", StringComparison.OrdinalIgnoreCase)
                    || n.Contains("content", StringComparison.OrdinalIgnoreCase)
                    || n.Contains("message", StringComparison.OrdinalIgnoreCase)
                    || n.Contains("words", StringComparison.OrdinalIgnoreCase)));
        }
        Check("Test Send: the service holds no database reference",
            !typeof(TestSendService).GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
                .Any(f => f.FieldType.Name.Contains("DbContext")));

        var stub = new StubTriggerHandler();
        var testSend = new TestSendService(
            new HttpClient(stub),
            new[]
            {
                new StandInEndpoint { Name = "LisaStandIn", PersonName = "Lisa", TriggerUrl = "http://standin.invalid/trigger", ActId = "lisa-telling-1" },
                new StandInEndpoint { Name = "BlankStandIn", PersonName = "Nobody", TriggerUrl = "", ActId = "lisa-telling-1" }
            });
        var unconfigured = await testSend.TriggerAsync("Nobody");
        Check("Test Send: an unconfigured stand-in sends nothing and reports unconfigured",
            unconfigured.Status == "unconfigured" && stub.Calls == 0);
        var blank = await testSend.TriggerAsync("BlankStandIn");
        Check("Test Send: a stand-in with no usable trigger address sends nothing and reports unconfigured",
            blank.Status == "unconfigured" && stub.Calls == 0);
        var sentAnswer = await testSend.TriggerAsync("LisaStandIn");
        Check("Test Send: the stand-in's sent answer passes through with its packet id",
            sentAnswer.Status == "sent" && sentAnswer.PacketId == "pkt-stub-1" && stub.Calls == 1);
        using (var doc = JsonDocument.Parse(stub.LastPayload!))
        {
            var wireNames = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
            Check("Test Send: what actually went on the wire names the act and carries no telling text",
                wireNames.Contains("act")
                && !wireNames.Any(n => n.Contains("telling", StringComparison.OrdinalIgnoreCase)
                    || n.Contains("text", StringComparison.OrdinalIgnoreCase)));
        }
        stub.ResponseBody = "{\"act\":\"lisa-telling-1\",\"packet_id\":\"pkt-stub-2\",\"result\":\"queued locally, unrecorded\"}";
        var queuedAnswer = await testSend.TriggerAsync("LisaStandIn");
        Check("Test Send: the stand-in's queued answer surfaces as queued-unrecorded, with its packet id",
            queuedAnswer.Status == "queued-unrecorded" && queuedAnswer.PacketId == "pkt-stub-2");
        stub.Throw = true;
        var unreachableAnswer = await testSend.TriggerAsync("LisaStandIn");
        Check("Test Send: an unreachable stand-in is reported unreachable",
            unreachableAnswer.Status == "unreachable");

        // ---------- member delivery acknowledgement ----------
        var memberDeliveries = sp.GetRequiredService<MemberDeliveryService>();
        async Task<(Event Ev, AlertDelivery Delivery)> SeedDeliveryAsync(string body)
        {
            var ev = new Event
            {
                HouseholdId = householdId,
                Type = EventType.HouseholdNote,
                Body = body,
                AuthorPersonId = 1,
                CreatedAtUtc = DateTime.UtcNow,
                SensitivityClass = SensitivityClass.Household,
                AudienceRule = """{"Class":"Household"}"""
            };
            db.Events.Add(ev);
            await db.SaveChangesAsync();
            var delivery = new AlertDelivery
            {
                EventId = ev.Id,
                LevelKey = "now",
                ToPersonId = 2,
                BodyText = body,
                DeliveredAtUtc = DateTime.UtcNow
            };
            db.AlertDeliveries.Add(delivery);
            await db.SaveChangesAsync();
            return (ev, delivery);
        }

        var (ackEvent, ackDelivery) = await SeedDeliveryAsync("A delivery Daniel acknowledges.");
        var ackStamped = await memberDeliveries.AcknowledgeDeliveryAsync(ackDelivery.Id, 2);
        var ackRow = await db.AlertDeliveries.FirstAsync(d => d.Id == ackDelivery.Id);
        Check("Delivery ack: the addressee's acknowledgement stamps the time and writes exactly one audit row naming them",
            ackStamped && ackRow.AcknowledgedAtUtc is not null
            && await db.EventAudits.CountAsync(a => a.ObjectKind == "AlertDelivery"
                && a.EventId == ackEvent.Id && a.ActorPersonId == 2
                && a.Channel == "member" && a.ToState == "Acknowledged") == 1);
        Check("Delivery ack: the member view surfaces the stamped time as the delivery's closing time",
            (await memberDeliveries.ReceivedForAsync(2))
                .Any(i => i.AlertDeliveryId == ackDelivery.Id && i.ClosedAtUtc == ackRow.AcknowledgedAtUtc));
        var firstStamp = ackRow.AcknowledgedAtUtc;
        var secondStamped = await memberDeliveries.AcknowledgeDeliveryAsync(ackDelivery.Id, 2);
        var ackRowAfter = await db.AlertDeliveries.FirstAsync(d => d.Id == ackDelivery.Id);
        Check("Delivery ack: a second acknowledgement by the addressee changes nothing further",
            !secondStamped && ackRowAfter.AcknowledgedAtUtc == firstStamp
            && await db.EventAudits.CountAsync(a => a.ObjectKind == "AlertDelivery" && a.EventId == ackEvent.Id) == 1);

        var (otherEvent, otherDelivery) = await SeedDeliveryAsync("A delivery only Daniel may acknowledge.");
        var otherStamped = await memberDeliveries.AcknowledgeDeliveryAsync(otherDelivery.Id, 3);
        var otherRow = await db.AlertDeliveries.FirstAsync(d => d.Id == otherDelivery.Id);
        Check("Delivery ack: another person's attempt changes nothing (no stamp, no audit row)",
            !otherStamped && otherRow.AcknowledgedAtUtc is null
            && await db.EventAudits.CountAsync(a => a.ObjectKind == "AlertDelivery" && a.EventId == otherEvent.Id) == 0);

        // ---------- Type-confused wire packets ----------
        // A packet whose envelope carries a JSON value of the wrong
        // type (a boolean issued_at, a floating point
        // ciphertext_length) must be rejected with a receipt, never
        // thrown through intake: in the relay loop one throw loses
        // every packet the relay already handed over in that batch.
        var confusedBase = TellingPlain(lisa, "member-lisa", "raw_telling",
            "Lisa called about the fence gate.", toldAt);
        var confusedWire1 = Wire(lisa, lisaKeys, confusedBase);
        var confusedPacket1 = Packet.Parse(confusedWire1);
        var confusedNode1 = JsonNode.Parse(confusedWire1)!.AsObject();
        confusedNode1["envelope"]!["issued_at"] = true;
        var confusedThrew1 = false;
        SealedIntakeService.IntakeReceipt? confusedReceipt1 = null;
        try { confusedReceipt1 = await intake.ReceiveWireAsync(confusedNode1.ToJsonString()); }
        catch { confusedThrew1 = true; }
        Check("Packet kinds: a wire packet with issued_at as a JSON boolean is rejected with a receipt, not thrown, and never reaches the inbox",
            !confusedThrew1 && confusedReceipt1 is { Accepted: false }
            && !await db.PacketInbox.AnyAsync(p => p.PacketId == confusedPacket1.Envelope.PacketId));

        var confusedBase2 = TellingPlain(lisa, "member-lisa", "raw_telling",
            "Lisa called about the fence gate latch.", toldAt);
        var confusedWire2 = Wire(lisa, lisaKeys, confusedBase2);
        var confusedPacket2 = Packet.Parse(confusedWire2);
        // Raw text substitution, because a JSON node round trip
        // serializes an integral double back as an integer and the
        // floating point form would never reach the wire.
        var confusedText2 = confusedWire2.Replace(
            $"\"ciphertext_length\":{confusedPacket2.Envelope.CiphertextLength}",
            $"\"ciphertext_length\":{confusedPacket2.Envelope.CiphertextLength}.0");
        var confusedThrew2 = false;
        SealedIntakeService.IntakeReceipt? confusedReceipt2 = null;
        try { confusedReceipt2 = await intake.ReceiveWireAsync(confusedText2); }
        catch { confusedThrew2 = true; }
        Check("Packet kinds: a wire packet with ciphertext_length as a JSON floating point number is rejected with a receipt, not thrown, and never reaches the inbox",
            confusedText2 != confusedWire2
            && !confusedThrew2 && confusedReceipt2 is { Accepted: false }
            && !await db.PacketInbox.AnyAsync(p => p.PacketId == confusedPacket2.Envelope.PacketId));

        // ---------- One bad packet cannot lose a poll batch ----------
        // The real poll path (RelayLoopService.RunPassAsync over
        // RelayClientService) against a stub relay whose poll
        // response carries two valid packets and one with a
        // type-confused issued_at. The relay drops everything it
        // forwards, so both valid packets must still be recorded.
        static JsonObject FlatOf(Packet p, long receivedAt) => new()
        {
            ["packet_id"] = p.Envelope.PacketId,
            ["household_endpoint"] = p.Envelope.HouseholdEndpoint,
            ["connection_id"] = p.Envelope.ConnectionId,
            ["envelope_class"] = EnvelopeClassNames.ToWire(p.Envelope.Class),
            ["delivery_mode"] = p.Envelope.DeliveryMode,
            ["ciphertext_length"] = p.Envelope.CiphertextLength,
            ["issued_at"] = p.Envelope.IssuedAt,
            ["ciphertext_hash"] = p.Envelope.CiphertextHash,
            ["received_at"] = receivedAt,
            ["expires_at"] = receivedAt + 300,
            ["ciphertext"] = p.CiphertextBase64Url,
            ["signature"] = p.SignatureBase64Url
        };
        var batchTellingA = TellingPlain(lisa, "member-lisa", "raw_telling",
            "Lisa called about the garden gate.", toldAt);
        var batchTellingB = TellingPlain(lisa, "member-lisa", "raw_telling",
            "Lisa called about the porch steps.", toldAt);
        var batchTellingBad = TellingPlain(lisa, "member-lisa", "raw_telling",
            "Lisa called about the cellar door.", toldAt);
        var batchPacketA = Packet.Parse(Wire(lisa, lisaKeys, batchTellingA));
        var batchPacketB = Packet.Parse(Wire(lisa, lisaKeys, batchTellingB));
        var batchPacketBad = Packet.Parse(Wire(lisa, lisaKeys, batchTellingBad));
        var batchBadFlat = FlatOf(batchPacketBad, 1793275400);
        batchBadFlat["issued_at"] = true;
        var batchPollBody = new JsonObject
        {
            ["packets"] = new JsonArray
            {
                FlatOf(batchPacketA, 1793275400),
                batchBadFlat,
                FlatOf(batchPacketB, 1793275401)
            }
        }.ToJsonString();
        var stubRelay = new StubRelayHandler { PollBody = batchPollBody };
        var pollClient = new RelayClientService(
            new HttpClient(stubRelay), "http://relay.invalid", "node-conn-stub", "stub-token");
        var batchThrew = false;
        try
        {
            await RelayLoopService.RunPassAsync(pollClient, intake, db, endpoint);
        }
        catch { batchThrew = true; }
        Check("Packet kinds: a poll batch carrying one type-confused packet among valid ones records both valid packets; the bad one is dropped and counted, not silently lost with the batch",
            !batchThrew && pollClient.LastPollDroppedMalformed == 1
            && await db.Events.CountAsync(e => e.ProvenancePacketId == batchPacketA.Envelope.PacketId) == 1
            && await db.Events.CountAsync(e => e.ProvenancePacketId == batchPacketB.Envelope.PacketId) == 1
            && !await db.PacketInbox.AnyAsync(p => p.PacketId == batchPacketBad.Envelope.PacketId)
            && !await db.Events.AnyAsync(e => e.ProvenancePacketId == batchPacketBad.Envelope.PacketId));

        // ---------- Transient extraction failure ----------
        // A second, isolated slice whose extraction client throws
        // once (a simulated transient model API error, mock only)
        // and then delegates to the deterministic mock. Two tellings
        // are queued; the first processing pass hits the failure.
        var flaky = new FlakyExtractionClient { FailuresRemaining = 1 };
        var flakyDbPath = Path.Combine(Path.GetTempPath(), $"tellhouse-node-flaky-{Guid.NewGuid():N}.db");
        var flakyServices = new ServiceCollection();
        flakyServices.AddLogging();
        flakyServices.AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={flakyDbPath}"));
        flakyServices.AddScoped<ConfirmationService>();
        flakyServices.AddScoped<AudienceDecisionService>();
        flakyServices.AddSingleton<IExtractionClient>(flaky);
        flakyServices.AddScoped<ExtractionContextBuilder>();
        flakyServices.AddScoped<ExtractionService>();
        flakyServices.AddScoped<PlanAssemblyService>();
        flakyServices.AddScoped<MemberDeliveryService>();
        flakyServices.AddScoped<BriefService>();
        flakyServices.AddScoped<LookupService>();
        flakyServices.AddSingleton(nodeKeys);
        flakyServices.AddScoped<DoorService>();
        flakyServices.AddScoped<SealedDeliveryService>();
        flakyServices.AddScoped<SealedIntakeService>();
        await using var flakyProvider = flakyServices.BuildServiceProvider();
        using var flakyScope = flakyProvider.CreateScope();
        var fsp = flakyScope.ServiceProvider;
        var fdb = fsp.GetRequiredService<AppDbContext>();
        await fdb.Database.EnsureCreatedAsync();
        var fHousehold = SeedSlice(fdb);
        var fDoor = fsp.GetRequiredService<DoorService>();
        var fIntake = fsp.GetRequiredService<SealedIntakeService>();
        using var fLisaKeys = IdentityKeypair.Generate();
        var fInvite = await fDoor.CreateInviteAsync(fHousehold, 1, 3, "Standard");
        var fRequest = await fDoor.KnockAsync(fInvite, "Lisa Ellis", "member-lisa", "Stand-in Assistant", fLisaKeys.PublicKeyBase64Url);
        var fLisa = await fDoor.ConfirmJoinAsync(fRequest.Id, 1, 3, fRequest.CheckPhrase);
        await fDoor.CreateConsentAsync(fHousehold, 3, "OneLiner", "FreeTextOneLiners", TimeSpan.FromDays(7), createdByReadBack: true);
        var fClientNonce = SessionChallenge.NewNonce();
        var fKnock = TellingPlain(fLisa, "member-lisa", SessionChallenge.OpenKind, fClientNonce, nowUnix());
        var fKnockReceipt = await fIntake.ReceiveWireAsync(Wire(fLisa, fLisaKeys, fKnock, EnvelopeClass.Knock, Envelope.DeliverySession));
        await fIntake.ProcessPacketAsync(fKnockReceipt.PacketId);
        var fChallenge = await fdb.SealedOutbox
            .Where(o => o.ConnectionId == fLisa.ConnectionId && o.SourceKind == "SessionChallenge")
            .OrderByDescending(o => o.CreatedAtUtc).FirstAsync();
        var fChallengePlain = Packet.Parse(fChallenge.WireJson).Open(fLisaKeys);
        var fServerNonce = fChallengePlain.Body.GetProperty("text").GetString() ?? "";
        var fProof = SessionChallenge.BuildProof(fLisa.ConnectionId, fClientNonce, fServerNonce, fLisaKeys);
        var fAck = new PlainPacket(PacketType.Acknowledgement, SealedDeliveryService.NewPacketId(),
            fLisa.ConnectionId, "member-lisa", NodeKeyStore.NodePartyId, nowUnix(),
            Base64Url.Encode(RandomNumberGenerator.GetBytes(16)),
            PlainPacket.AckBody(fChallenge.PacketId, "received"), fProof);
        var fAckReceipt = await fIntake.ReceiveWireAsync(Wire(fLisa, fLisaKeys, fAck));
        var fAckProcessed = await fIntake.ProcessPacketAsync(fAckReceipt.PacketId);
        Check("Transient failure setup: the isolated slice opens a session the usual way",
            fAckProcessed.Accepted && await fIntake.HasOpenSessionAsync(fLisa.ConnectionId));

        var fNonce1 = Base64Url.Encode(RandomNumberGenerator.GetBytes(16));
        var fNonce2 = Base64Url.Encode(RandomNumberGenerator.GetBytes(16));
        var fTelling1 = TellingPlain(fLisa, "member-lisa", "raw_telling",
            "We're low on wipes today; there are only a few left.", toldAt, nonce: fNonce1);
        var fTelling2 = TellingPlain(fLisa, "member-lisa", "raw_telling",
            "We're low on gauze today; there is only one left.", toldAt, nonce: fNonce2);
        var fWire1 = Wire(fLisa, fLisaKeys, fTelling1);
        var fWire2 = Wire(fLisa, fLisaKeys, fTelling2);
        var fReceipt1 = await fIntake.ReceiveWireAsync(fWire1);
        var fReceipt2 = await fIntake.ReceiveWireAsync(fWire2);
        Check("Transient failure setup: both tellings are queued unrecorded before the failing pass",
            fReceipt1.Accepted && fReceipt2.Accepted && await fdb.Events.CountAsync() == 0);
        var fRedelivered = await fIntake.ReceiveWireAsync(fWire1);
        Check("Transient failure: a re-delivery of a packet still queued and unrecorded is the same in-flight packet, not a replay; it adds no second inbox row",
            fRedelivered.Accepted
            && await fdb.PacketInbox.CountAsync(p => p.PacketId == fReceipt1.PacketId) == 1);

        var firstPassThrew = false;
        var firstPassRecorded = -1;
        try { firstPassRecorded = await fIntake.ProcessAllQueuedAsync(); }
        catch { firstPassThrew = true; }
        var midRows = await fdb.PacketInbox
            .Where(p => p.PacketId == fReceipt1.PacketId || p.PacketId == fReceipt2.PacketId)
            .ToListAsync();
        Check("Transient failure: one packet's transient extraction failure does not abort the batch; the other packet in the same pass is recorded and the failed one stays queued, not rejected",
            !firstPassThrew && firstPassRecorded == 1
            && await fdb.Events.CountAsync() == 1
            && midRows.Count(r => r.Status == "Recorded") == 1
            && midRows.Count(r => r.Status == "QueuedUnrecorded") == 1);

        var secondPassThrew = false;
        var secondPassRecorded = -1;
        try { secondPassRecorded = await fIntake.ProcessAllQueuedAsync(); }
        catch { secondPassThrew = true; }
        Check("Transient failure: after a transient extraction failure the same packet is retryable and records exactly once (no replay rejection from its own first attempt)",
            !secondPassThrew && secondPassRecorded == 1
            && await fdb.Events.CountAsync(e => e.ProvenancePacketId == fReceipt1.PacketId) == 1
            && await fdb.Events.CountAsync(e => e.ProvenancePacketId == fReceipt2.PacketId) == 1
            && await fdb.PacketNonceUses.CountAsync(n => n.PacketId == fReceipt1.PacketId) == 1
            && await fdb.PacketNonceUses.CountAsync(n => n.PacketId == fReceipt2.PacketId) == 1);

        var fEventsAtReplay = await fdb.Events.CountAsync();
        var fReplayReceipt = await fIntake.ReceiveWireAsync(fWire1);
        Check("Transient failure: a genuine replay of a successfully recorded packet is still rejected at the envelope step and records nothing twice",
            !fReplayReceipt.Accepted && await fdb.Events.CountAsync() == fEventsAtReplay);
        var fNonceReuse = TellingPlain(fLisa, "member-lisa", "raw_telling",
            "Lisa called about supper at the gatehouse.", toldAt, nonce: fNonce1);
        var fNonceReceipt = await fIntake.ReceiveWireAsync(Wire(fLisa, fLisaKeys, fNonceReuse));
        var fNonceProcessed = await fIntake.ProcessPacketAsync(fNonceReceipt.PacketId);
        Check("Transient failure: a fresh packet_id carrying the nonce of a successfully recorded packet is still rejected as a replay",
            !fNonceProcessed.Accepted && await fdb.Events.CountAsync() == fEventsAtReplay);
        try { File.Delete(flakyDbPath); } catch (IOException) { }
        // ---------- Signing uses the configured keystore ----------
        // The suite process still points HOME_NODE_KEYSTORE at the
        // original keyDir above, which is exactly the trap: a signing
        // path that resolved its own store from the environment or a
        // default path would sign with nodeKeys here. The configured
        // store for this block is a different directory and a
        // different keypair, standing in for a Node:KeyStorePath
        // setting that names it. Every signing path must use the
        // configured store it was given, and no other.
        var configuredKeyDir = Path.Combine(Path.GetTempPath(), $"tellhouse-configured-keys-{Guid.NewGuid():N}");
        using var configuredKeys = new NodeKeyStore(configuredKeyDir);
        Check("Configured keystore: the configured keystore is a different keypair from the default store the environment names",
            configuredKeys.PublicKeyBase64Url != nodeKeys.PublicKeyBase64Url
            && Environment.GetEnvironmentVariable("HOME_NODE_KEYSTORE") == keyDir);
        var configuredConfirmations = new ConfirmationService(db, configuredKeys);
        var configuredDeliveries = new SealedDeliveryService(db, configuredKeys);
        var configuredPlans = new PlanAssemblyService(db, configuredKeys);

        // Note: this scenario was a health mention as first
        // written, but the connection level gate correctly blocks a
        // health alert to Lisa's Standard connection before any
        // signing happens, so the signing path would never run. The
        // scenario is now a non-health alert in the same shape the
        // connection level checks use, which is what this block
        // exists to exercise: a delivery that flows, signed by
        // whichever keystore the service was given. The blocked
        // health path is covered by the connection level checks.
        var keystoreAlertEvent = new Event
        {
            HouseholdId = householdId,
            Type = EventType.Request,
            Body = "Configured keystore alert: signed with the configured key.",
            SubjectPersonId = 1,
            AuthorPersonId = 1,
            CreatedAtUtc = DateTime.UtcNow,
            SensitivityClass = SensitivityClass.Household,
            AudienceRule = """{"Class":"Household","RoutePersonId":3,"BackupPersonId":2}"""
        };
        var keystoreAlertAction = new EventAction
        {
            ActionType = EventActionType.NotifyPerson,
            TargetPersonId = 3,
            CreatedAtUtc = DateTime.UtcNow
        };
        await configuredConfirmations.ProposeAsync(keystoreAlertEvent, keystoreAlertAction, "tap");
        await configuredConfirmations.ConfirmAsync(keystoreAlertEvent.Id, 1, "tap");
        var keystoreAlertItem = await db.SealedOutbox.FirstOrDefaultAsync(o => o.SourceEventId == keystoreAlertEvent.Id);
        var keystoreAlertAfter = await db.Events.FirstAsync(e => e.Id == keystoreAlertEvent.Id);
        Check("Configured keystore: a confirmed alert is signed with the configured keystore key, not the default store key",
            keystoreAlertItem is not null
            && Packet.Parse(keystoreAlertItem.WireJson).VerifySignature(configuredKeys.PublicKeyBase64Url)
            && !Packet.Parse(keystoreAlertItem.WireJson).VerifySignature(nodeKeys.PublicKeyBase64Url));
        // Note: this check used to assert the event
        // completed at queue time; under the corrected rule a
        // sealed alert's event stays Confirmed, owed, until the
        // member acknowledges the packet.
        Check("Configured keystore: the configured alert opens for its addressee under the configured key and the event stays Confirmed until acknowledged",
            keystoreAlertItem is not null
            && configuredDeliveries.OpenDelivery(keystoreAlertItem, lisaKeys) == keystoreAlertEvent.Body
            && keystoreAlertAfter.ConfirmationState == ConfirmationState.Confirmed);

        var keystorePlan = await configuredPlans.CreatePlanAsync(householdId, "Configured keystore plan", null);
        var keystoreElement = await configuredPlans.AddElementAsync(keystorePlan.Id, PlanElementKind.Task,
            "Configured keystore plan words at seven", Array.Empty<int>(), assigneePersonId: 3);
        await configuredPlans.RetimeElementAsync(keystoreElement.Id, "Configured keystore plan words at eight", 1);
        var keystorePlanItem = await db.SealedOutbox.FirstOrDefaultAsync(o =>
            o.SourcePlanElementId == keystoreElement.Id && o.SourceKind == "Plan");
        Check("Configured keystore: a plan delivery is signed with the configured keystore key, not the default store key",
            keystorePlanItem is not null
            && Packet.Parse(keystorePlanItem.WireJson).VerifySignature(configuredKeys.PublicKeyBase64Url)
            && !Packet.Parse(keystorePlanItem.WireJson).VerifySignature(nodeKeys.PublicKeyBase64Url)
            && configuredDeliveries.OpenDelivery(keystorePlanItem, lisaKeys) == "Configured keystore plan words at eight");
        // ---------- Proposal, alert, clock, and Test Send checks (node side) ----------
        // A proposal delivery for an entry must not consume the
        // entry's one alert. Both confirmation routes are exercised:
        // the page tap (ConfirmAsync directly) and the federated
        // acknowledgement disposition.
        var reviewPageEvent = new Event
        {
            HouseholdId = householdId,
            Type = EventType.Request,
            Body = "Page route alert: the bins go out tonight.",
            SubjectPersonId = 2,
            AuthorPersonId = 1,
            CreatedAtUtc = DateTime.UtcNow,
            SensitivityClass = SensitivityClass.Household,
            AudienceRule = """{"Class":"Household","RoutePersonId":2,"BackupPersonId":3}"""
        };
        await confirmations.ProposeAsync(reviewPageEvent, new EventAction
        {
            ActionType = EventActionType.NotifyPerson,
            TargetPersonId = 2,
            CreatedAtUtc = DateTime.UtcNow
        }, "tap");
        var reviewPageProposal = await deliveries.QueueDeliveryAsync(householdId, 2,
            reviewPageEvent.Body, "Proposal", sourceEventId: reviewPageEvent.Id, kind: "proposal");
        Check("Proposal and alert setup: the proposal delivery for the page route entry is queued",
            reviewPageProposal is not null && reviewPageProposal.SourceKind == "Proposal");
        await confirmations.ConfirmAsync(reviewPageEvent.Id, 1, "tap");
        var reviewPageAlert = await db.SealedOutbox.FirstOrDefaultAsync(o =>
            o.SourceEventId == reviewPageEvent.Id && o.SourceKind == "Alert");
        Check("Proposal and alert: after a proposal was sent for the entry, the page tap confirmation still queues its alert",
            reviewPageAlert is not null);
        var reviewPageAfter = await db.Events.FirstAsync(e => e.Id == reviewPageEvent.Id);
        Check("Queued alert: a sealed alert that is only queued is not Done; the entry stays Confirmed, owed until acknowledged",
            reviewPageAlert is not null && reviewPageAfter.ConfirmationState == ConfirmationState.Confirmed);

        var reviewFedEvent = new Event
        {
            HouseholdId = householdId,
            Type = EventType.Request,
            Body = "Federated route alert: the gate latch is fixed.",
            SubjectPersonId = 2,
            AuthorPersonId = 1,
            CreatedAtUtc = DateTime.UtcNow,
            SensitivityClass = SensitivityClass.Household,
            AudienceRule = """{"Class":"Household","RoutePersonId":2,"BackupPersonId":3}"""
        };
        await confirmations.ProposeAsync(reviewFedEvent, new EventAction
        {
            ActionType = EventActionType.NotifyPerson,
            TargetPersonId = 2,
            CreatedAtUtc = DateTime.UtcNow
        }, "tap");
        var reviewFedProposal = await deliveries.QueueDeliveryAsync(householdId, 2,
            reviewFedEvent.Body, "Proposal", sourceEventId: reviewFedEvent.Id, kind: "proposal");
        var reviewFedAck = AckPlain(daniel, "member-daniel", reviewFedProposal!.PacketId, "confirmed");
        var reviewFedAckReceipt = await intake.ReceiveWireAsync(Wire(daniel, danielKeys, reviewFedAck));
        var reviewFedAckProcessed = await intake.ProcessPacketAsync(reviewFedAckReceipt.PacketId);
        var reviewFedAlert = await db.SealedOutbox.FirstOrDefaultAsync(o =>
            o.SourceEventId == reviewFedEvent.Id && o.SourceKind == "Alert");
        Check("Proposal and alert: after a proposal was sent for the entry, the federated confirmed acknowledgement still queues its alert",
            reviewFedAckProcessed.Accepted && reviewFedAlert is not null);

        // A federated telling told at 1:17 PM Chicago computes
        // its due time against the household clock, not UTC. The
        // told instant below is 18:17 UTC, which is 13:17 in
        // America/Chicago on Oct 27, 2026 (CDT, UTC-5).
        var reviewToldAt = new DateTimeOffset(2026, 10, 27, 18, 17, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var reviewCommit = TellingPlain(lisa, "member-lisa", "raw_telling",
            "I'll get the gauze by six.", reviewToldAt);
        var reviewCommitReceipt = await intake.ReceiveWireAsync(Wire(lisa, lisaKeys, reviewCommit));
        var reviewCommitProcessed = await intake.ProcessPacketAsync(reviewCommitReceipt.PacketId);
        var reviewCommitEvent = await db.Events.FirstOrDefaultAsync(e => e.ProvenancePacketId == reviewCommitReceipt.PacketId);
        var reviewPromise = reviewCommitEvent is null ? null
            : await db.Promises.FirstOrDefaultAsync(p => p.SourceEventId == reviewCommitEvent.Id);
        Check("Household clock: a federated commitment told at 1:17 PM Chicago with 'by six' is due today at 6 PM household time, not tomorrow",
            reviewCommitProcessed.Accepted && reviewPromise is not null
            && reviewPromise.DueAtLocal == new DateTime(2026, 10, 27, 18, 0, 0));
        // The brief's past-due wording runs on the household
        // clock too. A promise due 90 minutes from now, household
        // time, is not yet past, whatever the server clock says.
        var chicagoZone = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
        var reviewDueWall = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, chicagoZone).AddMinutes(90);
        var reviewBriefSource = new Event
        {
            HouseholdId = householdId,
            Type = EventType.Commitment,
            Body = "Daniel promised: pick up the parcel later.",
            SubjectPersonId = 2,
            AuthorPersonId = 2,
            CreatedAtUtc = DateTime.UtcNow,
            SensitivityClass = SensitivityClass.Household,
            AudienceRule = """{"Class":"Household"}"""
        };
        db.Events.Add(reviewBriefSource);
        await db.SaveChangesAsync();
        db.Promises.Add(new Promise
        {
            HouseholdId = householdId,
            PromisorPersonId = 2,
            CommitmentText = "pick up the parcel",
            DueText = "later",
            DueAtLocal = new DateTime(reviewDueWall.Year, reviewDueWall.Month, reviewDueWall.Day,
                reviewDueWall.Hour, reviewDueWall.Minute, 0),
            SourceEventId = reviewBriefSource.Id,
            State = PromiseState.Made
        });
        await db.SaveChangesAsync();
        var reviewBriefs = sp.GetRequiredService<BriefService>();
        var reviewBriefLines = await reviewBriefs.BuildBriefAsync(2,
            DateOnly.FromDateTime(reviewDueWall));
        var reviewBriefLine = reviewBriefLines.FirstOrDefault(l =>
            l.Section == "Promises still open" && l.Text.Contains("pick up the parcel"));
        Check("Household clock: a promise due 90 minutes from now, household time, reads as still to come in the brief, not as past",
            reviewBriefLine is not null && reviewBriefLine.Text.Contains("For later.")
            && !reviewBriefLine.Text.Contains("it was for"));

        // A Sent alert whose relay copy expired uncollected and
        // unacknowledged is owed again: the next relay pass posts it
        // again instead of leaving it Sent forever.
        if (reviewPageAlert is not null)
        {
            reviewPageAlert.Status = "Sent";
            reviewPageAlert.SentAtUtc = DateTime.UtcNow.AddMinutes(-10);
            reviewPageAlert.CreatedAtUtc = DateTime.UtcNow.AddMinutes(-10);
            await db.SaveChangesAsync();
            var reviewStubRelay = new StubRelayHandler { PollBody = "{\"packets\":[]}" };
            var reviewPollClient = new RelayClientService(
                new HttpClient(reviewStubRelay), "http://relay.invalid", "node-conn-stub", "stub-token");
            await RelayLoopService.RunPassAsync(reviewPollClient, intake, db, endpoint);
            var reviewResent = await db.SealedOutbox.FirstAsync(o => o.Id == reviewPageAlert.Id);
            Check("Alert re-post: an unacknowledged alert past the relay window is posted again on a later relay pass",
                reviewStubRelay.Posts >= 1 && reviewResent.Status == "Sent");
            // The member's received acknowledgement is what
            // completes the entry, and marks the outbox item.
            var reviewAlertAck = AckPlain(daniel, "member-daniel", reviewPageAlert.PacketId, "received");
            var reviewAlertAckReceipt = await intake.ReceiveWireAsync(Wire(daniel, danielKeys, reviewAlertAck));
            var reviewAlertAckProcessed = await intake.ProcessPacketAsync(reviewAlertAckReceipt.PacketId);
            var reviewPageDone = await db.Events.FirstAsync(e => e.Id == reviewPageEvent.Id);
            var reviewAlertAfterAck = await db.SealedOutbox.FirstAsync(o => o.Id == reviewPageAlert.Id);
            Check("Alert acknowledgement: the member's acknowledgement of the alert marks the outbox acknowledged and only then completes the entry",
                reviewAlertAckProcessed.Accepted && reviewAlertAfterAck.Status == "Acknowledged"
                && reviewPageDone.ConfirmationState == ConfirmationState.Done);
        }

        // A stand-in answer that reports the relay
        // refused its packet must not surface as sent.
        stub.ResponseBody = "{\"act\":\"lisa-telling-1\",\"packet_id\":\"pkt-stub-3\",\"relay_status\":409,\"relay_result\":\"duplicate\"}";
        stub.Throw = false;
        var reviewRefusedAnswer = await testSend.TriggerAsync("LisaStandIn");
        Check("Test Send: does not claim sent when the stand-in reports the relay refused its packet",
            reviewRefusedAnswer.Status != "sent");
        stub.ResponseBody = "{\"act\":\"lisa-telling-1\",\"packet_id\":\"pkt-stub-1\",\"relay_status\":202,\"relay_result\":\"accepted\"}";

        // Restart case: session state is a record table,
        // not process memory, so a node restart on the same
        // database does not close a session. A fresh context over
        // the same file still sees Lisa's verified session.
        {
            var restartOptions = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={dbPath}").Options;
            using var restartDb = new AppDbContext(restartOptions);
            var since = DateTime.UtcNow.AddMinutes(-30);
            Check("Session persistence: a verified session survives a node restart on the same database (its record is in the database, not in memory)",
                await restartDb.SessionChallenges.AnyAsync(c =>
                    c.ConnectionId == lisa.ConnectionId && c.Verified && c.VerifiedAtUtc >= since));
        }
        // ---------- A relay 409 duplicate does not block the outbox ----------
        // The relay already holds the first packet (its first post
        // succeeded and the response was lost), so every re-post is
        // answered 409 duplicate. That answer means the packet IS
        // at the relay: the item counts as sent, and the items
        // queued behind it still post in the same pass. A genuine
        // post failure (500 here) still leaves its item queued.
        var relayDup = await deliveries.QueueDeliveryAsync(householdId, 2,
            "First delivery: the relay already holds this packet.", "Delivery");
        var relayFail = await deliveries.QueueDeliveryAsync(householdId, 2,
            "Second delivery: its post fails outright.", "Delivery");
        var relayGood = await deliveries.QueueDeliveryAsync(householdId, 3,
            "Third delivery: queued behind both, must still go.", "Delivery");
        var relayStub = new StubRelayHandler();
        relayStub.DuplicatePacketIds.Add(relayDup!.PacketId);
        relayStub.FailPacketIds.Add(relayFail!.PacketId);
        var relayClient = new RelayClientService(
            new HttpClient(relayStub), "http://relay.invalid", "node-conn-stub", "stub-token");
        await RelayLoopService.RunPassAsync(relayClient, intake, db, endpoint);
        var relayDupAfter = await db.SealedOutbox.FirstAsync(o => o.Id == relayDup!.Id);
        var relayFailAfter = await db.SealedOutbox.FirstAsync(o => o.Id == relayFail.Id);
        var relayGoodAfter = await db.SealedOutbox.FirstAsync(o => o.Id == relayGood.Id);
        Check("Relay duplicate: a 409 duplicate answer from the relay counts the item as sent (the relay already holds it)",
            relayDupAfter.Status == "Sent");
        Check("Relay duplicate: one item's 409 and another's failed post do not block the queue; the delivery behind them is posted and marked Sent, the failed one stays Queued",
            relayGoodAfter.Status == "Sent" && relayFailAfter.Status == "Queued"
            && relayStub.PostedPacketIds.Contains(relayGood!.PacketId));

        // ---------- The connection id is born at knock time ----------
        using var timmyKeys = IdentityKeypair.Generate();
        var timmyInvite = await door.CreateInviteAsync(householdId, 1, 4, "Standard");
        var timmyRequest = await door.KnockAsync(timmyInvite, "Timmy", "member-timmy", "Stand-in Assistant", timmyKeys.PublicKeyBase64Url);
        var timmy = await door.ConfirmJoinAsync(timmyRequest.Id, 1, 4, timmyRequest.CheckPhrase);
        Check("Connection id: a knock for a party outside the frozen pair stores the connection id on the join request, and the confirmed connection carries that same id",
            timmyRequest.ConnectionId is not null
            && timmyRequest.ConnectionId == timmy.ConnectionId
            && timmyRequest.ResultConnectionId == timmy.ConnectionId);

        // ---------- A page created invite is usable ----------
        // The Connections page is how a person in the house creates
        // an invite in the demo. The knock identifies the
        // outstanding invite by its intended person, so an invite
        // the page stored with nobody intended could never be
        // knocked on. The page handler must record the intended
        // person its form names, and the form must offer the field.
        var connectionsPage = new HomeNode.Pages.ConnectionsModel(db, door)
        {
            TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(
                new Microsoft.AspNetCore.Http.DefaultHttpContext(), new StubTempDataProvider())
        };
        await connectionsPage.OnPostInviteAsync(1, 5, "Standard");
        var pageInviteRow = await db.DoorInvites.OrderByDescending(i => i.Id).FirstAsync();
        var pageInviteCode = connectionsPage.TempData["NewInviteCode"] as string;
        Check("Page invite: an invite created through the Connections page handler records the intended person the form names",
            pageInviteRow.IntendedPersonId == 5 && pageInviteCode is not null);
        using var miaKeys = IdentityKeypair.Generate();
        var miaRequest = await door.KnockAsync(pageInviteCode!, "Mia", "member-mia", "Stand-in Assistant", miaKeys.PublicKeyBase64Url);
        var miaWasPending = miaRequest.Status == "Pending";
        var miaConnection = await door.ConfirmJoinAsync(miaRequest.Id, 1, 5, miaRequest.CheckPhrase);
        Check("Page invite: the page created invite is knocked on with its code and confirmed for its intended person",
            miaWasPending && miaConnection.PersonId == 5);
        var connectionsPagePath = FindConnectionsPage();
        Check("Page invite: the Connections page invite form carries an intended person field",
            connectionsPagePath is not null
            && File.ReadAllText(connectionsPagePath).Contains("name=\"intendedPersonId\""));

        // ---------- Door name matching is exact ----------
        var allPeople = await db.Persons.OrderBy(p => p.Id).ToListAsync();
        Check("Exact name matching: person lookup by name is exact: a full name or a first name matches; a prefix, a single letter, an empty string, and an unknown name match nobody",
            DoorService.FindPersonByName(allPeople, "Margaret")?.Id == 1
            && DoorService.FindPersonByName(allPeople, "Margaret Ellis")?.Id == 1
            && DoorService.FindPersonByName(allPeople, "margaret")?.Id == 1
            && DoorService.FindPersonByName(allPeople, "M") is null
            && DoorService.FindPersonByName(allPeople, "Mar") is null
            && DoorService.FindPersonByName(allPeople, "") is null
            && DoorService.FindPersonByName(allPeople, null) is null
            && DoorService.FindPersonByName(allPeople, "Nobody") is null);

        // ---------- A KnockOnly invite means KnockOnly ----------
        using var knockKeys = IdentityKeypair.Generate();
        var knockCode = await door.CreateInviteAsync(householdId, 1, 6, "KnockOnly");
        var knockRequest = await door.KnockAsync(knockCode, "June Alvarez", "member-june-knock", "Stand-in Assistant", knockKeys.PublicKeyBase64Url);
        var knockConnection = await door.ConfirmJoinAsync(knockRequest.Id, 1, 6, knockRequest.CheckPhrase);
        Check("Connection level: a KnockOnly invite confirms to a KnockOnly standing connection, never a fully Active one",
            knockConnection.Level == "KnockOnly" && knockConnection.Standing == ConnectionStanding.KnockOnly);
        Check("Connection level: a KnockOnly connection receives no deliveries at all",
            await deliveries.QueueDeliveryAsync(householdId, 6, "A delivery a KnockOnly connection must not receive.", "Delivery") is null
            && !await db.SealedOutbox.AnyAsync(o => o.ConnectionId == knockConnection.ConnectionId));

        // ---------- Packet class versus standing and session ----------
        // The envelope's declared class was never checked against
        // what the opened packet actually is. Mia joins Active and
        // never opens a session before the first three attempts.
        using var miaS6Keys = IdentityKeypair.Generate();
        var mia = await JoinAsync(5, "Mia", "member-mia", miaS6Keys);
        await door.CreateConsentAsync(householdId, 5, "OneLiner", "FreeTextOneLiners",
            TimeSpan.FromDays(7), createdByReadBack: true);
        await door.CreateConsentAsync(householdId, 5, "Question", "FreeTextOneLiners",
            TimeSpan.FromDays(7), createdByReadBack: true);
        var eventsBeforeS6 = await db.Events.CountAsync();

        var miaKnockTelling = TellingPlain(mia, "member-mia", "raw_telling",
            "We're low on wipes today; there are only a few left.", toldAt);
        var miaKnockReceipt = await intake.ReceiveWireAsync(
            Wire(mia, miaS6Keys, miaKnockTelling, EnvelopeClass.Knock));
        var miaKnockProcessed = await intake.ProcessPacketAsync(miaKnockReceipt.PacketId);
        var miaKnockRow = await db.PacketInbox.FirstAsync(p => p.PacketId == miaKnockReceipt.PacketId);
        Check("Packet class: a knock class telling from an Active connection that never opened a session is rejected after opening, nothing recorded",
            miaKnockReceipt.Accepted && !miaKnockProcessed.Accepted
            && miaKnockRow.Status == "Rejected"
            && await db.Events.CountAsync() == eventsBeforeS6);

        // A challenge merely issued is not a session: the knock
        // exchange starts, no proof is ever sent, and a session
        // class telling must still not record.
        var miaClientNonce = SessionChallenge.NewNonce();
        var miaOpenKnock = TellingPlain(mia, "member-mia", SessionChallenge.OpenKind, miaClientNonce, nowUnix());
        var miaOpenReceipt = await intake.ReceiveWireAsync(
            Wire(mia, miaS6Keys, miaOpenKnock, EnvelopeClass.Knock));
        var miaOpenProcessed = await intake.ProcessPacketAsync(miaOpenReceipt.PacketId);
        var miaChallengeItem = await db.SealedOutbox
            .Where(o => o.ConnectionId == mia.ConnectionId && o.SourceKind == "SessionChallenge")
            .OrderByDescending(o => o.CreatedAtUtc).FirstOrDefaultAsync();
        Check("Packet class setup: the knock starts the exchange, a challenge is issued, and no session is open yet",
            miaOpenProcessed.Accepted && miaChallengeItem is not null
            && !await intake.HasOpenSessionAsync(mia.ConnectionId));
        var miaSessionTelling = TellingPlain(mia, "member-mia", "raw_telling",
            "We're low on wipes today; there are only a few left.", toldAt);
        var miaSessionReceipt = await intake.ReceiveWireAsync(Wire(mia, miaS6Keys, miaSessionTelling));
        var miaSessionProcessed = await intake.ProcessPacketAsync(miaSessionReceipt.PacketId);
        Check("Packet class: a session class telling is rejected when a challenge was merely issued and no proof was ever sent",
            miaSessionReceipt.Accepted && !miaSessionProcessed.Accepted
            && await db.Events.CountAsync() == eventsBeforeS6
            && !await intake.HasOpenSessionAsync(mia.ConnectionId));

        // Positive control: the proof completes the exchange, and
        // the same telling in session class then records.
        var miaChallengePlain = Packet.Parse(miaChallengeItem!.WireJson).Open(miaS6Keys);
        var miaServerNonce = miaChallengePlain.Body.GetProperty("text").GetString() ?? "";
        var miaProof = SessionChallenge.BuildProof(mia.ConnectionId, miaClientNonce, miaServerNonce, miaS6Keys);
        var miaAck = new PlainPacket(PacketType.Acknowledgement, SealedDeliveryService.NewPacketId(),
            mia.ConnectionId, "member-mia", NodeKeyStore.NodePartyId, nowUnix(),
            Base64Url.Encode(RandomNumberGenerator.GetBytes(16)),
            PlainPacket.AckBody(miaChallengeItem.PacketId, "received"), miaProof);
        var miaAckReceipt = await intake.ReceiveWireAsync(Wire(mia, miaS6Keys, miaAck));
        var miaAckProcessed = await intake.ProcessPacketAsync(miaAckReceipt.PacketId);
        var miaTellingAfterProof = TellingPlain(mia, "member-mia", "raw_telling",
            "We're low on wipes today; there are only a few left.", toldAt);
        var miaAfterReceipt = await intake.ReceiveWireAsync(Wire(mia, miaS6Keys, miaTellingAfterProof));
        var miaAfterProcessed = await intake.ProcessPacketAsync(miaAfterReceipt.PacketId);
        Check("Packet class: after a verified proof the session is open and a session class telling records",
            miaAckProcessed.Accepted && await intake.HasOpenSessionAsync(mia.ConnectionId)
            && miaAfterProcessed.Accepted && await db.Events.CountAsync() == eventsBeforeS6 + 1);

        // KnockOnly standing (June's knockConnection from the earlier
        // block): the knock class trick must not record a telling
        // either, with a OneLiner Consent in force so the valve
        // cannot be the reason.
        await door.CreateConsentAsync(householdId, 6, "OneLiner", "FreeTextOneLiners",
            TimeSpan.FromDays(7), createdByReadBack: true);
        var eventsBeforeKnockOnly = await db.Events.CountAsync();
        var juneKnockTelling = TellingPlain(knockConnection, "member-june-knock", "raw_telling",
            "We're low on wipes today; there are only a few left.", toldAt);
        var juneKnockReceipt = await intake.ReceiveWireAsync(
            Wire(knockConnection, knockKeys, juneKnockTelling, EnvelopeClass.Knock));
        var juneKnockProcessed = await intake.ProcessPacketAsync(juneKnockReceipt.PacketId);
        Check("Packet class: a knock class telling from a KnockOnly standing connection is rejected after opening, nothing recorded",
            juneKnockReceipt.Accepted && !juneKnockProcessed.Accepted
            && await db.Events.CountAsync() == eventsBeforeKnockOnly);
        var juneOpenKnock = TellingPlain(knockConnection, "member-june-knock",
            SessionChallenge.OpenKind, SessionChallenge.NewNonce(), nowUnix());
        var juneOpenReceipt = await intake.ReceiveWireAsync(
            Wire(knockConnection, knockKeys, juneOpenKnock, EnvelopeClass.Knock));
        var juneOpenProcessed = await intake.ProcessPacketAsync(juneOpenReceipt.PacketId);
        Check("Packet class: a KnockOnly connection can still run the knock (session open) exchange itself",
            juneOpenProcessed.Accepted
            && await db.SealedOutbox.AnyAsync(o => o.ConnectionId == knockConnection.ConnectionId
                && o.SourceKind == "SessionChallenge"));

        // ---------- A question cannot write ----------
        // Daniel's OneLiner Consent was revoked in the valve block
        // above; his Question Consent is in force (audience fence block). The
        // same supply text that records as a telling must not write
        // a record entry when it arrives as a question.
        var eventsBeforeS7 = await db.Events.CountAsync();
        var adjustmentsBeforeS7 = await db.SupplyAdjustments.CountAsync();
        var danielQuestionWrite = TellingPlain(daniel, "member-daniel", "question",
            "We're low on wipes today; there are only a few left.", toldAt);
        var danielQuestionReceipt = await intake.ReceiveWireAsync(Wire(daniel, danielKeys, danielQuestionWrite));
        var danielQuestionProcessed = await intake.ProcessPacketAsync(danielQuestionReceipt.PacketId);
        var danielQuestionRow = await db.PacketInbox.FirstAsync(p => p.PacketId == danielQuestionReceipt.PacketId);
        Check("Question valve: with the OneLiner valve closed and the Question valve open, a question carrying a supply telling writes nothing and is rejected whole",
            !danielQuestionProcessed.Accepted && danielQuestionRow.Status == "Rejected"
            && (danielQuestionRow.RejectionReason ?? "").Contains("OneLiner")
            && await db.Events.CountAsync() == eventsBeforeS7
            && await db.SupplyAdjustments.CountAsync() == adjustmentsBeforeS7);
        // Lisa holds both Consents, so for her the same words as a
        // question may write, under the telling permission she holds.
        var lisaQuestionWrite = TellingPlain(lisa, "member-lisa", "question",
            "We're low on wipes today; there are only a few left.", toldAt);
        var lisaQuestionReceipt = await intake.ReceiveWireAsync(Wire(lisa, lisaKeys, lisaQuestionWrite));
        var lisaQuestionProcessed = await intake.ProcessPacketAsync(lisaQuestionReceipt.PacketId);
        Check("Question valve: with both valves open for the asker, a question that resolves to a record entry writes it under the telling permission",
            lisaQuestionProcessed.Accepted
            && await db.Events.CountAsync() == eventsBeforeS7 + 1
            && await db.Events.AnyAsync(e => e.ProvenancePacketId == lisaQuestionReceipt.PacketId
                && e.Type == EventType.Supply));

        // ---------- standing enforcement, last ----------
        using var juneKeys = IdentityKeypair.Generate();
        var june = await JoinAsync(6, "June Alvarez", "member-june", juneKeys);
        await door.SetKnockOnlyAsync(june.ConnectionId);
        var juneTelling = TellingPlain(june, "member-june", "raw_telling", "June here.", toldAt);
        var juneReceipt = await intake.ReceiveWireAsync(Wire(june, juneKeys, juneTelling));
        Check("Standing: a knock-only connection may submit nothing but a knock; its session packet is rejected at intake",
            !juneReceipt.Accepted);
        await door.RevokeConnectionAsync(daniel.ConnectionId);
        var danielLate = TellingPlain(daniel, "member-daniel", "raw_telling", "Daniel, after revocation.", toldAt);
        var danielLateReceipt = await intake.ReceiveWireAsync(Wire(daniel, danielKeys, danielLate));
        Check("Standing: a revoked connection's packets are rejected at intake",
            !danielLateReceipt.Accepted);

        // ---------- Re-enrollment replaces the connection ----------
        // Lisa joins again with a fresh key. There must be exactly
        // one connection row under her connection id afterward,
        // carrying the new key: intake verifies against it,
        // deliveries seal to it, and revoking the id ends it.
        using var lisaKeys2 = IdentityKeypair.Generate();
        var reInvite = await door.CreateInviteAsync(householdId, 1, 3, "Standard");
        var reRequest = await door.KnockAsync(reInvite, "Lisa Ellis", "member-lisa", "Stand-in Assistant", lisaKeys2.PublicKeyBase64Url);
        var lisa2 = await door.ConfirmJoinAsync(reRequest.Id, 1, 3, reRequest.CheckPhrase);
        Check("Re-enrollment: re-enrolling a member leaves exactly one connection row under their connection id, carrying the new key and standing Active",
            await db.HouseConnections.CountAsync(c => c.ConnectionId == lisa.ConnectionId) == 1
            && lisa2.PublicKeyBase64Url == lisaKeys2.PublicKeyBase64Url
            && lisa2.Standing == ConnectionStanding.Active);
        var reDelivery = await deliveries.QueueDeliveryAsync(householdId, 3,
            "Re-enrollment delivery: sealed to the re-enrolled key.", "Delivery");
        Check("Re-enrollment: after re-enrollment, deliveries seal to the new key and no longer open under the replaced one",
            reDelivery is not null
            && deliveries.OpenDelivery(reDelivery, lisaKeys2) == "Re-enrollment delivery: sealed to the re-enrolled key."
            && ThrowsSync(() => deliveries.OpenDelivery(reDelivery!, lisaKeys)));
        Check("Re-enrollment: the re-enrolled member opens a session with the new key",
            await OpenSessionAsync(lisa2, lisaKeys2, "member-lisa"));
        var oldKeyTelling = TellingPlain(lisa2, "member-lisa", "raw_telling", "Lisa, on her replaced key.", nowUnix());
        var oldKeyReceipt = await intake.ReceiveWireAsync(Wire(lisa2, lisaKeys, oldKeyTelling));
        Check("Re-enrollment: packets signed with the replaced key are rejected at intake",
            !oldKeyReceipt.Accepted);
        await door.RevokeConnectionAsync(lisa.ConnectionId);
        var postRevokeTelling = TellingPlain(lisa2, "member-lisa", "raw_telling", "Lisa, after revocation.", nowUnix());
        var postRevokeReceipt = await intake.ReceiveWireAsync(Wire(lisa2, lisaKeys2, postRevokeTelling));
        Check("Re-enrollment: revoking the connection id revokes the member's only row: new key packets are rejected and no delivery is queued",
            !postRevokeReceipt.Accepted
            && await deliveries.QueueDeliveryAsync(householdId, 3, "Re-enrollment delivery after revoke.", "Delivery") is null
            && await db.HouseConnections.CountAsync(c => c.ConnectionId == lisa.ConnectionId && c.Standing == ConnectionStanding.Revoked) == 1);

        Console.WriteLine();
        Console.WriteLine($"Node suite: {_passed} passed, {_failed} failed.");
        foreach (var failure in _failures)
        {
            Console.WriteLine($"  FAILED: {failure}");
        }
        try { File.Delete(dbPath); } catch (IOException) { }
        return _failed == 0 ? 0 : 1;
    }

    // A wire packet whose sealed plain packet carries a body with
    // a key outside the closed list (kind, text), sealed and signed
    // correctly, so only the body's shape can reject it. The plain
    // JSON is written in canonical form by hand.
    private static string BuildStructuredWire(
        HouseConnection connection, IdentityKeypair keys, NodeKeyStore nodeKeys, string endpoint)
    {
        var packetId = SealedDeliveryService.NewPacketId();
        var nonce = Base64Url.Encode(RandomNumberGenerator.GetBytes(16));
        var plainJson = "{\"body\":{\"EventType\":\"Supply\",\"kind\":\"raw_telling\",\"text\":\"Sneaky structure.\"}"
            + ",\"connection_id\":\"" + connection.ConnectionId + "\""
            + ",\"packet_id\":\"" + packetId + "\""
            + ",\"packet_nonce\":\"" + nonce + "\""
            + ",\"packet_type\":\"telling\""
            + ",\"recipient_id\":\"" + NodeKeyStore.NodePartyId + "\""
            + ",\"sender_id\":\"" + connection.PartyId + "\""
            + ",\"told_at\":" + DateTimeOffset.UtcNow.ToUnixTimeSeconds() + "}";
        var plainBytes = Encoding.UTF8.GetBytes(plainJson);
        long blobLength = Sealer.EphemeralPointLength + Sealer.NonceLength + plainBytes.Length + Sealer.TagLength;
        var probe = new Envelope(packetId, endpoint, connection.ConnectionId, EnvelopeClass.Session,
            Envelope.DeliverySession, blobLength, DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Base64Url.Encode(new byte[32]));
        using var ephemeral = IdentityKeypair.Generate();
        var blob = Sealer.Seal(plainBytes, probe.AadBytes(), nodeKeys.PublicKeyBase64Url,
            ephemeral, RandomNumberGenerator.GetBytes(Sealer.NonceLength));
        var envelope = new Envelope(packetId, endpoint, connection.ConnectionId, EnvelopeClass.Session,
            Envelope.DeliverySession, blob.Length, probe.IssuedAt,
            Base64Url.Encode(SHA256.HashData(blob)));
        var signature = Packet.Sign(envelope.CanonicalBytes(), blob, keys);
        return new Packet(envelope, Base64Url.Encode(blob), signature).ToCanonicalJson();
    }

    // The frozen protocol vectors, checked from the outside: parse,
    // canonical forms, hashes, signature verification, opening,
    // deterministic re-sealing from the published seal inputs, the
    // fingerprint rule, and the session challenge proof.
    private static async Task VectorChecksAsync()
    {
        await Task.Yield();
        var path = FindVectorsFile();
        Check("Vectors: the frozen protocol test vectors file is present", path is not null);
        if (path is null)
        {
            return;
        }
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var household = root.GetProperty("household");
        var nodePub = household.GetProperty("node_public_key").GetString()!;
        var nodeKey = IdentityKeypair.FromExported(
            household.GetProperty("node_private_key_test_only").GetString()!, nodePub);
        var members = root.GetProperty("members");
        var lisaVector = members[0];
        var danielVector = members[1];
        var lisaKey = IdentityKeypair.FromExported(
            lisaVector.GetProperty("private_key_test_only").GetString()!,
            lisaVector.GetProperty("public_key").GetString()!);
        var danielKey = IdentityKeypair.FromExported(
            danielVector.GetProperty("private_key_test_only").GetString()!,
            danielVector.GetProperty("public_key").GetString()!);

        Check("Vectors: fingerprints are hex SHA-256 of the 65 byte public point, for the node and both members",
            nodeKey.Fingerprint == household.GetProperty("node_fingerprint").GetString()
            && lisaKey.Fingerprint == lisaVector.GetProperty("fingerprint").GetString()
            && danielKey.Fingerprint == danielVector.GetProperty("fingerprint").GetString()
            && nodeKey.Fingerprint == DoorService.FingerprintOf(nodePub));

        var vectors = root.GetProperty("vectors");
        foreach (var vector in vectors.EnumerateArray())
        {
            var name = vector.GetProperty("name").GetString()!;
            var packet = Packet.Parse(vector.GetProperty("packet_canonical").GetString()!);
            var envelopeMatches = packet.Envelope.ToCanonicalJson()
                == vector.GetProperty("envelope_canonical").GetString();
            var hashMatches = Convert.ToHexStringLower(SHA256.HashData(packet.CiphertextBytes()))
                == vector.GetProperty("ciphertext_sha256_hex").GetString();
            var senderPub = name == "delivery_node_to_daniel" || name == "delivery_node_to_lisa"
                ? nodePub
                : name == "telling_lisa_to_node"
                    ? lisaVector.GetProperty("public_key").GetString()!
                    : danielVector.GetProperty("public_key").GetString()!;
            var signatureVerifies = packet.VerifySignature(senderPub);
            Check($"Vectors: {name} envelope canonical form, ciphertext hash, and signature verify",
                envelopeMatches && hashMatches && signatureVerifies);

            var opener = name == "delivery_node_to_daniel" ? danielKey
                : name == "delivery_node_to_lisa" ? lisaKey
                : nodeKey;
            var plain = packet.Open(opener);
            var plainMatches = Encoding.UTF8.GetString(plain.CanonicalBytes())
                == vector.GetProperty("plaintext_canonical").GetString();
            var seal = vector.GetProperty("seal");
            using var ephemeral = IdentityKeypair.FromExported(
                seal.GetProperty("ephemeral_private_key_test_only").GetString()!,
                seal.GetProperty("ephemeral_public_key").GetString()!);
            var resealed = Sealer.Seal(
                plain.CanonicalBytes(), packet.Envelope.AadBytes(),
                name == "delivery_node_to_daniel" ? danielVector.GetProperty("public_key").GetString()!
                    : name == "delivery_node_to_lisa" ? lisaVector.GetProperty("public_key").GetString()!
                    : nodePub,
                ephemeral, Base64Url.Decode(seal.GetProperty("nonce").GetString()!));
            var sealMatches = resealed.SequenceEqual(packet.CiphertextBytes());
            Check($"Vectors: {name} opens to the published plaintext and re-seals byte for byte from the published seal inputs (AAD is Envelope.AadBytes)",
                plainMatches && sealMatches);
        }

        var challenge = root.GetProperty("session_challenge");
        var proof = challenge.GetProperty("proof");
        var proofOk = !ThrowsSync(() => SessionChallenge.VerifyProof(
            proof, challenge.GetProperty("connection_id").GetString()!,
            challenge.GetProperty("client_nonce").GetString()!,
            challenge.GetProperty("server_nonce").GetString()!,
            danielVector.GetProperty("public_key").GetString()!));
        Check("Vectors: the session challenge proof verifies under the member key over the contract transcript",
            proofOk);

        static bool ThrowsSync(Action act)
        {
            try { act(); return false; }
            catch { return true; }
        }
    }

    // The seeded record's clock, against the real DbSeeder on its
    // own fresh database (the scratch slice above is seeded by hand
    // and never runs DbSeeder). Seed times are the scenario's
    // America/Chicago wall clock times stored as true UTC instants,
    // so Daniel's 2:55 PM closing whereabouts is stored at 19:55 UTC
    // and a lookup that converts back to the household zone names
    // 2:55 PM, not the shifted 9:55 AM the old seed produced.
    private static async Task SeededRecordChecksAsync()
    {
        var seedDbPath = Path.Combine(Path.GetTempPath(), $"tellhouse-node-seed-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={seedDbPath}")
            .Options;
        using var seedDb = new AppDbContext(options);
        await seedDb.Database.EnsureCreatedAsync();
        DbSeeder.Seed(seedDb);

        Check("Seeded record: pristine counts hold at 6 persons, 16 events, 23 audits, 1 promise",
            await seedDb.Persons.CountAsync() == 6
            && await seedDb.Events.CountAsync() == 16
            && await seedDb.EventAudits.CountAsync() == 23
            && await seedDb.Promises.CountAsync() == 1);

        var daniel = await seedDb.Persons.FirstAsync(p => p.DisplayName == "Daniel");
        var lisa = await seedDb.Persons.FirstAsync(p => p.DisplayName == "Lisa");
        var closing = await seedDb.Events
            .Where(e => e.SubjectPersonId == daniel.Id && e.Type == EventType.Whereabouts)
            .OrderByDescending(e => e.CreatedAtUtc)
            .FirstAsync();
        Check("Seeded record: the closing whereabouts is Daniel's closing statement, stored as 19:55 UTC on Oct 27, 2026",
            closing.Body == "Home, like I said this morning."
            && closing.CreatedAtUtc == new DateTime(2026, 10, 27, 19, 55, 0));

        var lookup = new LookupService(seedDb);
        var answer = await lookup.AnswerWhereaboutsAsync(lisa.Id, daniel.Id);
        Check("Seeded record: the whereabouts lookup for Daniel returns the closing statement at 2:55 PM",
            answer == "At 2:55 PM, Daniel said: Home, like I said this morning.");

        try { File.Delete(seedDbPath); } catch (IOException) { }
    }

    private static string? FindVectorsFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && dir is not null; i++, dir = dir.Parent)
        {
            var direct = Path.Combine(dir.FullName, "test-vectors.json");
            if (File.Exists(direct)) return direct;
            var nested = Path.Combine(dir.FullName, "Protocol", "test-vectors.json");
            if (File.Exists(nested)) return nested;
        }
        return null;
    }

    // The Connections page markup, for the page invite form check: the
    // suite runs from the project tree, the same way the vectors
    // file is found.
    private static string? FindConnectionsPage()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && dir is not null; i++, dir = dir.Parent)
        {
            var direct = Path.Combine(dir.FullName, "Pages", "Connections.cshtml");
            if (File.Exists(direct)) return direct;
            var nested = Path.Combine(dir.FullName, "src", "HomeNode", "Pages", "Connections.cshtml");
            if (File.Exists(nested)) return nested;
        }
        return null;
    }

    // In-memory TempData provider for driving the Connections
    // page model directly in the page invite checks (no web host here).
    private sealed class StubTempDataProvider : Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(
            Microsoft.AspNetCore.Http.HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(
            Microsoft.AspNetCore.Http.HttpContext context, IDictionary<string, object> values) { }
    }

    // A stub stand-in trigger endpoint for the Test Send checks: it
    // records what was posted and answers in the real stand-in's own
    // shapes (sent first, then whatever the check sets).
    private sealed class StubTriggerHandler : HttpMessageHandler
    {
        public string? LastPayload;
        public int Calls;
        public string ResponseBody = "{\"act\":\"lisa-telling-1\",\"packet_id\":\"pkt-stub-1\",\"relay_status\":202,\"relay_result\":\"accepted\"}";
        public bool Throw;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (Throw)
            {
                throw new HttpRequestException("stub failure");
            }
            LastPayload = request.Content is null
                ? null
                : request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ResponseBody, Encoding.UTF8, "application/json")
            });
        }
    }

    // Stand-in for a transient model API failure:
    // throws a stated number of times, then delegates to the
    // deterministic mock. Local and mock only; no live call.
    private sealed class FlakyExtractionClient : IExtractionClient
    {
        private readonly MockExtractionClient _inner = new();
        public int FailuresRemaining;
        public string Name => "flaky-mock";
        public Task<ExtractionOutput> ExtractAsync(ExtractionInput input, CancellationToken ct = default)
        {
            if (FailuresRemaining > 0)
            {
                FailuresRemaining--;
                throw new InvalidOperationException("Simulated transient model API error.");
            }
            return _inner.ExtractAsync(input, ct);
        }
    }

    // Live authorship stub for the Live model: whatever the speaker says,
    // it returns one Commitment whose author and subject are both
    // Daniel. Used only in-process, mock only; no live call.
    private sealed class DanielAttributionClient : IExtractionClient
    {
        public string Name => "daniel-attribution-stub";
        public Task<ExtractionOutput> ExtractAsync(ExtractionInput input, CancellationToken ct = default)
        {
            var output = new ExtractionOutput { UtteranceId = input.UtteranceId };
            output.Events.Add(new ExtractionEvent
            {
                Type = "Commitment",
                Body = input.Transcript.Trim(),
                SubjectPersonId = 2,
                AuthorPersonId = 2,
                ConfirmationState = "Confirmed",
                Details = new System.Text.Json.Nodes.JsonObject
                {
                    ["CommitmentText"] = "Daniel said he'll get the milk by five"
                }
            });
            output.SpokenResponse = "Recorded.";
            return Task.FromResult(output);
        }
    }

    // Stub relay: answers a poll with a fixed flat
    // response body and accepts posts the way the real relay does.
    // A post can also be answered 409 duplicate (the
    // relay already holds that packet id) or 500, per packet id.
    private sealed class StubRelayHandler : HttpMessageHandler
    {
        public string PollBody = "{\"packets\":[]}";
        public int Posts;
        public readonly List<string> PostedPacketIds = new();
        public readonly HashSet<string> DuplicatePacketIds = new();
        public readonly HashSet<string> FailPacketIds = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(PollBody, Encoding.UTF8, "application/json")
                });
            }
            Posts++;
            var packetId = "";
            try
            {
                var bodyText = request.Content is null
                    ? ""
                    : request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                using var doc = JsonDocument.Parse(bodyText);
                packetId = doc.RootElement.GetProperty("envelope")
                    .GetProperty("packet_id").GetString() ?? "";
            }
            catch { }
            PostedPacketIds.Add(packetId);
            if (DuplicatePacketIds.Contains(packetId))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)
                {
                    Content = new StringContent(
                        "{\"error\":\"duplicate_packet_id\"}",
                        Encoding.UTF8, "application/json")
                });
            }
            if (FailPacketIds.Contains(packetId))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent(
                        "{\"error\":\"stub_failure\"}",
                        Encoding.UTF8, "application/json")
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = new StringContent(
                    "{\"packet_id\":\"stub-post\",\"expires_at\":1793275700}",
                    Encoding.UTF8, "application/json")
            });
        }
    }

    private static void Check(string name, bool condition)
    {
        if (condition)
        {
            _passed++;
            Console.WriteLine($"  PASS  {name}");
        }
        else
        {
            _failed++;
            _failures.Add(name);
            Console.WriteLine($"  FAIL  {name}");
        }
    }

    private static int SeedSlice(AppDbContext db)
    {
        var household = new Household
        {
            Name = "The Ellis Household",
            TimeZoneId = "America/Chicago",
            HealthMentionLevelKey = "now"
        };
        db.Households.Add(household);
        db.SaveChanges();

        var margaret = new Person { Id = 1, HouseholdId = household.Id, DisplayName = "Margaret Ellis", Role = PersonRole.Family };
        var daniel = new Person { Id = 2, HouseholdId = household.Id, DisplayName = "Daniel Ellis", Role = PersonRole.Family };
        var lisa = new Person { Id = 3, HouseholdId = household.Id, DisplayName = "Lisa Ellis", Role = PersonRole.Family };
        var timmy = new Person { Id = 4, HouseholdId = household.Id, DisplayName = "Timmy", Role = PersonRole.Family, MinorFlag = true };
        var mia = new Person { Id = 5, HouseholdId = household.Id, DisplayName = "Mia", Role = PersonRole.Family, MinorFlag = true };
        var june = new Person { Id = 6, HouseholdId = household.Id, DisplayName = "June Alvarez", Role = PersonRole.Helper };
        db.Persons.AddRange(margaret, daniel, lisa, timmy, mia, june);
        db.SaveChanges();

        household.DefaultRoutePersonId = 2;
        household.BackupRoutePersonId = 3;
        db.SaveChanges();

        db.AlertLevels.AddRange(
            new AlertLevel { HouseholdId = household.Id, LevelKey = "now", Label = "Tell them now", RouteToPersonId = 2, BackupPersonId = 3 },
            new AlertLevel { HouseholdId = household.Id, LevelKey = "brief", Label = "Put it in the morning brief" },
            new AlertLevel { HouseholdId = household.Id, LevelKey = "here", Label = "Just keep it here" });
        db.HouseholdSchedules.AddRange(
            new HouseholdSchedule { Id = 100, HouseholdId = household.Id, Label = "Morning ones", SetByPersonId = 2, TimeLocal = "08:00" },
            new HouseholdSchedule { Id = 101, HouseholdId = household.Id, Label = "Evening ones", SetByPersonId = 2, TimeLocal = "19:00" });
        db.GlossaryTerms.Add(new GlossaryTerm
        {
            HouseholdId = household.Id,
            Term = "my program",
            Meaning = "the afternoon show Margaret watches at four"
        });
        db.SupplyItems.AddRange(
            new SupplyItem { Id = 10, HouseholdId = household.Id, Name = "Wipes", Unit = "wipe", QuantityOnHand = 12m, SeededDailyUsageRate = 5m, ReorderLeadDays = 2, AssignedPersonId = 2, Aliases = """["wipes","the wipes"]""" },
            new SupplyItem { Id = 11, HouseholdId = household.Id, Name = "Gauze", Unit = "dressing", QuantityOnHand = 1m, SeededDailyUsageRate = 1m, ReorderLeadDays = 2, AssignedPersonId = 2, Aliases = """["gauze"]""" },
            new SupplyItem { Id = 12, HouseholdId = household.Id, Name = "Pads", Unit = "pad", QuantityOnHand = 18m, SeededDailyUsageRate = 4m, ReorderLeadDays = 3, AssignedPersonId = 3, Aliases = """["pads","the big pads"]""" },
            new SupplyItem { Id = 13, HouseholdId = household.Id, Name = "Heart pills (morning)", Unit = "pill", QuantityOnHand = 7m, SeededDailyUsageRate = 1m, ReorderLeadDays = 7, AssignedPersonId = 3, Aliases = """["heart pills"]""" },
            new SupplyItem { Id = 14, HouseholdId = household.Id, Name = "Evening pills (bottle)", Unit = "bottle", QuantityOnHand = 9m, SeededDailyUsageRate = 0m, ReorderLeadDays = 7, AssignedPersonId = 1, Aliases = """["evening ones","evening pills"]""" });
        db.SaveChanges();
        return household.Id;
    }
}
