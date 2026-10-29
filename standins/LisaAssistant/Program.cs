using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LisaAssistant;

// Reference assistant for Lisa, a fictional member of the demo
// household. This program is a stand-in: it speaks the Tellhouse
// packet protocol (the protocol contract, version 1.0, frozen) so real
// protocol traffic exists to film and to test. It is not Lisa's
// real assistant, and it is labeled as a reference assistant in
// its own docs and on its own surface.
//
// It holds its own keypair and its own store. It has no reference
// to the home node's data layer, no shared database access, and
// no route that returns packet bodies.

const string PersonName = "Lisa";
const string PartyId = "member-lisa";
const string ProviderClaim = "Lisa reference assistant (self-asserted)";
const string NodePartyId = "household-node";
const string HouseholdEndpoint = "hh-demo-01";

if (args.Contains("selftest"))
{
    var vp = Environment.GetEnvironmentVariable("VECTORS_PATH") ?? "test-vectors.json";
    Environment.Exit(Selftest.Run(vp));
}

string storeDir = Environment.GetEnvironmentVariable("STANDIN_STORE") ?? "store";
Directory.CreateDirectory(storeDir);
string KeysPath = Path.Combine(storeDir, "keys.json");
string StatePath = Path.Combine(storeDir, "state.json");
string OutboxPath = Path.Combine(storeDir, "outbox.json");
string InboxPath = Path.Combine(storeDir, "inbox.json");
string SentPath = Path.Combine(storeDir, "sent.json");
string RefusalsPath = Path.Combine(storeDir, "refusals.json");
string SeenPath = Path.Combine(storeDir, "seen-nonces.json");
string InvitePath = Path.Combine(storeDir, "pending-invite.json");
string relayUrl = Environment.GetEnvironmentVariable("RELAY_URL") ?? "http://127.0.0.1:7411";
string nodeUrl = Environment.GetEnvironmentVariable("NODE_URL") ?? "http://127.0.0.1:7412";
string scriptPath = Environment.GetEnvironmentVariable("SCRIPT_PATH")
    ?? Path.Combine(AppContext.BaseDirectory, "script.json");

JsonObject LoadJson(string path, JsonObject fallback) =>
    File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsObject() : fallback;
void SaveJson(string path, JsonObject obj) =>
    File.WriteAllText(path, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
JsonArray LoadArr(string path) =>
    File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsArray() : new JsonArray();
void SaveArr(string path, JsonArray arr) =>
    File.WriteAllText(path, arr.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

// One identity keypair, generated locally on first run (contract
// section 1). The private key never leaves this store.
if (!File.Exists(KeysPath))
{
    var (kd, kx, ky) = Protocol.NewKeyPair();
    SaveJson(KeysPath, new JsonObject { ["d"] = kd, ["x"] = kx, ["y"] = ky });
}
var keys = LoadJson(KeysPath, new JsonObject());
string KD = keys["d"]!.GetValue<string>();
string KX = keys["x"]!.GetValue<string>();
string KY = keys["y"]!.GetValue<string>();
string fingerprint = Protocol.Fingerprint(KX, KY);
string pubRawB64 = Protocol.B64Url(Protocol.PubRaw(KX, KY));

JsonObject Script() => LoadJson(scriptPath, new JsonObject());
using var http = Protocol.LocalHttp();

string NewPacketId() => "pkt-" + Protocol.Hex(RandomNumberGenerator.GetBytes(8));
string NewNonce() => Protocol.B64Url(RandomNumberGenerator.GetBytes(16));

JsonObject MakePlain(string type, JsonObject body, string recipient, string packetId,
    JsonObject? proof = null)
{
    var plain = new JsonObject
    {
        ["body"] = body,
        ["connection_id"] = LoadJson(StatePath, new JsonObject())["connection_id"]!.GetValue<string>(),
        ["packet_id"] = packetId,
        ["packet_nonce"] = NewNonce(),
        ["packet_type"] = type,
        ["recipient_id"] = recipient,
        ["sender_id"] = PartyId,
        ["told_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
    };
    if (proof != null) plain["proof"] = proof;
    return plain;
}

JsonObject BuildForNode(JsonObject plain, string envelopeClass, string packetId)
{
    var state = LoadJson(StatePath, new JsonObject());
    var nodePub = Protocol.UnB64Url(state["node_public_key"]!.GetValue<string>());
    return Protocol.BuildPacket(KD, KX, KY, nodePub, plain,
        state["connection_id"]!.GetValue<string>(), "session", envelopeClass,
        HouseholdEndpoint, packetId, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
}

async Task<(int status, string body)> RelaySend(JsonObject wire)
{
    var state = LoadJson(StatePath, new JsonObject());
    try
    {
        var req = new HttpRequestMessage(HttpMethod.Post, relayUrl + "/packets");
        req.Headers.Add("X-Connection-Id", state["connection_id"]!.GetValue<string>());
        req.Headers.Add("X-Connection-Token", state["credential"]!.GetValue<string>());
        req.Content = new StringContent(Protocol.Canonical(wire), Encoding.UTF8, "application/json");
        var resp = await http.SendAsync(req);
        return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync());
    }
    catch (HttpRequestException)
    {
        return (0, "relay unreachable");
    }
}

async Task<List<JsonObject>?> RelayPoll()
{
    var state = LoadJson(StatePath, new JsonObject());
    try
    {
        var req = new HttpRequestMessage(HttpMethod.Get,
            relayUrl + "/inbox?household_endpoint=" + HouseholdEndpoint);
        req.Headers.Add("X-Connection-Id", state["connection_id"]!.GetValue<string>());
        req.Headers.Add("X-Connection-Token", state["credential"]!.GetValue<string>());
        var resp = await http.SendAsync(req);
        if (!resp.IsSuccessStatusCode) return null;
        var parsed = JsonNode.Parse(await resp.Content.ReadAsStringAsync())!.AsObject();
        var wires = new List<JsonObject>();
        foreach (var entryNode in parsed["packets"]!.AsArray())
        {
            var e = entryNode!.AsObject();
            var envelope = new JsonObject();
            foreach (var f in new[] { "ciphertext_hash", "ciphertext_length", "connection_id",
                "delivery_mode", "envelope_class", "household_endpoint", "issued_at", "packet_id" })
                envelope[f] = e[f]?.DeepClone();
            wires.Add(new JsonObject
            {
                ["ciphertext"] = e["ciphertext"]!.GetValue<string>(),
                ["envelope"] = envelope,
                ["signature"] = e["signature"]!.GetValue<string>()
            });
        }
        return wires;
    }
    catch (HttpRequestException) { return null; }
}

void QueueLocal(JsonObject envelope, string act)
{
    var arr = LoadArr(OutboxPath);
    arr.Add(new JsonObject
    {
        ["act"] = act,
        ["queued_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        ["record_state"] = "unrecorded",
        ["envelope"] = envelope.DeepClone()
    });
    SaveArr(OutboxPath, arr);
}

void Refuse(string packetId, string reason)
{
    var refusals = LoadArr(RefusalsPath);
    refusals.Add(new JsonObject
    {
        ["packet_id"] = packetId,
        ["reason"] = reason,
        ["at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
    });
    SaveArr(RefusalsPath, refusals);
}

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/", () => Results.Text(
    "Lisa reference assistant. A stand-in program for Lisa, a fictional member of the demo household, " +
    "speaking the Tellhouse packet protocol, contract version 1.0. It is not a real person's assistant. " +
    "This surface shows status metadata only; it has no route that returns packet bodies."));

app.MapGet("/status", () =>
{
    var state = LoadJson(StatePath, new JsonObject());
    return Results.Json(new
    {
        assistant = "Lisa reference assistant (stand-in for a fictional member)",
        person = PersonName,
        party_id = PartyId,
        provider_claim = ProviderClaim,
        fingerprint,
        joined = state["joined"]?.GetValue<bool>() ?? false,
        invite_disposed = state["invite_disposed"]?.GetValue<bool>() ?? false,
        connection_id = state["connection_id"]?.GetValue<string>(),
        session_proof_sent = state["session_proof_sent"]?.GetValue<bool>() ?? false,
        outbox_unrecorded = LoadArr(OutboxPath).Count,
        inbox_deliveries = LoadArr(InboxPath).Count,
        refusals = LoadArr(RefusalsPath).Count
    });
});

app.MapPost("/act/join-begin", async () =>
{
    if (!File.Exists(InvitePath))
        return Results.Json(new { error = "no pending invite in this store" }, statusCode: 400);
    var invite = LoadJson(InvitePath, new JsonObject());
    var credential = Protocol.InviteCredential(invite["code"]!.GetValue<string>(), fingerprint);
    var knock = new JsonObject
    {
        ["person"] = PersonName,
        ["party_id"] = PartyId,
        ["provider_claim"] = ProviderClaim,
        ["public_key"] = pubRawB64,
        ["fingerprint"] = fingerprint,
        ["check_phrase"] = invite["check_phrase"]!.GetValue<string>(),
        ["credential_verifier"] = Protocol.Hex(SHA256.HashData(Encoding.UTF8.GetBytes(credential)))
    };
    var resp = await http.PostAsync(nodeUrl + "/door/knock",
        new StringContent(knock.ToJsonString(), Encoding.UTF8, "application/json"));
    var body = await resp.Content.ReadAsStringAsync();
    if (!resp.IsSuccessStatusCode)
        return Results.Json(new { error = "knock refused", detail = body }, statusCode: 502);
    var parsed = JsonNode.Parse(body)!.AsObject();
    var state = LoadJson(StatePath, new JsonObject());
    state["connection_id"] = parsed["connection_id"]!.GetValue<string>();
    state["credential"] = credential;
    state["joined"] = false;
    SaveJson(StatePath, state);
    return Results.Json(new { knocked = true, connection_id = state["connection_id"]!.GetValue<string>() });
});

app.MapPost("/act/join-complete", async () =>
{
    var state = LoadJson(StatePath, new JsonObject());
    if (state["connection_id"] == null)
        return Results.Json(new { error = "no knock yet" }, statusCode: 400);
    var req = new HttpRequestMessage(HttpMethod.Get,
        nodeUrl + "/door/status/" + state["connection_id"]!.GetValue<string>());
    req.Headers.Add("X-Credential", state["credential"]!.GetValue<string>());
    var resp = await http.SendAsync(req);
    var body = await resp.Content.ReadAsStringAsync();
    if (!resp.IsSuccessStatusCode)
        return Results.Json(new { error = "status refused", detail = body }, statusCode: 502);
    var parsed = JsonNode.Parse(body)!.AsObject();
    if (parsed["standing"]!.GetValue<string>() != "enrolled")
        return Results.Json(new { joined = false, standing = parsed["standing"]!.GetValue<string>() });
    state["node_public_key"] = parsed["node_public_key"]!.GetValue<string>();
    state["joined"] = true;
    // Explicit disposal: the raw invite leaves this store for good.
    // Only the derived credential stays.
    if (File.Exists(InvitePath)) File.Delete(InvitePath);
    state["invite_disposed"] = true;
    SaveJson(StatePath, state);
    return Results.Json(new { joined = true, invite_disposed = true });
});

app.MapPost("/act/session-open", async () =>
{
    var state = LoadJson(StatePath, new JsonObject());
    if (state["joined"]?.GetValue<bool>() != true)
        return Results.Json(new { error = "not enrolled through the door" }, statusCode: 409);
    // Contract section 7, step 1: a knock class telling packet,
    // body kind session_open, body text the client nonce.
    var clientNonce = NewNonce();
    var packetId = NewPacketId();
    var plain = MakePlain("telling",
        new JsonObject { ["kind"] = "session_open", ["text"] = clientNonce },
        NodePartyId, packetId);
    state["pending_client_nonce"] = clientNonce;
    state["pending_open_packet"] = packetId;
    SaveJson(StatePath, state);
    var wire = BuildForNode(plain, "knock", packetId);
    var (status, respBody) = await RelaySend(wire);
    if (status == 0)
    {
        QueueLocal(wire, "session-open");
        return Results.Json(new { result = "relay unreachable, knock queued locally, unrecorded" }, statusCode: 202);
    }
    return Results.Json(new { packet_id = packetId, relay_status = status, relay_result = respBody });
});

app.MapPost("/act/session-answer", async () =>
{
    var state = LoadJson(StatePath, new JsonObject());
    if (state["pending_client_nonce"] == null)
        return Results.Json(new { error = "no session open in progress" }, statusCode: 409);
    var nodePubRaw = Protocol.UnB64Url(state["node_public_key"]!.GetValue<string>());
    var (nx, ny) = Protocol.XYFromPubRaw(nodePubRaw);
    // The challenge arrives asynchronously: the node polls the
    // relay on its own loop, answers, and the answer queues for
    // this poll. Retry for a few seconds before giving up.
    for (int attempt = 0; attempt < 15; attempt++)
    {
    var wires = await RelayPoll();
    if (wires == null)
        return Results.Json(new { error = "relay unreachable or poll refused" }, statusCode: 502);
    foreach (var wire in wires)
    {
        var packetId = wire["envelope"]!["packet_id"]!.GetValue<string>();
        if (!Protocol.VerifyPacket(nx, ny, wire))
        {
            Refuse(packetId, "packet signature did not verify under the node key");
            continue;
        }
        JsonObject plain;
        try
        {
            plain = JsonNode.Parse(Encoding.UTF8.GetString(
                Protocol.OpenPacket(KD, KX, KY, wire)))!.AsObject();
        }
        catch { Refuse(packetId, "packet did not open under this member key"); continue; }
        var body = plain["body"]!.AsObject();
        if (plain["packet_type"]!.GetValue<string>() != "delivery"
            || body["kind"]!.GetValue<string>() != "session_challenge")
        {
            // Not the challenge: put it back by treating it as a
            // delivery for the poll path. Session answers only
            // consume challenges; anything else is stored as an
            // inbox delivery without an acknowledgement here.
            var inbox = LoadArr(InboxPath);
            inbox.Add(new JsonObject
            {
                ["packet_id"] = packetId,
                ["type"] = plain["packet_type"]!.GetValue<string>(),
                ["body_text"] = body["text"]!.GetValue<string>(),
                ["received_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["provenance"] = "reported, not observed"
            });
            SaveArr(InboxPath, inbox);
            continue;
        }
        // Contract section 7, step 3: the proof over the transcript.
        var serverNonce = body["text"]!.GetValue<string>();
        var clientNonce = state["pending_client_nonce"]!.GetValue<string>();
        var transcript = Protocol.Transcript(state["connection_id"]!.GetValue<string>(),
            Protocol.UnB64Url(clientNonce), Protocol.UnB64Url(serverNonce));
        var proof = new JsonObject
        {
            ["client_nonce"] = clientNonce,
            ["server_nonce"] = serverNonce,
            ["proof_signature"] = Protocol.Sign(KD, KX, KY, transcript)
        };
        var ackId = NewPacketId();
        var ackPlain = MakePlain("acknowledgement",
            new JsonObject { ["acked_packet_id"] = packetId, ["disposition"] = "received" },
            NodePartyId, ackId, proof);
        var ackWire = BuildForNode(ackPlain, "session", ackId);
        var (status, respBody) = await RelaySend(ackWire);
        state["session_proof_sent"] = status == 202;
        state["pending_client_nonce"] = null;
        SaveJson(StatePath, state);
        return Results.Json(new
        {
            challenge_packet = packetId,
            proof_packet = ackId,
            relay_status = status,
            relay_result = respBody
        });
    }
    if (attempt < 14) await Task.Delay(800);
    }
    return Results.Json(new { error = "no session challenge among polled packets" }, statusCode: 404);
});

app.MapPost("/trigger/test-send", async (HttpRequest req) =>
{
    // The trigger is a remote control only. It carries an act id
    // from this stand-in's own script and no telling content of
    // its own. A request that tries to supply content is refused
    // whole.
    var bodyText = await new StreamReader(req.Body).ReadToEndAsync();
    JsonObject body;
    try { body = JsonNode.Parse(string.IsNullOrWhiteSpace(bodyText) ? "{}" : bodyText)!.AsObject(); }
    catch { return Results.Json(new { error = "bad request body" }, statusCode: 400); }
    foreach (var field in new[] { "text", "body", "telling", "content", "message", "words" })
        if (body.ContainsKey(field))
            return Results.Json(new { error = "trigger carries no telling content" }, statusCode: 400);
    var act = body["act"]?.GetValue<string>();
    if (act == null)
        return Results.Json(new { error = "act id required" }, statusCode: 400);
    var script = Script();
    if (script["acts"]?[act] == null)
        return Results.Json(new { error = "unknown act" }, statusCode: 400);
    var state = LoadJson(StatePath, new JsonObject());
    if (state["joined"]?.GetValue<bool>() != true)
        return Results.Json(new { error = "not enrolled through the door" }, statusCode: 409);
    var actDef = script["acts"]![act]!.AsObject();
    var packetId = NewPacketId();
    var plain = MakePlain("telling",
        new JsonObject { ["kind"] = "raw_telling", ["text"] = actDef["telling"]!.GetValue<string>() },
        NodePartyId, packetId);
    var wire = BuildForNode(plain, "session", packetId);
    var (status, respBody) = await RelaySend(wire);
    var sent = LoadArr(SentPath);
    if (status == 0)
    {
        QueueLocal(wire, act);
        sent.Add(new JsonObject
        {
            ["act"] = act, ["packet_id"] = packetId,
            ["result"] = "queued locally, unrecorded", ["at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        });
        SaveArr(SentPath, sent);
        return Results.Json(new { act, packet_id = packetId, result = "queued locally, unrecorded" }, statusCode: 202);
    }
    sent.Add(new JsonObject
    {
        ["act"] = act, ["packet_id"] = packetId,
        ["result"] = respBody, ["at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        ["wire"] = wire.DeepClone()
    });
    SaveArr(SentPath, sent);
    return Results.Json(new { act, packet_id = packetId, relay_status = status, relay_result = respBody });
});

app.MapPost("/act/poll", async () =>
{
    var state = LoadJson(StatePath, new JsonObject());
    if (state["joined"]?.GetValue<bool>() != true)
        return Results.Json(new { error = "not enrolled" }, statusCode: 409);
    var wires = await RelayPoll();
    if (wires == null)
        return Results.Json(new { error = "relay unreachable or poll refused" }, statusCode: 502);
    var nodePubRaw = Protocol.UnB64Url(state["node_public_key"]!.GetValue<string>());
    var (nx, ny) = Protocol.XYFromPubRaw(nodePubRaw);
    int opened = 0, refused = 0, acked = 0;
    foreach (var wire in wires)
    {
        var envelope = wire["envelope"]!.AsObject();
        var packetId = envelope["packet_id"]!.GetValue<string>();
        if (!Protocol.VerifyPacket(nx, ny, wire))
        {
            Refuse(packetId, "packet signature did not verify under the node key");
            refused++;
            continue;
        }
        JsonObject plain;
        try
        {
            plain = JsonNode.Parse(Encoding.UTF8.GetString(
                Protocol.OpenPacket(KD, KX, KY, wire)))!.AsObject();
        }
        catch
        {
            // Refused whole. Nothing is stored, nothing is
            // acknowledged, and no substitute text is invented.
            Refuse(packetId, "delivery did not open under this member key");
            refused++;
            continue;
        }
        bool shapeOk =
            plain["packet_id"]!.GetValue<string>() == packetId
            && plain["connection_id"]!.GetValue<string>() == envelope["connection_id"]!.GetValue<string>()
            && plain["sender_id"]!.GetValue<string>() == NodePartyId
            && plain["recipient_id"]!.GetValue<string>() == PartyId
            && plain["packet_type"]!.GetValue<string>() == "delivery";
        var seen = LoadArr(SeenPath);
        var nonce = plain["packet_nonce"]!.GetValue<string>();
        bool nonceFresh = !seen.Any(n => n!.GetValue<string>() == nonce);
        if (!shapeOk || !nonceFresh)
        {
            Refuse(packetId, !shapeOk ? "plain packet did not match its envelope or enrollment"
                                      : "packet nonce already used on this connection");
            refused++;
            continue;
        }
        seen.Add(nonce);
        SaveArr(SeenPath, seen);
        var inbox = LoadArr(InboxPath);
        inbox.Add(new JsonObject
        {
            ["packet_id"] = packetId,
            ["type"] = plain["packet_type"]!.GetValue<string>(),
            ["kind"] = plain["body"]!["kind"]!.GetValue<string>(),
            ["body_text"] = plain["body"]!["text"]!.GetValue<string>(),
            ["received_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["provenance"] = "reported, not observed"
        });
        SaveArr(InboxPath, inbox);
        opened++;
        // The acknowledgement is built from this actual receipt:
        // it names the delivery packet that opened and verified.
        // Its disposition is received, a receipt report, never a
        // confirmation of any record event.
        var ackId = NewPacketId();
        var ackPlain = MakePlain("acknowledgement",
            new JsonObject { ["acked_packet_id"] = packetId, ["disposition"] = "received" },
            NodePartyId, ackId);
        var ackWire = BuildForNode(ackPlain, "session", ackId);
        var (status, _) = await RelaySend(ackWire);
        if (status == 0) QueueLocal(ackWire, "acknowledgement");
        else acked++;
    }
    return Results.Json(new { polled = wires.Count, opened, refused, acked });
});

app.Run();
