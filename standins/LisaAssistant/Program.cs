using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LisaAssistant;

// Reference assistant for Lisa, a fictional member of the demo
// household. This program is a stand-in: it speaks the Tellhouse
// packet protocol (contract version 1.0, frozen) so real
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
string RecoveriesPath = Path.Combine(storeDir, "session-recoveries.json");
string SeenPath = Path.Combine(storeDir, "seen-nonces.json");
string HeldPath = Path.Combine(storeDir, "held.json");
string InvitePath = Path.Combine(storeDir, "pending-invite.json");
string relayUrl = Environment.GetEnvironmentVariable("RELAY_URL") ?? "http://127.0.0.1:7411";
string nodeUrl = Environment.GetEnvironmentVariable("NODE_URL") ?? "http://127.0.0.1:7412";
string scriptPath = Environment.GetEnvironmentVariable("SCRIPT_PATH")
    ?? Path.Combine(AppContext.BaseDirectory, "script.json");

JsonObject LoadJson(string path, JsonObject fallback) =>
    File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsObject() : fallback;
// The state file holds this assistant's live connection
// credential, so it is written through the key file writer:
// created owner read/write only (0600) whatever the umask says,
// and tightened again on every write, so a state file an older
// build left readable is repaired the next time state is saved.
void SaveState(JsonObject state) =>
    Protocol.WriteKeyFile(StatePath,
        state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
JsonArray LoadArr(string path) =>
    File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsArray() : new JsonArray();
void SaveArr(string path, JsonArray arr) =>
    File.WriteAllText(path, arr.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

// One identity keypair, generated locally on first run (contract
// section 1). The private key never leaves this store:
// keys.json is written owner only (0600), and a key file left
// readable by an older build is tightened before it is read.
if (!File.Exists(KeysPath))
{
    var (kd, kx, ky) = Protocol.NewKeyPair();
    Protocol.WriteKeyFile(KeysPath,
        new JsonObject { ["d"] = kd, ["x"] = kx, ["y"] = ky }
            .ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
}
Protocol.TightenKeyFile(KeysPath);
// The same tightening on load for the two files that carry
// credential material: the state file (the live connection
// credential) and a pending invite (the raw code a connection
// is minted from). Either may have been left readable by an
// older build; both are read at startup, so both are tightened
// at startup.
if (File.Exists(StatePath))
{
    Protocol.TightenKeyFile(StatePath);
}
if (File.Exists(InvitePath))
{
    Protocol.TightenKeyFile(InvitePath);
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


// ------------------------------------------------------------------
// Incoming delivery processing: the acknowledging path. The relay
// poll is destructive (it takes every queued packet for this
// connection), so every packet this stand-in takes off the relay
// is accounted for in exactly one of three ways: processed here
// (verified, opened, stored, and acknowledged from the actual
// receipt), refused with a recorded reason, or HELD in the store
// for this path when another exchange (the session answer hunt)
// took it first. Nothing taken is ever silently dropped, and no
// delivery is stored without its acknowledgement being sent or
// queued: a delivery that arrives during a cold session exchange
// waits in held.json and is drained through this path once the
// session is open, or by the next poll.
var heldLock = new object();

async Task<(int opened, int refused, int acked)> ProcessDeliveryWireAsync(JsonObject wire)
{
    var st = LoadJson(StatePath, new JsonObject());
    var nodePubRaw = Protocol.UnB64Url(st["node_public_key"]!.GetValue<string>());
    var (nx, ny) = Protocol.XYFromPubRaw(nodePubRaw);
    var envelope = wire["envelope"]!.AsObject();
    var packetId = envelope["packet_id"]!.GetValue<string>();
    try
    {
        if (!Protocol.VerifyPacket(nx, ny, wire))
        {
            Refuse(packetId, "packet signature did not verify under the node key");
            return (0, 1, 0);
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
            return (0, 1, 0);
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
            return (0, 1, 0);
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
        if (status == 0)
        {
            QueueLocal(ackWire, "acknowledgement");
            return (1, 0, 0);
        }
        return (1, 0, 1);
    }
    catch (Exception ex)
    {
        // A malformed packet must not strand the packets queued
        // behind it: refuse it by its envelope id and let the
        // caller move on to the next one.
        Refuse(packetId, "delivery processing failed: " + ex.Message);
        return (0, 1, 0);
    }
}

void HoldWire(JsonObject wire)
{
    lock (heldLock)
    {
        var arr = LoadArr(HeldPath);
        arr.Add(wire.DeepClone());
        SaveArr(HeldPath, arr);
    }
}

async Task<int> DrainHeldAsync()
{
    List<JsonObject> held;
    lock (heldLock)
    {
        held = LoadArr(HeldPath).Select(n => n!.AsObject()).ToList();
        if (held.Count > 0) SaveArr(HeldPath, new JsonArray());
    }
    var processed = 0;
    foreach (var wire in held)
    {
        try
        {
            await ProcessDeliveryWireAsync(wire);
            processed++;
        }
        catch
        {
            // Not processed: put it back for the next drain
            // rather than dropping a taken packet.
            HoldWire(wire);
        }
    }
    return processed;
}

// The recorded open attempts of the session exchange: every knock
// this stand-in has sent that has not been answered yet, newest
// last. Stores written before attempt history existed carry only
// the single pending nonce; it is read as one attempt whose send
// time is the recorded open time when present.
List<SessionHunt.Attempt> ReadAttempts(JsonObject state)
{
    var list = new List<SessionHunt.Attempt>();
    if (state["open_attempts"] is JsonArray arr)
    {
        foreach (var n in arr)
        {
            var o = n!.AsObject();
            if (o["client_nonce"] != null && o["sent_at"] != null)
                list.Add(new SessionHunt.Attempt(
                    o["client_nonce"]!.GetValue<string>(), o["sent_at"]!.GetValue<long>()));
        }
    }
    if (list.Count == 0 && state["pending_client_nonce"] != null)
    {
        list.Add(new SessionHunt.Attempt(
            state["pending_client_nonce"]!.GetValue<string>(),
            state["open_sent_at"]?.GetValue<long>() ?? 0));
    }
    return list;
}


// ------------------------------------------------------------------
// Self-operating session mode (auto session), gated by the
// STANDIN_AUTO_SESSION environment variable. The gate defaults off:
// when the variable is unset or is not "1", nothing in this section
// runs and every driver facing behavior is exactly as before.
//
// The session open and session answer bodies live in the two core
// functions below so the /act endpoints and the auto session path
// run one and the same exchange: the section 7 knock, challenge,
// and proof. There is no bypass and no second code path.
bool AutoSessionEnabled() =>
    Environment.GetEnvironmentVariable("STANDIN_AUTO_SESSION") == "1";

var sessionGate = new SemaphoreSlim(1, 1);

async Task<(bool ok, int httpStatus, object payload)> SessionOpenCoreAsync()
{
    var state = LoadJson(StatePath, new JsonObject());
    if (state["joined"]?.GetValue<bool>() != true)
        return (false, 409, new { error = "not enrolled through the door" });
    // Contract section 7, step 1: a knock class telling packet,
    // body kind session_open, body text the client nonce.
    var clientNonce = NewNonce();
    var packetId = NewPacketId();
    var plain = MakePlain("telling",
        new JsonObject { ["kind"] = "session_open", ["text"] = clientNonce },
        NodePartyId, packetId);
    state["pending_client_nonce"] = clientNonce;
    state["pending_open_packet"] = packetId;
    // Every open attempt is recorded with its send time. The
    // answer hunt pairs the node's challenge with the attempt
    // that prompted it (see SessionHunt in Protocol.cs); without
    // the attempt history, a challenge left over from an earlier
    // interrupted attempt cannot be told apart from this one.
    var attempts = state["open_attempts"]?.AsArray() ?? new JsonArray();
    attempts.Add(new JsonObject
    {
        ["client_nonce"] = clientNonce,
        ["sent_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
    });
    while (attempts.Count > 5) attempts.RemoveAt(0);
    state["open_attempts"] = attempts;
    state["open_sent_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    SaveState(state);
    var wire = BuildForNode(plain, "knock", packetId);
    var (status, respBody) = await RelaySend(wire);
    if (status == 0)
    {
        QueueLocal(wire, "session-open");
        return (false, 202, new { result = "relay unreachable, knock queued locally, unrecorded" });
    }
    // ok here means the knock reached the relay rather than being
    // queued locally. Proof success is reported by the answer core.
    return (true, 200, new { packet_id = packetId, relay_status = status, relay_result = respBody });
}

async Task<(bool ok, int httpStatus, object payload)> SessionAnswerCoreAsync()
{
    var state = LoadJson(StatePath, new JsonObject());
    var attempts = ReadAttempts(state);
    if (attempts.Count == 0)
        return (false, 409, new { error = "no session open in progress" });
    var nodePubRaw = Protocol.UnB64Url(state["node_public_key"]!.GetValue<string>());
    var (nx, ny) = Protocol.XYFromPubRaw(nodePubRaw);
    // The latest attempt is the last one sent (list order);
    // MaxBy would return the FIRST maximal attempt on a
    // same-second tie, which is the superseded knock (see
    // SessionHunt in Protocol.cs).
    var latest = attempts[^1];
    var candidates = new List<SessionHunt.Candidate>();
    SessionHunt.Choice? choice = null;
    long settleUntil = 0;
    // The challenge arrives asynchronously: the node polls the
    // relay on its own loop, answers, and the answer queues for
    // this poll. The hunt runs up to 60 seconds (the challenge
    // lifetime is 120): a slow node loop must cost waiting here,
    // not the whole session. Every packet the destructive poll
    // takes is accounted for. A challenge that answers this
    // exchange is a candidate; a challenge from a superseded or
    // expired attempt is refused by record and skipped (answering
    // it would mix two exchanges' nonces and the node would
    // rightly reject the proof, while the real challenge, taken
    // by the same poll, could never be answered at all). Anything
    // else the poll takes is HELD, raw, for the acknowledging
    // path, and drained once this session is open.
    var deadline = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60;
    while (true)
    {
        var nowS = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var wires = await RelayPoll();
        if (wires == null)
            return (false, 502, new { error = "relay unreachable or poll refused" });
        foreach (var wire in wires)
        {
            var env = wire["envelope"]!.AsObject();
            var packetId = env["packet_id"]!.GetValue<string>();
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
            var body = plain["body"] as JsonObject;
            bool isChallenge =
                plain["packet_type"]?.GetValue<string>() == "delivery"
                && body?["kind"]?.GetValue<string>() == "session_challenge"
                && body["text"] != null;
            if (!isChallenge)
            {
                HoldWire(wire);
                continue;
            }
            var cand = new SessionHunt.Candidate(packetId,
                body!["text"]!.GetValue<string>(),
                env["issued_at"]?.GetValue<long>() ?? 0);
            if (!SessionHunt.IsAnswerable(latest, cand, nowS))
            {
                Refuse(packetId, "session challenge from a superseded or expired open attempt; not answered");
                continue;
            }
            candidates.Add(cand);
        }
        var newChoice = SessionHunt.Choose(attempts, candidates, nowS);
        if (newChoice != null && (choice == null || newChoice.PacketId != choice.PacketId))
        {
            choice = newChoice;
            // Settle briefly before answering: a newer challenge
            // for this same exchange may already be in flight
            // behind this one, and the newest is the one the
            // node holds open.
            settleUntil = nowS + 3;
        }
        if (choice != null && nowS >= settleUntil) break;
        if (nowS >= deadline) break;
        await Task.Delay(800);
    }
    if (choice == null)
        return (false, 404, new { error = "no session challenge among polled packets" });
    // Contract section 7, step 3: the proof over the transcript,
    // pairing the chosen challenge with the open attempt that
    // prompted it, as recorded in the attempt history.
    var transcript = Protocol.Transcript(state["connection_id"]!.GetValue<string>(),
        Protocol.UnB64Url(choice.ClientNonce), Protocol.UnB64Url(choice.ServerNonce));
    var proof = new JsonObject
    {
        ["client_nonce"] = choice.ClientNonce,
        ["server_nonce"] = choice.ServerNonce,
        ["proof_signature"] = Protocol.Sign(KD, KX, KY, transcript)
    };
    var ackId = NewPacketId();
    var ackPlain = MakePlain("acknowledgement",
        new JsonObject { ["acked_packet_id"] = choice.PacketId, ["disposition"] = "received" },
        NodePartyId, ackId, proof);
    var ackWire = BuildForNode(ackPlain, "session", ackId);
    var (status, respBody) = await RelaySend(ackWire);
    state = LoadJson(StatePath, new JsonObject());
    state["session_proof_sent"] = status == 202;
    if (status == 202)
        state["session_open_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    state["pending_client_nonce"] = null;
    state["open_attempts"] = new JsonArray();
    SaveState(state);
    if (status == 202)
    {
        // The proof this exchange sent is what the held packets
        // were waiting for: their acknowledgements are session
        // class. Drain them through the acknowledging path now.
        await DrainHeldAsync();
    }
    // ok is the proof sent report: true only when the relay
    // accepted the acknowledgement carrying the proof.
    return (status == 202, 200, new
    {
        challenge_packet = choice.PacketId,
        proof_packet = ackId,
        relay_status = status,
        relay_result = respBody
    });
}

// Silent session recovery lives here, as one mechanism with
// this auto session family: RecordSessionRecovery is the audit
// trail for it (a store file beside sent.json, plus the console),
// written for recovery events and never shown in a presented
// result. The recovery itself is not a new path: it is this same
// exchange, run at the moment a presented send needs it.
void RecordSessionRecovery(string what)
{
    try
    {
        var log = LoadArr(RecoveriesPath);
        log.Add(new JsonObject
        {
            ["at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["event"] = what
        });
        SaveArr(RecoveriesPath, log);
    }
    catch { /* the audit write never blocks a send */ }
    Console.WriteLine("session-recovery: " + what);
}

// Ensure a session proof is fresh enough for a session class
// telling, running the real open and answer exchange when it is
// not. A recorded proof younger than 20 minutes is fresh (the node
// accepts session class tellings within 30 minutes of a verified
// session open proof). Returns true when fresh. On relay
// unreachability or any failure it returns false and the caller
// proceeds anyway: the subsequent send attempt then queues
// unrecorded exactly as it does without auto session, which is the
// honest relay stopped behavior. The semaphore keeps concurrent
// triggers from racing two opens.
//
// forceExchange: a presented send must not trust the
// recorded proof's age. The proof records when this stand-in last
// SENT a proof the relay accepted; whether the node still holds
// that session is not observable from here, and after a node reset
// it does not, while the recorded proof still looks fresh. So a
// presented send runs the real exchange first, every time: if the
// node has forgotten the session, the exchange silently
// re-establishes it on the node's current record and the send
// lands; if the exchange itself fails, the caller does not send
// on the doubtful proof at all. Background callers (the
// keepalive) keep the freshness shortcut.
async Task<bool> EnsureSessionAsync(bool forceExchange = false)
{
    await sessionGate.WaitAsync();
    try
    {
        var state = LoadJson(StatePath, new JsonObject());
        if (state["joined"]?.GetValue<bool>() != true)
            return false;
        var openNode = state["session_open_at"];
        if (!forceExchange && openNode != null
            && SessionFreshness.IsFresh(openNode.GetValue<long>(),
                DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
            return true;
        var attempts = ReadAttempts(state);
        if (attempts.Count > 0)
        {
            var latestSent = attempts.Max(a => a.SentAt);
            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - latestSent <= 110)
            {
                // An open attempt is already in flight, possibly
                // started by an earlier run of this same assistant
                // that was stopped mid-exchange. Answer its
                // challenge rather than knocking again: a second
                // knock makes the node consume the first
                // challenge while its delivery can still be
                // queued ahead of the new one.
                var ans = await SessionAnswerCoreAsync();
                if (ans.ok)
                    return true;
            }
        }
        var open = await SessionOpenCoreAsync();
        if (!open.ok)
            return false;
        var answer = await SessionAnswerCoreAsync();
        return answer.ok;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"auto-session: ensure failed: {ex.Message}");
        return false;
    }
    finally
    {
        sessionGate.Release();
    }
}


var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// Auto session keepalive. A real always-on assistant keeps its own
// session alive; in the deployed demo this is what lets a judge's
// click hours later still land, through the same real proof
// exchange, never a bypass. Runs once after the app starts and
// then every 20 minutes, only when joined. Fire and forget: a
// failure is logged to the console and never crashes the app.
if (AutoSessionEnabled())
{
    _ = Task.Run(async () =>
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(20));
            while (true)
            {
                try
                {
                    var keepState = LoadJson(StatePath, new JsonObject());
                    if (keepState["joined"]?.GetValue<bool>() == true)
                    {
                        // Anything an earlier hunt held gets its
                        // acknowledging pass at least once a cycle.
                        await DrainHeldAsync();
                        // A failed ensure is retried inside the
                        // cycle, not left for the next one twenty
                        // minutes later: one slow exchange must
                        // cost seconds, not the whole session.
                        for (int attempt = 0; attempt < 3; attempt++)
                        {
                            if (await EnsureSessionAsync()) break;
                            if (attempt < 2) await Task.Delay(TimeSpan.FromSeconds(10));
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"auto-session keepalive: {ex.Message}");
                }
                await timer.WaitForNextTickAsync();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"auto-session keepalive stopped: {ex.Message}");
        }
    });
}


// ------------------------------------------------------------------
// Self-operating join mode (auto join), gated by the
// STANDIN_AUTO_JOIN environment variable together with an invite
// supplied by environment (STANDIN_INVITE_CODE, and optionally
// STANDIN_INVITE_CHECK_PHRASE). This is the fresh deployment
// path: an instance that starts with no joined store writes the
// operator-supplied invite into its own store as a pending
// invite, knocks through its own /act/join-begin, and polls its
// own /act/join-complete until a named inviter has confirmed the
// knock at the node. It is the same real door as any other
// client takes: no bypass, no second code path, and nothing is
// joined until the node says the connection is enrolled. The
// gate defaults off: with it off, nothing in this section runs
// and every driver facing behavior is exactly as before.
bool AutoJoinEnabled() =>
    Environment.GetEnvironmentVariable("STANDIN_AUTO_JOIN") == "1";

if (AutoJoinEnabled())
{
    _ = Task.Run(async () =>
    {
        try
        {
            var inviteCode = Environment.GetEnvironmentVariable("STANDIN_INVITE_CODE");
            if (string.IsNullOrWhiteSpace(inviteCode))
            {
                Console.WriteLine("auto-join: STANDIN_INVITE_CODE is not set; staying unjoined");
                return;
            }
            var state0 = LoadJson(StatePath, new JsonObject());
            if (state0["joined"]?.GetValue<bool>() == true)
                return;
            if (!File.Exists(InvitePath))
            {
                var phrase = Environment.GetEnvironmentVariable("STANDIN_INVITE_CHECK_PHRASE");
                // The pending invite carries the raw invite code,
                // so the file is written owner only, like the key
                // and state files.
                Protocol.WriteKeyFile(InvitePath, new JsonObject
                {
                    ["code"] = inviteCode,
                    ["check_phrase"] = string.IsNullOrWhiteSpace(phrase)
                        ? "issued by the node door" : phrase
                }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
            var selfUrl = Environment.GetEnvironmentVariable("SELF_URL")
                ?? "http://127.0.0.1:5000";
            for (int i = 0; i < 60; i++)
            {
                try
                {
                    var r = await http.GetAsync(selfUrl + "/status");
                    if (r.IsSuccessStatusCode) break;
                }
                catch (HttpRequestException) { }
                await Task.Delay(1000);
            }
            var state1 = LoadJson(StatePath, new JsonObject());
            if (state1["connection_id"] == null)
            {
                // The node may still be starting (a deployed
                // instance cold starts), so the knock retries
                // for a while. A refused or unanswered knock
                // writes nothing, so retrying is safe; the knock
                // itself is the same single real knock either way.
                var knocked = false;
                for (int i = 0; i < 40 && !knocked; i++)
                {
                    try
                    {
                        var resp = await http.PostAsync(selfUrl + "/act/join-begin", null);
                        if (resp.IsSuccessStatusCode)
                        {
                            knocked = true;
                            Console.WriteLine("auto-join: knock placed at the node's door");
                        }
                        else
                        {
                            Console.WriteLine($"auto-join: join-begin answered {(int)resp.StatusCode}; retrying");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"auto-join: join-begin attempt failed: {ex.Message}");
                    }
                    if (!knocked) await Task.Delay(TimeSpan.FromSeconds(15));
                }
                if (!knocked)
                {
                    Console.WriteLine("auto-join: could not knock within the retry window; staying unjoined");
                    return;
                }
            }
            for (int i = 0; i < 180; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(10));
                try
                {
                    var resp = await http.PostAsync(selfUrl + "/act/join-complete", null);
                    var body = await resp.Content.ReadAsStringAsync();
                    var parsed = JsonNode.Parse(body)?.AsObject();
                    if (parsed?["joined"]?.GetValue<bool>() == true)
                    {
                        Console.WriteLine("auto-join: joined through the real door");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"auto-join: join-complete attempt failed: {ex.Message}");
                }
            }
            Console.WriteLine("auto-join: not confirmed within the polling window; staying unjoined");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"auto-join stopped: {ex.Message}");
        }
    });
}

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
        // The node checks the invite code against the invite's
        // stored hash before the invite is spent:
        // the joiner proves the invite itself, not just knowledge
        // that one is outstanding.
        ["invite_code"] = invite["code"]!.GetValue<string>(),
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
    SaveState(state);
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
    SaveState(state);
    return Results.Json(new { joined = true, invite_disposed = true });
});

app.MapPost("/act/session-open", async () =>
{
    var r = await SessionOpenCoreAsync();
    return Results.Json(r.payload, statusCode: r.httpStatus);
});

app.MapPost("/act/session-answer", async () =>
{
    var r = await SessionAnswerCoreAsync();
    return Results.Json(r.payload, statusCode: r.httpStatus);
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
    // In auto session mode the session is re-opened
    // when stale BEFORE sending, and a failed re-open means the
    // telling is not sent at all. Sending it anyway on a stale
    // proof made the node refuse it with "No open session" while
    // this trigger (and the front door reading its answer)
    // reported the telling as sent.
    //
    // Silent session recovery: a presented send in auto
    // session mode confirms its session through the real exchange
    // first (forceExchange), because the recorded proof's age
    // cannot show whether the node still holds the session; after
    // a node reset it does not, and the exchange is what silently
    // re-establishes it on the node's current record. The user
    // never sees a dead-session refusal: they see the send's real
    // result. If the exchange fails, the telling is not sent on
    // the doubtful proof at all (the stale-proof rule above).
    var sessionReady = true;
    if (AutoSessionEnabled())
    {
        sessionReady = await EnsureSessionAsync(forceExchange: true);
        RecordSessionRecovery(sessionReady
            ? "presented send: session confirmed through the exchange before sending"
            : "presented send: the session exchange failed; the telling was not sent on a doubtful proof");
    }
    var actDef = script["acts"]![act]!.AsObject();
    var packetId = NewPacketId();
    var plain = MakePlain("telling",
        new JsonObject { ["kind"] = "raw_telling", ["text"] = actDef["telling"]!.GetValue<string>() },
        NodePartyId, packetId);
    var wire = BuildForNode(plain, "session", packetId);
    var (status, respBody) = sessionReady
        ? await RelaySend(wire)
        : (0, "session could not be reopened");
    // The refused send itself: if the relay refuses this
    // send, the session is re-opened through the real exchange
    // and the SAME packet is retried exactly once. The same
    // packet, never a second one: if the first attempt was taken
    // in and only its answer was lost, the retry meets the
    // relay's duplicate protection (409), which reads as the send
    // standing exactly once. A second refusal falls through to
    // the honest queued outcome below; there is no third attempt
    // and nothing is presented as sent that was not accepted.
    var retried = false;
    var sendAccepted = status >= 200 && status < 300;
    if (AutoSessionEnabled() && sessionReady && !sendAccepted && status != 409)
    {
        RecordSessionRecovery($"presented send: the relay refused the send (status {status}); "
            + "re-opening the session and retrying the same packet once");
        retried = true;
        if (await EnsureSessionAsync(forceExchange: true))
        {
            var retry = await RelaySend(wire);
            status = retry.status;
            respBody = retry.body;
            sendAccepted = (status >= 200 && status < 300) || status == 409;
            RecordSessionRecovery(sendAccepted
                ? $"presented send: the single retry was accepted (status {status}); the telling stands sent exactly once"
                : $"presented send: the single retry was also refused (status {status}); the error surfaces and nothing is presented as sent");
        }
        else
        {
            RecordSessionRecovery("presented send: the recovery exchange failed; the send was not retried");
        }
    }
    var sent = LoadArr(SentPath);
    // Only a relay acceptance (a 2xx answer, or the duplicate
    // backstop on the single recovery retry) is a send.
    // A relay refusal, an unreachable relay, or a session that
    // could not be reopened all land in the same honest place:
    // the telling stays in this stand-in's own outbox, marked
    // unrecorded, and the answer says so.
    if (!sendAccepted)
    {
        QueueLocal(wire, act);
        sent.Add(new JsonObject
        {
            ["act"] = act, ["packet_id"] = packetId,
            ["result"] = "queued locally, unrecorded", ["at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        });
        SaveArr(SentPath, sent);
        return Results.Json(new { act, packet_id = packetId, result = "queued locally, unrecorded", detail = !sessionReady ? "session could not be reopened; the telling was not sent on a stale session" : retried ? "the relay did not accept the packet, even after the one session recovery retry; the telling was not sent" : "the relay did not accept the packet; the telling was not sent" }, statusCode: 202);
    }
    var sentEntry = new JsonObject
    {
        ["act"] = act, ["packet_id"] = packetId,
        ["result"] = respBody, ["at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        ["wire"] = wire.DeepClone()
    };
    if (retried) sentEntry["recovered_by_retry"] = true;
    sent.Add(sentEntry);
    SaveArr(SentPath, sent);
    return Results.Json(new { act, packet_id = packetId, relay_status = status, relay_result = respBody });
});

app.MapPost("/act/poll", async () =>
{
    var state = LoadJson(StatePath, new JsonObject());
    if (state["joined"]?.GetValue<bool>() != true)
        return Results.Json(new { error = "not enrolled" }, statusCode: 409);
    // Packets an earlier session hunt took and held are older
    // than anything the relay still queues: they get their
    // acknowledging pass first.
    await DrainHeldAsync();
    var wires = await RelayPoll();
    if (wires == null)
        return Results.Json(new { error = "relay unreachable or poll refused" }, statusCode: 502);
    int opened = 0, refused = 0, acked = 0;
    foreach (var wire in wires)
    {
        var (o, r, a) = await ProcessDeliveryWireAsync(wire);
        opened += o; refused += r; acked += a;
    }
    return Results.Json(new { polled = wires.Count, opened, refused, acked });
});

app.Run();
