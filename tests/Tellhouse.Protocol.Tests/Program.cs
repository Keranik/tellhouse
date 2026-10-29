using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tellhouse.Protocol;

// Protocol check suite. A plain console runner, no test framework
// packages, so it restores offline. Every check prints one line;
// the process exit code is 0 only if every check passes.
// Run: dotnet run --project tests/Tellhouse.Protocol.Tests
// It loads the shared vectors from
// src/Tellhouse.Protocol/vectors/test-vectors.json and codes
// against them exactly the way later consumers must: from the file,
// not from the library internals that produced it.

int passed = 0;
int failed = 0;
var failures = new List<string>();

void Check(string name, Action body)
{
    try
    {
        body();
        passed++;
        Console.WriteLine("PASS " + name);
    }
    catch (Exception ex)
    {
        failed++;
        failures.Add(name + ": " + ex.Message);
        Console.WriteLine("FAIL " + name + ": " + ex.Message);
    }
}

void Assert(bool cond, string msg)
{
    if (!cond) throw new Exception("Assertion failed: " + msg);
}

void AssertThrows(Action body, string msg)
{
    try { body(); }
    catch (ProtocolException) { return; }
    catch (Exception ex) { throw new Exception(msg + " (threw " + ex.GetType().Name + " instead of ProtocolException)"); }
    throw new Exception(msg + " (nothing was thrown)");
}

// Locate the vector file by walking up from the working directory
// and from the assembly location.
string FindVectors()
{
    var starts = new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
    foreach (string start in starts)
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null)
        {
            string p = Path.Combine(dir.FullName, "src", "Tellhouse.Protocol", "vectors", "test-vectors.json");
            if (File.Exists(p)) return p;
            dir = dir.Parent;
        }
    }
    throw new Exception("test-vectors.json not found above the working directory or the assembly.");
}

string vectorPath = FindVectors();
using var vdoc = JsonDocument.Parse(File.ReadAllText(vectorPath));
JsonElement vroot = vdoc.RootElement;
JsonElement household = vroot.GetProperty("household");
string endpoint = household.GetProperty("household_endpoint").GetString()!;
string nodeId = household.GetProperty("node_party_id").GetString()!;
var node = IdentityKeypair.FromExported(
    household.GetProperty("node_private_key_test_only").GetString()!,
    household.GetProperty("node_public_key").GetString()!);
JsonElement membersEl = vroot.GetProperty("members");
var lisaEl = membersEl[0];
var danielEl = membersEl[1];
var lisa = IdentityKeypair.FromExported(
    lisaEl.GetProperty("private_key_test_only").GetString()!,
    lisaEl.GetProperty("public_key").GetString()!);
var daniel = IdentityKeypair.FromExported(
    danielEl.GetProperty("private_key_test_only").GetString()!,
    danielEl.GetProperty("public_key").GetString()!);
string lisaConn = lisaEl.GetProperty("connection_id").GetString()!;
string danielConn = danielEl.GetProperty("connection_id").GetString()!;
JsonElement vectors = vroot.GetProperty("vectors");

JsonElement VectorByName(string name)
{
    foreach (var v in vectors.EnumerateArray())
        if (v.GetProperty("name").GetString() == name) return v;
    throw new Exception("Vector missing: " + name);
}

Packet VectorPacket(string name) => Packet.Parse(VectorByName(name).GetProperty("packet_canonical").GetString()!);

// ----------------------------------------------------------------
// Canonical serialization
// ----------------------------------------------------------------

Check("canonical: keys sort recursively and no whitespace appears", () =>
{
    using var doc = JsonDocument.Parse("{\"b\":{\"d\":1,\"c\":2},\"a\":[3,{\"z\":0,\"y\":1}]}");
    string s = Encoding.UTF8.GetString(CanonicalJson.Serialize(doc.RootElement));
    Assert(s == "{\"a\":[3,{\"y\":1,\"z\":0}],\"b\":{\"c\":2,\"d\":1}}", "got " + s);
});

Check("canonical: string escapes are the single defined form", () =>
{
    using var doc = JsonDocument.Parse("{\"t\":\"a\\\"b\\\\c\\nd\"}");
    string s = Encoding.UTF8.GetString(CanonicalJson.Serialize(doc.RootElement));
    Assert(s == "{\"t\":\"a\\\"b\\\\c\\u000ad\"}", "got " + s);
});

Check("canonical: non integer numbers are rejected", () =>
{
    using var doc = JsonDocument.Parse("{\"t\":1.5}");
    AssertThrows(() => CanonicalJson.Serialize(doc.RootElement), "fraction must throw");
});

Check("canonical: duplicate keys are rejected", () =>
{
    using var doc = JsonDocument.Parse("{\"t\":1,\"t\":2}");
    AssertThrows(() => CanonicalJson.Serialize(doc.RootElement), "duplicate key must throw");
});

// ----------------------------------------------------------------
// Envelope: exactly the relay-visible fields
// ----------------------------------------------------------------

Check("envelope: field set is exactly the relay-visible list, no material class anywhere", () =>
{
    foreach (var v in vectors.EnumerateArray())
    {
        string canon = v.GetProperty("envelope_canonical").GetString()!;
        Assert(!canon.Contains("material"), "envelope leaks a material field: " + canon);
        Assert(!canon.Contains("packet_type"), "envelope leaks the packet type: " + canon);
        using var d = JsonDocument.Parse(canon);
        var names = new List<string>();
        foreach (var p in d.RootElement.EnumerateObject()) names.Add(p.Name);
        names.Sort(StringComparer.Ordinal);
        Assert(names.SequenceEqual(Envelope.FieldNames), "field set differs: " + string.Join(",", names));
    }
});

Check("envelope: an added field is rejected at parse", () =>
{
    var v = VectorByName("telling_lisa_to_node");
    string canon = v.GetProperty("envelope_canonical").GetString()!;
    string widened = canon.Replace("\"packet_id\"", "\"material_class\":\"telling\",\"packet_id\"");
    AssertThrows(() => Envelope.Parse(widened), "a material_class field must be rejected");
});

Check("envelope: non canonical byte form is rejected at parse", () =>
{
    var v = VectorByName("telling_lisa_to_node");
    string canon = v.GetProperty("envelope_canonical").GetString()!;
    string spaced = canon.Replace(":", ": ");
    AssertThrows(() => Envelope.Parse(spaced), "non canonical spacing must be rejected");
});

Check("envelope: vector canonical bytes reproduce from the vector envelope object", () =>
{
    foreach (var v in vectors.EnumerateArray())
    {
        var env = Envelope.Parse(v.GetProperty("envelope_canonical").GetString()!);
        Assert(env.ToCanonicalJson() == v.GetProperty("envelope_canonical").GetString()!, "canonical mismatch");
        using var d = JsonDocument.Parse(v.GetProperty("envelope").GetRawText());
        Assert(d.RootElement.GetProperty("ciphertext_hash").GetString() == env.CiphertextHash, "hash field mismatch");
    }
});

// ----------------------------------------------------------------
// Vectors: seal reproduction, open, signatures
// ----------------------------------------------------------------

foreach (string name in new[] { "telling_lisa_to_node", "delivery_node_to_daniel", "acknowledgement_daniel_to_node", "delivery_node_to_lisa" })
{
    Check("vector " + name + ": sealed bytes reproduce exactly from the vector inputs", () =>
    {
        var v = VectorByName(name);
        var packet = VectorPacket(name);
        var recipientPub = packet.Envelope.PacketId switch
        {
            "pkt-0001" => node.PublicKeyBase64Url,
            "pkt-0002" => daniel.PublicKeyBase64Url,
            "pkt-0003" => node.PublicKeyBase64Url,
            "pkt-0004" => lisa.PublicKeyBase64Url,
            _ => throw new Exception("unknown vector packet"),
        };
        var seal = v.GetProperty("seal");
        var eph = IdentityKeypair.FromExported(
            seal.GetProperty("ephemeral_private_key_test_only").GetString()!,
            seal.GetProperty("ephemeral_public_key").GetString()!);
        byte[] nonce = Base64Url.Decode(seal.GetProperty("nonce").GetString()!);
        byte[] plainBytes = Encoding.UTF8.GetBytes(v.GetProperty("plaintext_canonical").GetString()!);
        byte[] blob = Sealer.Seal(plainBytes, packet.Envelope.AadBytes(), recipientPub, eph, nonce);
        Assert(Base64Url.Encode(blob) == v.GetProperty("ciphertext").GetString()!, "ciphertext differs");
        Assert(Convert.ToHexString(SHA256.HashData(blob)).ToLowerInvariant() ==
               v.GetProperty("ciphertext_sha256_hex").GetString()!, "ciphertext hash differs");
        Assert(packet.Envelope.CiphertextHash == Base64Url.Encode(SHA256.HashData(blob)), "envelope hash differs");
        Assert(packet.Envelope.CiphertextLength == blob.Length, "envelope length differs");
    });

    Check("vector " + name + ": opens to the vector plaintext and the signature verifies", () =>
    {
        var v = VectorByName(name);
        var packet = VectorPacket(name);
        string signerPub = packet.Envelope.PacketId switch
        {
            "pkt-0001" => lisa.PublicKeyBase64Url,
            "pkt-0002" => node.PublicKeyBase64Url,
            "pkt-0003" => daniel.PublicKeyBase64Url,
            "pkt-0004" => node.PublicKeyBase64Url,
            _ => throw new Exception("unknown vector packet"),
        };
        var recipient = packet.Envelope.PacketId switch
        {
            "pkt-0001" => node,
            "pkt-0002" => daniel,
            "pkt-0003" => node,
            "pkt-0004" => lisa,
            _ => throw new Exception("unknown vector packet"),
        };
        packet.VerifyEnvelopeIntegrityAndSignature(signerPub);
        var plain = packet.Open(recipient);
        Assert(Encoding.UTF8.GetString(plain.CanonicalBytes()) == v.GetProperty("plaintext_canonical").GetString()!,
            "plaintext differs");
    });
}

// ----------------------------------------------------------------
// Live behavior (fresh keys, production overloads)
// ----------------------------------------------------------------

Check("keys: a generated keypair round trips through its exported form", () =>
{
    using var k = IdentityKeypair.Generate();
    using var k2 = IdentityKeypair.FromExported(k.PrivateKeyBase64Url, k.PublicKeyBase64Url);
    Assert(k2.PublicKeyBase64Url == k.PublicKeyBase64Url, "public key differs");
    Assert(k2.Fingerprint == k.Fingerprint, "fingerprint differs");
    Assert(k.Fingerprint.Length == 64, "fingerprint must be a full SHA-256 hex string");
});

PlainPacket LiveTelling(string id, string nonceSeedText) => new(
    PacketType.Telling, id, lisaConn, "member-lisa", nodeId, 1793275200,
    Base64Url.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(nonceSeedText)).AsSpan(0, 16).ToArray()),
    PlainPacket.TextBody("raw_telling", "We need more dish soap."), null);

Check("seal and open: a fresh telling to the node round trips", () =>
{
    var plain = LiveTelling("pkt-live-1", "nonce-a");
    var packet = Packet.Create(plain, endpoint, EnvelopeClass.Session, Envelope.DeliverySession,
        1793275200, node.PublicKeyBase64Url, lisa);
    packet.VerifyEnvelopeIntegrityAndSignature(lisa.PublicKeyBase64Url);
    var opened = packet.Open(node);
    Assert(opened.Body.GetProperty("text").GetString() == "We need more dish soap.", "text differs");
});

Check("seal: the same plaintext twice seals to different blobs", () =>
{
    var plain = LiveTelling("pkt-live-2", "nonce-b");
    var a = Packet.Create(plain, endpoint, EnvelopeClass.Session, Envelope.DeliverySession,
        1793275200, node.PublicKeyBase64Url, lisa);
    var b = Packet.Create(plain, endpoint, EnvelopeClass.Session, Envelope.DeliverySession,
        1793275200, node.PublicKeyBase64Url, lisa);
    Assert(a.CiphertextBase64Url != b.CiphertextBase64Url, "blobs must differ (fresh ephemeral and nonce)");
});

Check("per reader sealing: a packet sealed to Lisa does not open under Daniel key", () =>
{
    var packet = VectorPacket("delivery_node_to_lisa");
    AssertThrows(() => packet.Open(daniel), "Daniel must not open a packet sealed to Lisa");
});

Check("per reader sealing: a packet sealed to the node does not open under any member key", () =>
{
    var packet = VectorPacket("telling_lisa_to_node");
    AssertThrows(() => packet.Open(lisa), "Lisa must not open a node sealed packet");
    AssertThrows(() => packet.Open(daniel), "Daniel must not open a node sealed packet");
});

Check("per reader sealing: a delivery sealed to Daniel opens for Daniel only", () =>
{
    var packet = VectorPacket("delivery_node_to_daniel");
    var opened = packet.Open(daniel);
    Assert(opened.RecipientId == "member-daniel", "recipient differs");
    AssertThrows(() => packet.Open(lisa), "Lisa must not open a packet sealed to Daniel");
});

Check("tamper: a bit flipped ciphertext is rejected before any plaintext exists", () =>
{
    var packet = VectorPacket("telling_lisa_to_node");
    byte[] blob = packet.CiphertextBytes();
    blob[100] ^= 0x01;
    var tampered = packet with { CiphertextBase64Url = Base64Url.Encode(blob) };
    AssertThrows(() => tampered.VerifyEnvelopeIntegrityAndSignature(lisa.PublicKeyBase64Url),
        "hash mismatch must reject");
    AssertThrows(() => Sealer.Open(blob, packet.Envelope.AadBytes(), node),
        "AES-GCM must reject the flipped bytes");
});

Check("tamper: an envelope edited after signing fails signature verification", () =>
{
    var packet = VectorPacket("telling_lisa_to_node");
    var edited = packet with { Envelope = packet.Envelope with { IssuedAt = packet.Envelope.IssuedAt + 5 } };
    Assert(!edited.VerifySignature(lisa.PublicKeyBase64Url), "edited envelope must not verify");
});

Check("transplant: a blob moved onto a different envelope does not open", () =>
{
    var packet = VectorPacket("telling_lisa_to_node");
    var other = VectorPacket("acknowledgement_daniel_to_node");
    var moved = other with { CiphertextBase64Url = packet.CiphertextBase64Url };
    AssertThrows(() => moved.Open(node), "the seal binds the blob to its envelope, transplant must fail");
});

Check("signature: a packet signed with a non enrolled key does not verify as the member", () =>
{
    var plain = LiveTelling("pkt-live-3", "nonce-c");
    using var stranger = IdentityKeypair.Generate();
    var packet = Packet.Create(plain, endpoint, EnvelopeClass.Session, Envelope.DeliverySession,
        1793275200, node.PublicKeyBase64Url, stranger);
    Assert(!packet.VerifySignature(lisa.PublicKeyBase64Url), "stranger signature must not verify as Lisa");
    AssertThrows(() => packet.VerifyEnvelopeIntegrityAndSignature(lisa.PublicKeyBase64Url),
        "intake must reject a non enrolled signer");
});

Check("signature: an unsigned packet (zero signature) is rejected at intake", () =>
{
    var packet = VectorPacket("telling_lisa_to_node");
    var unsigned = packet with { SignatureBase64Url = Base64Url.Encode(new byte[64]) };
    AssertThrows(() => unsigned.VerifyEnvelopeIntegrityAndSignature(lisa.PublicKeyBase64Url),
        "zero signature must be rejected");
});

Check("replay: the same packet twice is rejected by the seen registry", () =>
{
    var packet = VectorPacket("telling_lisa_to_node");
    var registry = new SeenRegistry();
    var plain = packet.Open(node);
    registry.CheckAndRecord(packet.Envelope, plain);
    Assert(registry.HasSeenPacketId(packet.Envelope.PacketId), "packet id must be recorded");
    AssertThrows(() => registry.CheckAndRecordEnvelope(packet.Envelope), "second copy must be rejected");
});

Check("replay: a fresh packet id carrying a reused nonce is rejected", () =>
{
    var v = VectorByName("telling_lisa_to_node");
    var first = VectorPacket("telling_lisa_to_node");
    var registry = new SeenRegistry();
    registry.CheckAndRecord(first.Envelope, first.Open(node));
    var plainEl = JsonDocument.Parse(v.GetProperty("plaintext_canonical").GetString()!).RootElement;
    string reusedNonce = plainEl.GetProperty("packet_nonce").GetString()!;
    var replay = new PlainPacket(PacketType.Telling, "pkt-replay-1", lisaConn, "member-lisa", nodeId,
        1793275300, reusedNonce, PlainPacket.TextBody("raw_telling", "A different telling, same nonce."), null);
    var packet2 = Packet.Create(replay, endpoint, EnvelopeClass.Session, Envelope.DeliverySession,
        1793275300, node.PublicKeyBase64Url, lisa);
    var opened2 = packet2.Open(node);
    AssertThrows(() => registry.CheckAndRecord(packet2.Envelope, opened2), "reused nonce must be rejected");
});

Check("body shape: structured event fields in a telling body are rejected", () =>
{
    using var doc = JsonDocument.Parse("{\"kind\":\"raw_telling\",\"text\":\"x\",\"event_fields\":{\"when\":\"friday\"}}");
    var plain = new PlainPacket(PacketType.Telling, "pkt-bad-1", lisaConn, "member-lisa", nodeId,
        1793275200, Base64Url.Encode(new byte[16]), doc.RootElement.Clone(), null);
    AssertThrows(() => plain.ValidateAgainst(new Envelope("pkt-bad-1", endpoint, lisaConn,
        EnvelopeClass.Session, Envelope.DeliverySession, 100, 1793275200, Base64Url.Encode(new byte[32]))),
        "structured payload beyond raw text must be rejected");
});

Check("body shape: an acknowledgement with an unknown disposition is rejected", () =>
{
    var plain = new PlainPacket(PacketType.Acknowledgement, "pkt-bad-2", danielConn, "member-daniel", nodeId,
        1793275200, Base64Url.Encode(new byte[16]), PlainPacket.AckBody("pkt-0002", "maybe"), null);
    AssertThrows(() => plain.ValidateAgainst(new Envelope("pkt-bad-2", endpoint, danielConn,
        EnvelopeClass.Session, Envelope.DeliverySession, 100, 1793275200, Base64Url.Encode(new byte[32]))),
        "unknown disposition must be rejected");
});

Check("binding: a body claiming a different packet id than the envelope is rejected on open", () =>
{
    var plain = LiveTelling("pkt-inner", "nonce-d");
    var packet = Packet.Create(plain, endpoint, EnvelopeClass.Session, Envelope.DeliverySession,
        1793275200, node.PublicKeyBase64Url, lisa);
    var mismatched = packet with { Envelope = packet.Envelope with { PacketId = "pkt-outer" } };
    AssertThrows(() => mismatched.Open(node), "envelope and body ids must bind, and the seal AAD must fail first");
});

// ----------------------------------------------------------------
// Session open challenge
// ----------------------------------------------------------------

Check("session: the vector challenge proof verifies and its transcript reproduces", () =>
{
    var sc = vroot.GetProperty("session_challenge");
    string conn = sc.GetProperty("connection_id").GetString()!;
    string clientNonce = sc.GetProperty("client_nonce").GetString()!;
    string serverNonce = sc.GetProperty("server_nonce").GetString()!;
    byte[] transcript = SessionChallenge.TranscriptBytes(conn, Base64Url.Decode(clientNonce), Base64Url.Decode(serverNonce));
    Assert(Convert.ToHexString(transcript).ToLowerInvariant() == sc.GetProperty("transcript_hex").GetString(),
        "transcript differs");
    SessionChallenge.VerifyProof(sc.GetProperty("proof"), conn, clientNonce, serverNonce, daniel.PublicKeyBase64Url);
});

Check("session: a full open exchange runs end to end with sealed packets", () =>
{
    string clientNonce = SessionChallenge.NewNonce();
    // 1. Knock: Daniel asks to open a session, sealed to the node.
    var openPlain = new PlainPacket(PacketType.Telling, "pkt-open-1", danielConn, "member-daniel", nodeId,
        1793275200, Base64Url.Encode(new byte[16]), PlainPacket.TextBody(SessionChallenge.OpenKind, clientNonce), null);
    var knock = Packet.Create(openPlain, endpoint, EnvelopeClass.Knock, Envelope.DeliverySession,
        1793275200, node.PublicKeyBase64Url, daniel);
    knock.VerifyEnvelopeIntegrityAndSignature(daniel.PublicKeyBase64Url);
    var openedKnock = knock.Open(node);
    Assert(openedKnock.Body.GetProperty("kind").GetString() == SessionChallenge.OpenKind, "knock kind differs");
    string seenClientNonce = openedKnock.Body.GetProperty("text").GetString()!;
    Assert(seenClientNonce == clientNonce, "client nonce differs");
    // 2. The node challenges, sealed to Daniel.
    string serverNonce = SessionChallenge.NewNonce();
    var challengePlain = new PlainPacket(PacketType.Delivery, "pkt-open-2", danielConn, nodeId, "member-daniel",
        1793275201, Base64Url.Encode(new byte[16]),
        PlainPacket.TextBody(SessionChallenge.ChallengeKind, serverNonce), null);
    var challenge = Packet.Create(challengePlain, endpoint, EnvelopeClass.Knock, Envelope.DeliverySession,
        1793275201, daniel.PublicKeyBase64Url, node);
    var openedChallenge = challenge.Open(daniel);
    string seenServerNonce = openedChallenge.Body.GetProperty("text").GetString()!;
    // 3. Daniel answers with an acknowledgement carrying the proof.
    var proof = SessionChallenge.BuildProof(danielConn, seenClientNonce, seenServerNonce, daniel);
    var answerPlain = new PlainPacket(PacketType.Acknowledgement, "pkt-open-3", danielConn, "member-daniel", nodeId,
        1793275202, Base64Url.Encode(new byte[16]), PlainPacket.AckBody("pkt-open-2", "confirmed"), proof);
    var answer = Packet.Create(answerPlain, endpoint, EnvelopeClass.Session, Envelope.DeliverySession,
        1793275202, node.PublicKeyBase64Url, daniel);
    answer.VerifyEnvelopeIntegrityAndSignature(daniel.PublicKeyBase64Url);
    var openedAnswer = answer.Open(node);
    Assert(openedAnswer.Proof is not null, "proof missing");
    SessionChallenge.VerifyProof(openedAnswer.Proof!.Value, danielConn, clientNonce, serverNonce,
        daniel.PublicKeyBase64Url);
});

Check("session: a proof over the wrong server nonce is rejected", () =>
{
    string clientNonce = SessionChallenge.NewNonce();
    var proof = SessionChallenge.BuildProof(danielConn, clientNonce, SessionChallenge.NewNonce(), daniel);
    AssertThrows(() => SessionChallenge.VerifyProof(proof, danielConn, clientNonce,
        SessionChallenge.NewNonce(), daniel.PublicKeyBase64Url),
        "nonce mismatch must be rejected");
});

Check("session: a proof signed by the wrong member is rejected", () =>
{
    var sc = vroot.GetProperty("session_challenge");
    AssertThrows(() => SessionChallenge.VerifyProof(
        sc.GetProperty("proof"), danielConn,
        sc.GetProperty("client_nonce").GetString()!, sc.GetProperty("server_nonce").GetString()!,
        lisa.PublicKeyBase64Url), "Daniel proof must not verify under Lisa key");
});

Console.WriteLine();
Console.WriteLine($"Protocol checks: {passed} passed, {failed} failed, {passed + failed} total.");
if (failed > 0)
{
    Console.WriteLine("Failures:");
    foreach (string f in failures) Console.WriteLine("  " + f);
}
return failed == 0 ? 0 : 1;
