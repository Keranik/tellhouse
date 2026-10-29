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
// It exercises the home node's own work against the frozen protocol
// contract and a scratch slice:
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
//   - the relay client wire rebuild (the relay poll shape);
//   - the seeded record's clock: a fresh database seeded
//     through the real DbSeeder, checked for its pristine counts,
//     for the closing whereabouts stored as its true UTC instant,
//     and for the whereabouts lookup naming the scenario's clock
//     time for that closing statement.
// The regression harness is the separate --demo-suite run (the
// monolith's checks unmodified, plus 30 audience
// checks from its port); this suite adds to it, never edits it.
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
        Check("Intake: the intake writes the record's provenance fields (the provenance field names) from the packet it opened",
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
        Check("Delivery: a confirmed alert for a remote member leaves as a sealed packet, no plaintext delivery row, event Done",
            alertOutbox is not null && alertOutbox.SourceKind == "Alert"
            && await db.AlertDeliveries.CountAsync(d => d.EventId == alertEvent.Id) == 0
            && alertAfter.ConfirmationState == ConfirmationState.Done
            && deliveries.OpenDelivery(alertOutbox, danielKeys) == alertEvent.Body);

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
        var r14WhereEvent = new Event
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
        await confirmations.WriteStatementAsync(r14WhereEvent, "tap");
        var r14LisaQuestion = TellingPlain(lisa, "member-lisa", "question", "Where's Margaret?", toldAt);
        var r14LisaReceipt = await intake.ReceiveWireAsync(Wire(lisa, lisaKeys, r14LisaQuestion));
        var r14LisaProcessed = await intake.ProcessPacketAsync(r14LisaReceipt.PacketId);
        var r14LisaAnswer = await db.SealedOutbox
            .Where(o => o.ToPersonId == 3 && o.SourceKind == "Answer")
            .OrderByDescending(o => o.Id)
            .FirstOrDefaultAsync();
        Check("Audience fence: a stand-in question from a person outside a Restricted audience is answered without its body text",
            r14LisaProcessed.Accepted && r14LisaAnswer is not null
            && !deliveries.OpenDelivery(r14LisaAnswer, lisaKeys).Contains("clinic annex"));
        await door.CreateConsentAsync(householdId, 2, "Question", "FreeTextOneLiners",
            TimeSpan.FromDays(7), createdByReadBack: true);
        var r14DanielQuestion = TellingPlain(daniel, "member-daniel", "question", "Where's Margaret?", toldAt);
        var r14DanielReceipt = await intake.ReceiveWireAsync(Wire(daniel, danielKeys, r14DanielQuestion));
        var r14DanielProcessed = await intake.ProcessPacketAsync(r14DanielReceipt.PacketId);
        var r14DanielAnswer = await db.SealedOutbox
            .Where(o => o.ToPersonId == 2 && o.SourceKind == "Answer")
            .OrderByDescending(o => o.Id)
            .FirstOrDefaultAsync();
        Check("Audience fence: a stand-in question from the route person inside the audience is answered from the Restricted record",
            r14DanielProcessed.Accepted && r14DanielAnswer is not null
            && deliveries.OpenDelivery(r14DanielAnswer, danielKeys).Contains("clinic annex"));

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
