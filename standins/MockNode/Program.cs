using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MockNode;

// TEST DOUBLE for the home node, written for the stand-in checks
// only. This node double stands in for the home node's door
// registry and sealed ingress during the stand-in protocol
// checks. It is not the home node deliverable: extraction, the
// record pipeline, and the house pages are the home node's work.
//
// What this double does implement for real, because the stand-in
// checks depend on it, all against the frozen protocol contract
// version 1.0: the door ceremony (knock, inviter card, check
// phrase, explicit confirmation, invite disposal); packet intake
// in the contract section 8 order (parse, envelope field list,
// seen packet registry, hash and length, signature under the
// enrolled key, open, plain packet validation); the session open
// challenge of contract section 7; the household Consent valve
// for a member channel, exercised both ways (the frozen contract
// plain packet carries no Consent field, so the valve is a
// node side setting in this double, checked after intake and
// before any record event, rejecting the packet whole with one
// trail line; the class mapping follows the home node: a raw telling
// maps to the OneLiner class, a question to Question, a
// proposal-disposing acknowledgement to ProposalResponse, and
// content classes are not checked in band because the frozen
// body shapes carry no content class signal); a record log with
// federated provenance; sealed
// deliveries posted through the real relay; and
// acknowledgements recorded as reported, not observed.

string storeDir = Environment.GetEnvironmentVariable("NODE_STORE") ?? "node-store";
Directory.CreateDirectory(storeDir);
string KeysPath = Path.Combine(storeDir, "node-keys.json");
string InvitesPath = Path.Combine(storeDir, "invites.json");
string ConnsPath = Path.Combine(storeDir, "connections.json");
string RecordPath = Path.Combine(storeDir, "record.json");
string TrailPath = Path.Combine(storeDir, "trail.json");
string AcksPath = Path.Combine(storeDir, "acks.json");
string SeenPath = Path.Combine(storeDir, "seen.json");
string ValvesPath = Path.Combine(storeDir, "valves.json");
string ProposalsPath = Path.Combine(storeDir, "proposals.json");
string SessionsPath = Path.Combine(storeDir, "sessions.json");
string relayUrl = Environment.GetEnvironmentVariable("RELAY_URL") ?? "http://127.0.0.1:7411";
string nodeToken = Environment.GetEnvironmentVariable("NODE_RELAY_TOKEN") ?? "node-token-dev";
const string NodeConnId = "node-conn-01";
const string HouseholdEndpoint = "hh-demo-01";
const string NodePartyId = "household-node";
var Gate = new object();

JsonObject LoadObj(string path) =>
    File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsObject() : new JsonObject();
JsonArray LoadArr(string path) =>
    File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsArray() : new JsonArray();
void SaveObj(string path, JsonObject o) =>
    File.WriteAllText(path, o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
void SaveArr(string path, JsonArray a) =>
    File.WriteAllText(path, a.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

if (!File.Exists(KeysPath))
{
    var (kd, kx, ky) = Protocol.NewKeyPair();
    SaveObj(KeysPath, new JsonObject { ["d"] = kd, ["x"] = kx, ["y"] = ky });
}
var nkeys = LoadObj(KeysPath);
string ND = nkeys["d"]!.GetValue<string>();
string NX = nkeys["x"]!.GetValue<string>();
string NY = nkeys["y"]!.GetValue<string>();
string nodePubRaw = Protocol.B64Url(Protocol.PubRaw(NX, NY));

void Trail(string packetId, string result, string reason)
{
    lock (Gate)
    {
        var t = LoadArr(TrailPath);
        t.Add(new JsonObject
        {
            ["at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["packet_id"] = packetId,
            ["result"] = result,
            ["reason"] = reason
        });
        SaveArr(TrailPath, t);
    }
}

using var http = Protocol.LocalHttp();

async Task SendNodePacket(JsonObject plain, string recipientConn, string recipPubRawB64, string envelopeClass)
{
    var packetId = plain["packet_id"]!.GetValue<string>();
    var wire = Protocol.BuildPacket(ND, NX, NY, Protocol.UnB64Url(recipPubRawB64), plain,
        recipientConn, "session", envelopeClass, HouseholdEndpoint, packetId,
        DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    var req = new HttpRequestMessage(HttpMethod.Post, relayUrl + "/packets");
    req.Headers.Add("X-Connection-Id", NodeConnId);
    req.Headers.Add("X-Connection-Token", nodeToken);
    req.Content = new StringContent(Protocol.Canonical(wire), Encoding.UTF8, "application/json");
    var resp = await http.SendAsync(req);
    if (!resp.IsSuccessStatusCode)
        Trail(packetId, "delivery not queued", "relay refused a node packet with " + (int)resp.StatusCode);
}

JsonObject MakePlain(string type, JsonObject body, string recipientParty, string recipientConn, string packetId) => new()
{
    ["body"] = body,
    ["connection_id"] = recipientConn,
    ["packet_id"] = packetId,
    ["packet_nonce"] = Protocol.B64Url(RandomNumberGenerator.GetBytes(16)),
    ["packet_type"] = type,
    ["recipient_id"] = recipientParty,
    ["sender_id"] = NodePartyId,
    ["told_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
};

string NewId(string prefix) => prefix + Protocol.Hex(RandomNumberGenerator.GetBytes(8));

async Task ProcessPacket(JsonObject wire)
{
    var envelope = wire["envelope"]!.AsObject();
    var packetId = envelope["packet_id"]!.GetValue<string>();
    var connId = envelope["connection_id"]!.GetValue<string>();
    void Reject(string reason) => Trail(packetId, "rejected", reason);

    JsonObject? conn = null;
    string plainText = "";
    JsonObject plain = new();
    lock (Gate)
    {
        // Contract section 8 intake order.
        var seen = LoadObj(SeenPath);
        if (seen[packetId] != null) { Reject("replayed packet id"); return; }
        var blob = Protocol.UnB64Url(wire["ciphertext"]!.GetValue<string>());
        if (Protocol.B64Url(SHA256.HashData(blob)) != envelope["ciphertext_hash"]!.GetValue<string>()
            || blob.Length != envelope["ciphertext_length"]!.GetValue<long>())
        { Reject("ciphertext hash or length mismatch"); return; }
        var conns = LoadObj(ConnsPath);
        conn = conns[connId]?.AsObject();
        if (conn == null || conn["standing"]!.GetValue<string>() != "enrolled")
        { Reject("connection not enrolled"); return; }
        if (!Protocol.VerifyPacket(conn["public_x"]!.GetValue<string>(),
                conn["public_y"]!.GetValue<string>(), wire))
        { Reject("signature did not verify under the enrolled key"); return; }
        try
        {
            plainText = Encoding.UTF8.GetString(Protocol.OpenPacket(ND, NX, NY, wire));
        }
        catch { Reject("packet did not open under the node key"); return; }
        try { plain = JsonNode.Parse(plainText)!.AsObject(); }
        catch { Reject("inner packet was not the protocol shape"); return; }
        if (plain["packet_id"]!.GetValue<string>() != packetId
            || plain["connection_id"]!.GetValue<string>() != connId)
        { Reject("plain packet did not match its envelope"); return; }
        if (plain["sender_id"]!.GetValue<string>() != conn["party_id"]!.GetValue<string>())
        { Reject("sender differs from the connection enrollment"); return; }
        if (plain["recipient_id"]!.GetValue<string>() != NodePartyId)
        { Reject("packet not addressed to the node"); return; }
        var nonce = plain["packet_nonce"]!.GetValue<string>();
        if (seen["nonce:" + connId + ":" + nonce] != null)
        { Reject("packet nonce already used on this connection"); return; }
        var bodyKeys = plain["body"]!.AsObject().Select(kv => kv.Key).OrderBy(k => k).ToArray();
        var type0 = plain["packet_type"]!.GetValue<string>();
        var bodyShapeOk = type0 == "acknowledgement"
            ? bodyKeys.SequenceEqual(new[] { "acked_packet_id", "disposition" })
            : bodyKeys.SequenceEqual(new[] { "kind", "text" });
        if (!bodyShapeOk)
        { Reject("structured payload beyond the closed body shapes"); return; }
        seen[packetId] = true;
        seen["nonce:" + connId + ":" + nonce] = true;
        SaveObj(SeenPath, seen);
    }

    var type = plain["packet_type"]!.GetValue<string>();
    var body = plain["body"]!.AsObject();
    var sessions = LoadObj(SessionsPath);
    var session = sessions[connId]?.AsObject();

    if (type == "telling" && body["kind"]!.GetValue<string>() == "session_open")
    {
        if (envelope["envelope_class"]!.GetValue<string>() != "knock")
        { Reject("session open arrived outside the knock class"); return; }
        var serverNonce = Protocol.B64Url(RandomNumberGenerator.GetBytes(16));
        lock (Gate)
        {
            sessions[connId] = new JsonObject
            {
                ["stage"] = "challenge",
                ["client_nonce"] = body["text"]!.GetValue<string>(),
                ["server_nonce"] = serverNonce,
                ["expires"] = DateTimeOffset.UtcNow.AddSeconds(120).ToUnixTimeSeconds(),
                ["open"] = false
            };
            SaveObj(SessionsPath, sessions);
        }
        var challengeId = NewId("pkt-");
        var challenge = MakePlain("delivery",
            new JsonObject { ["kind"] = "session_challenge", ["text"] = serverNonce },
            conn["party_id"]!.GetValue<string>(), connId, challengeId);
        await SendNodePacket(challenge, connId, conn["public_key"]!.GetValue<string>(), "knock");
        Trail(packetId, "challenge issued", "session challenge delivered for " + connId);
        return;
    }

    if (type == "acknowledgement" && plain["proof"] != null)
    {
        var proof = plain["proof"]!.AsObject();
        bool ok = session != null
            && session["stage"]!.GetValue<string>() == "challenge"
            && session["expires"]!.GetValue<long>() >= DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            && proof["client_nonce"]!.GetValue<string>() == session["client_nonce"]!.GetValue<string>()
            && proof["server_nonce"]!.GetValue<string>() == session["server_nonce"]!.GetValue<string>()
            && Protocol.Verify(conn["public_x"]!.GetValue<string>(), conn["public_y"]!.GetValue<string>(),
                Protocol.Transcript(connId,
                    Protocol.UnB64Url(proof["client_nonce"]!.GetValue<string>()),
                    Protocol.UnB64Url(proof["server_nonce"]!.GetValue<string>())),
                proof["proof_signature"]!.GetValue<string>());
        if (!ok) { Reject("session proof did not verify"); return; }
        lock (Gate)
        {
            session!["stage"] = "open";
            session["open"] = true;
            sessions[connId] = session;
            SaveObj(SessionsPath, sessions);
        }
        Trail(packetId, "session open", "proof verified for " + connId);
        return;
    }

    if (type == "acknowledgement")
    {
        if (session == null || session["open"]?.GetValue<bool>() != true)
        { Reject("session not open for this connection"); return; }
        lock (Gate)
        {
            var acks = LoadArr(AcksPath);
            acks.Add(new JsonObject
            {
                ["packet_id"] = packetId,
                ["acked_packet_id"] = body["acked_packet_id"]!.GetValue<string>(),
                ["from_person"] = conn["person"]!.GetValue<string>(),
                ["from_connection"] = connId,
                ["disposition"] = body["disposition"]!.GetValue<string>(),
                ["provenance"] = "reported, not observed",
                ["recorded_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });
            SaveArr(AcksPath, acks);
        }
        Trail(packetId, "acknowledgement recorded", "reported, not observed");
        // Proposal disposal (the home node mapping): if this
        // acknowledgement answers a proposal delivery, a confirmed
        // or declined disposition disposes the proposal. A receipt
        // disposition disposes nothing.
        var ackedId = body["acked_packet_id"]!.GetValue<string>();
        var disp = body["disposition"]!.GetValue<string>();
        lock (Gate)
        {
            var proposals = LoadArr(ProposalsPath);
            foreach (var pn in proposals)
            {
                var po = pn!.AsObject();
                if (po["proposal_packet_id"]!.GetValue<string>() == ackedId
                    && po["disposition"]!.GetValue<string>() == "pending")
                {
                    if (disp == "confirmed" || disp == "declined")
                    {
                        po["disposition"] = disp;
                        po["disposed_by"] = conn["person"]!.GetValue<string>();
                        Trail(packetId, "proposal disposed",
                            "proposal " + ackedId + " disposed as " + disp + " by the addressed member acknowledgement, reported, not observed");
                    }
                    else
                    {
                        Trail(packetId, "proposal not disposed",
                            "a receipt disposition does not dispose a proposal");
                    }
                }
            }
            SaveArr(ProposalsPath, proposals);
        }
        return;
    }

    if (type != "telling" || body["kind"]!.GetValue<string>() != "raw_telling")
    { Reject("unexpected packet type or kind at the ingress"); return; }

    if (session == null || session["open"]?.GetValue<bool>() != true)
    { Reject("session not open for this connection"); return; }

    // The Consent valve is standing node state, mapped by packet
    // kind (the home node mapping): a raw telling maps to the OneLiner
    // class. No Consent in force for the class means the valve is
    // closed and the packet is rejected whole: one trail line, no
    // record event, no partial intake.
    var valves = LoadObj(ValvesPath);
    if ((valves[connId + ":OneLiner"]?.GetValue<string>() ?? "open") == "closed")
    {
        Reject("consent valve closed for class OneLiner on this connection (standing node Consent state)");
        return;
    }

    string eventId;
    lock (Gate)
    {
        var record = LoadArr(RecordPath);
        eventId = NewId("evt-");
        record.Add(new JsonObject
        {
            ["event_id"] = eventId,
            ["packet_id"] = packetId,
            ["connection_id"] = connId,
            ["origin"] = "federated",
            ["channel"] = "connected assistant",
            ["author_person"] = conn["person"]!.GetValue<string>(),
            ["body_text"] = body["text"]!.GetValue<string>(),
            ["told_at"] = plain["told_at"]!.GetValue<long>(),
            ["recorded_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["provenance"] = "via " + conn["person"]!.GetValue<string>() + " reference assistant, connection " + connId
        });
        SaveArr(RecordPath, record);
    }
    Trail(packetId, "recorded", "telling admitted, Consent in force for class OneLiner on this connection");

    // Sealed delivery outbox: this double routes a verbatim
    // delivery of Lisa's telling text to Daniel's connection. The home
    // node owns the real pipeline decision; the delivery mechanics
    // here are the stand-ins' exercise.
    if (conn["person"]!.GetValue<string>() == "Lisa")
    {
        JsonObject? daniel = null;
        string? danielConn = null;
        foreach (var (k, v) in LoadObj(ConnsPath))
        {
            var o = v!.AsObject();
            if (o["person"]!.GetValue<string>() == "Daniel" && o["standing"]!.GetValue<string>() == "enrolled")
            { danielConn = k; daniel = o; }
        }
        if (daniel != null)
        {
            var deliveryId = NewId("pkt-");
            var delivery = MakePlain("delivery",
                new JsonObject { ["kind"] = "delivery", ["text"] = body["text"]!.GetValue<string>() },
                daniel["party_id"]!.GetValue<string>(), danielConn!, deliveryId);
            await SendNodePacket(delivery, danielConn!, daniel["public_key"]!.GetValue<string>(), "session");
            if (body["text"]!.GetValue<string>().Contains("gauze"))
            {
                // The proposal act: the house sends Daniel a
                // proposal as a sealed delivery (kind proposal).
                // Only his acknowledgement disposition can dispose
                // it; a counter would have to arrive as a telling.
                var proposalId = NewId("pkt-");
                var proposal = MakePlain("delivery",
                    new JsonObject { ["kind"] = "proposal",
                        ["text"] = "The house asks Daniel: shall the house add gauze pads to the Thursday grocery run?" },
                    daniel["party_id"]!.GetValue<string>(), danielConn!, proposalId);
                lock (Gate)
                {
                    var proposals = LoadArr(ProposalsPath);
                    proposals.Add(new JsonObject
                    {
                        ["proposal_packet_id"] = proposalId,
                        ["to_connection"] = danielConn,
                        ["disposition"] = "pending"
                    });
                    SaveArr(ProposalsPath, proposals);
                }
                await SendNodePacket(proposal, danielConn!, daniel["public_key"]!.GetValue<string>(), "session");
            }
        }
    }
}

async Task PollLoop()
{
    while (true)
    {
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get,
                relayUrl + "/inbox?household_endpoint=" + HouseholdEndpoint);
            req.Headers.Add("X-Connection-Id", NodeConnId);
            req.Headers.Add("X-Connection-Token", nodeToken);
            var resp = await http.SendAsync(req);
            if (resp.IsSuccessStatusCode)
            {
                var parsed = JsonNode.Parse(await resp.Content.ReadAsStringAsync())!.AsObject();
                foreach (var entryNode in parsed["packets"]!.AsArray())
                {
                    var e = entryNode!.AsObject();
                    var envelope = new JsonObject();
                    foreach (var f in new[] { "ciphertext_hash", "ciphertext_length", "connection_id",
                        "delivery_mode", "envelope_class", "household_endpoint", "issued_at", "packet_id" })
                        envelope[f] = e[f]?.DeepClone();
                    var wire = new JsonObject
                    {
                        ["ciphertext"] = e["ciphertext"]!.GetValue<string>(),
                        ["envelope"] = envelope,
                        ["signature"] = e["signature"]!.GetValue<string>()
                    };
                    await ProcessPacket(wire);
                }
            }
        }
        catch { /* relay not up yet or briefly down; keep polling */ }
        await Task.Delay(400);
    }
}

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
_ = Task.Run(PollLoop);

app.MapPost("/door/knock", (JsonObject body) =>
{
    var invites = LoadArr(InvitesPath);
    JsonObject? invite = null;
    foreach (var i in invites)
    {
        var o = i!.AsObject();
        if (o["person"]!.GetValue<string>() == body["person"]!.GetValue<string>()
            && o["check_phrase"]!.GetValue<string>() == body["check_phrase"]!.GetValue<string>()
            && o["used"]!.GetValue<bool>() == false)
            invite = o;
    }
    if (invite == null)
    {
        Trail("knock", "rejected", "no matching unused invite for knock");
        return Results.Json(new { error = "no matching invite" }, statusCode: 403);
    }
    var pubRaw = Protocol.UnB64Url(body["public_key"]!.GetValue<string>());
    if (Protocol.Hex(SHA256.HashData(pubRaw)) != body["fingerprint"]!.GetValue<string>())
    {
        Trail("knock", "rejected", "fingerprint did not match the presented public key");
        return Results.Json(new { error = "fingerprint mismatch" }, statusCode: 403);
    }
    // Connection ids are the contract's frozen scope ids. the home node
    // owns the real registry; this double assigns the same ids.
    var partyId = body["party_id"]!.GetValue<string>();
    var connId = partyId switch
    {
        "member-lisa" => "conn-lisa-01",
        "member-daniel" => "conn-daniel-01",
        _ => null
    };
    if (connId == null)
        return Results.Json(new { error = "party outside the frozen scope" }, statusCode: 403);
    var (px, py) = Protocol.XYFromPubRaw(pubRaw);
    lock (Gate)
    {
        var conns = LoadObj(ConnsPath);
        conns[connId] = new JsonObject
        {
            ["person"] = body["person"]!.GetValue<string>(),
            ["party_id"] = partyId,
            ["provider_claim"] = body["provider_claim"]!.GetValue<string>(),
            ["public_key"] = body["public_key"]!.GetValue<string>(),
            ["public_x"] = px,
            ["public_y"] = py,
            ["fingerprint"] = body["fingerprint"]!.GetValue<string>(),
            ["credential_verifier"] = body["credential_verifier"]!.GetValue<string>(),
            ["invite_code"] = invite["code"]!.GetValue<string>(),
            ["standing"] = "pending"
        };
        SaveObj(ConnsPath, conns);
    }
    Trail("knock:" + connId, "pending", "knock accepted, waiting for a named inviter");
    return Results.Json(new { connection_id = connId, standing = "pending" });
});

app.MapGet("/door/card/{connId}", (string connId) =>
{
    var c = LoadObj(ConnsPath)[connId]?.AsObject();
    if (c == null) return Results.Json(new { error = "unknown connection" }, statusCode: 404);
    string? phrase = null;
    foreach (var i in LoadArr(InvitesPath))
    {
        var o = i!.AsObject();
        if (o["code"]!.GetValue<string>() == c["invite_code"]!.GetValue<string>())
            phrase = o["check_phrase"]!.GetValue<string>();
    }
    return Results.Json(new
    {
        person = c["person"]!.GetValue<string>(),
        provider_claim = c["provider_claim"]!.GetValue<string>(),
        fingerprint = c["fingerprint"]!.GetValue<string>(),
        check_phrase = phrase,
        standing = c["standing"]!.GetValue<string>()
    });
});

app.MapPost("/door/confirm", (JsonObject body) =>
{
    var connId = body["connection_id"]!.GetValue<string>();
    lock (Gate)
    {
        var conns = LoadObj(ConnsPath);
        var c = conns[connId]?.AsObject();
        if (c == null) return Results.Json(new { error = "unknown connection" }, statusCode: 404);
        JsonObject? invite = null;
        var invites = LoadArr(InvitesPath);
        foreach (var i in invites)
        {
            var o = i!.AsObject();
            if (o["code"]!.GetValue<string>() == c["invite_code"]!.GetValue<string>()) invite = o;
        }
        if (invite == null || invite["check_phrase"]!.GetValue<string>() != body["check_phrase"]!.GetValue<string>())
        {
            Trail("knock:" + connId, "rejected", "inviter check phrase did not match");
            return Results.Json(new { error = "check phrase mismatch" }, statusCode: 403);
        }
        c["standing"] = "enrolled";
        c["inviter"] = body["inviter"]!.GetValue<string>();
        invite["used"] = true;
        SaveObj(ConnsPath, conns);
        SaveArr(InvitesPath, invites);
    }
    Trail("knock:" + connId, "enrolled", "confirmed by " + body["inviter"]!.GetValue<string>());
    return Results.Json(new { standing = "enrolled" });
});

app.MapPost("/door/decline", (JsonObject body) =>
{
    var connId = body["connection_id"]!.GetValue<string>();
    lock (Gate)
    {
        var conns = LoadObj(ConnsPath);
        var c = conns[connId]?.AsObject();
        if (c == null) return Results.Json(new { error = "unknown connection" }, statusCode: 404);
        c["standing"] = "declined";
        SaveObj(ConnsPath, conns);
    }
    Trail("knock:" + connId, "declined", "inviter declined the join");
    return Results.Json(new { standing = "declined" });
});

app.MapGet("/door/status/{connId}", (string connId, HttpRequest req) =>
{
    var c = LoadObj(ConnsPath)[connId]?.AsObject();
    if (c == null) return Results.Json(new { error = "unknown connection" }, statusCode: 404);
    var cred = req.Headers["X-Credential"].ToString();
    var verifier = Protocol.Hex(SHA256.HashData(Encoding.UTF8.GetBytes(cred)));
    if (verifier != c["credential_verifier"]!.GetValue<string>())
        return Results.Json(new { error = "credential mismatch" }, statusCode: 403);
    return Results.Json(new
    {
        standing = c["standing"]!.GetValue<string>(),
        node_public_key = c["standing"]!.GetValue<string>() == "enrolled" ? nodePubRaw : null
    });
});

app.MapPost("/admin/valve", (JsonObject body) =>
{
    // Standing Consent state for one connection and one material
    // class, following the home node mapping. This is node side
    // state; packets carry no valve field in band.
    var cls = body["material_class"]?.GetValue<string>() ?? "OneLiner";
    lock (Gate)
    {
        var valves = LoadObj(ValvesPath);
        valves[body["connection_id"]!.GetValue<string>() + ":" + cls] = body["state"]!.GetValue<string>();
        SaveObj(ValvesPath, valves);
    }
    return Results.Json(new { valve = body["state"]!.GetValue<string>(), material_class = cls });
});

app.MapGet("/harness/record", () => Results.Json(LoadArr(RecordPath)));
app.MapGet("/harness/trail", () => Results.Json(LoadArr(TrailPath)));
app.MapGet("/harness/acks", () => Results.Json(LoadArr(AcksPath)));
app.MapGet("/harness/connections", () => Results.Json(LoadObj(ConnsPath)));
app.MapGet("/harness/sessions", () => Results.Json(LoadObj(SessionsPath)));
app.MapGet("/harness/proposals", () => Results.Json(LoadArr(ProposalsPath)));

app.Run();
