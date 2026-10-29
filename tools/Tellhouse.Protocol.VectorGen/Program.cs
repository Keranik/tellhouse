using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tellhouse.Protocol;

// Test vector generator for the protocol. Deterministic in every field except
// the ECDSA signatures, which are randomized by design in .NET; the
// vectors mark signatures as verification only, meaning a consumer
// must verify them and must not compare them byte for byte. Every
// other byte (key material, canonical forms, sealed blobs, hashes,
// session transcript) must reproduce exactly.
//
// Usage: dotnet run --project tools/Tellhouse.Protocol.VectorGen -- <output path>
// Default output: src/Tellhouse.Protocol/vectors/test-vectors.json
// relative to the current directory's repo root search.

string outputPath = args.Length > 0 ? args[0] : FindDefaultOutput();
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

// Fixed test key material. TEST ONLY. These private keys exist so
// anyone can reproduce the vectors; they protect nothing and
// must never be used for a real household.
var node = IdentityKeypair.FromExported(
    "NnT7WsF0tYeM290P7B2HzhoA3-aX_iSGa7t87bvkGL4",
    "BOK8r-B0z-tXUwimt_G6cuioaI1pxMjtEYiHvmh6ZSUPTcS5jDTLPisZZQLs5UHL439_jWTLa0Ld1KyR1ZB3XJQ");
var lisa = IdentityKeypair.FromExported(
    "bABx451A95BIl5heaSyB2tMWJrqkE1Ad3oktWZ7VCfo",
    "BEXZq5CscZ644XbKQ2meN3dceRvgTxyfVMNm7zALAul9kdI22stjIwRVWkaDJps1IwwsLaq3fza0bXa9miQcxno");
var daniel = IdentityKeypair.FromExported(
    "_BiNIvZmyhZsIGtMKO7Patbco3Fhj4B_SqHe4aDzJoI",
    "BJRTyrbuMz4NovPQABYryhdctd24AlyZNmEpxiYqHo45FssnCn6Ept4ZyTQxYMJv8rxbxhFTv-WneeM5K0E9Sek");
var ephTelling = IdentityKeypair.FromExported(
    "WO0NZLuQpZyasZ2uIr9DfjlfvphRml2mO52Lx6lIUeE",
    "BFsECVaVkskh0965Iam2DWbF9a1ghrBXDQAKc-3jIILD4jof98iYSedFH-urWlM9158hDzXPlJZwPJQB5YdjloY");
var ephDelivery = IdentityKeypair.FromExported(
    "ApRRv9HQ7-B_fVyJ4Iyr4jHFFZdihpvyCqvqiUvHEk8",
    "BIdkreQkO7SBI0FKajIlDN8Uzin1jvDJ0hFdlSlPL-Dg1j_36SjQ6V6itH7BKJJ4ODE5ddwlu6Ya2Z7dyawky6g");
var ephAck = IdentityKeypair.FromExported(
    "6wdGr2AhjQ2afikVzIEOzEW0FXHE1u8dL9udFkG5-E4",
    "BOOvrGFCWGnZwaKtnCICNGH1TxsEw23XsS5JAYSCt3-3bLND8U2iLrq1mRHQOnwQlCY6ZRL8mE7xUqnVCsQfmak");
var ephLisaDelivery = IdentityKeypair.FromExported(
    "--GxQQdz36yYugbOc46PynSi4T5FxicDDucXaa-AUsM",
    "BPyxH5Rz-Kx5NOuVur7OO4MfB6uB7lwOoZVgpk5sAYnkfgASdwCP5302ANaJn0JWfHBAvD4xbYOtN_J7CBNZIeg");

const string Endpoint = "hh-demo-01";
const string NodeId = "household-node";
const string LisaId = "member-lisa";
const string DanielId = "member-daniel";
const string LisaConn = "conn-lisa-01";
const string DanielConn = "conn-daniel-01";
const long T0 = 1793275200; // 2026-10-29T12:00:00Z

byte[] Nonce12(int seed) => Enumerable.Range(seed, 12).Select(i => (byte)i).ToArray();
string Nonce16(int seed) => Base64Url.Encode(Enumerable.Range(seed, 16).Select(i => (byte)i).ToArray());

object PacketVector(
    string name, PlainPacket plain, EnvelopeClass cls, string deliveryMode, long issuedAt,
    string recipientPub, IdentityKeypair signer, IdentityKeypair recipient,
    IdentityKeypair ephemeral, int sealNonceSeed, string signerPub)
{
    var packet = Packet.Create(plain, Endpoint, cls, deliveryMode, issuedAt,
        recipientPub, signer, ephemeral, Nonce12(sealNonceSeed));
    // Sanity: the vector must open under the recipient key here, or
    // it must never be written.
    var opened = packet.Open(recipient);
    if (opened.PacketId != plain.PacketId) throw new Exception("Vector self check failed: " + name);
    if (!packet.VerifySignature(signerPub)) throw new Exception("Vector signature self check failed: " + name);
    return new
    {
        name,
        envelope = JsonDocument.Parse(packet.Envelope.ToCanonicalJson()).RootElement,
        envelope_canonical = packet.Envelope.ToCanonicalJson(),
        ciphertext = packet.CiphertextBase64Url,
        ciphertext_sha256_hex = Convert.ToHexString(SHA256.HashData(packet.CiphertextBytes())).ToLowerInvariant(),
        signature = packet.SignatureBase64Url,
        signature_verification_only = true,
        plaintext_canonical = Encoding.UTF8.GetString(plain.CanonicalBytes()),
        packet_canonical = packet.ToCanonicalJson(),
        seal = new
        {
            ephemeral_public_key = ephemeral.PublicKeyBase64Url,
            ephemeral_private_key_test_only = ephemeral.PrivateKeyBase64Url,
            nonce = Base64Url.Encode(Nonce12(sealNonceSeed)),
        },
    };
}

var telling = new PlainPacket(
    PacketType.Telling, "pkt-0001", LisaConn, LisaId, NodeId, T0, Nonce16(0x10),
    PlainPacket.TextBody("raw_telling", "Lisa here. We are out of gauze for the first aid kit."), null);
var deliveryDaniel = new PlainPacket(
    PacketType.Delivery, "pkt-0002", DanielConn, NodeId, DanielId, T0 + 60, Nonce16(0x20),
    PlainPacket.TextBody("delivery", "Daniel, your plan for Saturday is ready."), null);
var ackDaniel = new PlainPacket(
    PacketType.Acknowledgement, "pkt-0003", DanielConn, DanielId, NodeId, T0 + 120, Nonce16(0x30),
    PlainPacket.AckBody("pkt-0002", "confirmed"), null);
var deliveryLisa = new PlainPacket(
    PacketType.Delivery, "pkt-0004", LisaConn, NodeId, LisaId, T0 + 180, Nonce16(0x40),
    PlainPacket.TextBody("delivery", "Lisa, got it. Gauze is on the list."), null);

// Session challenge vector: fixed nonces, proof signed by Daniel.
string clientNonce = Nonce16(0x50);
string serverNonce = Nonce16(0x60);
var proof = SessionChallenge.BuildProof(DanielConn, clientNonce, serverNonce, daniel);
byte[] transcript = SessionChallenge.TranscriptBytes(
    DanielConn, Base64Url.Decode(clientNonce), Base64Url.Decode(serverNonce));

var file = new
{
    contract = "tellhouse-protocol",
    contract_version = "1.0",
    generated_by = "tools/Tellhouse.Protocol.VectorGen",
    notes = new[]
    {
        "Test key material only. Private keys are published here so anyone can reproduce the vectors. They protect nothing and must never protect a real household.",
        "Signatures are verification only: verify them, never compare bytes. Every other byte must reproduce exactly.",
        "The envelope object in each vector is the complete relay visible metadata. There are no other relay visible fields.",
    },
    household = new
    {
        household_endpoint = Endpoint,
        node_party_id = NodeId,
        node_public_key = node.PublicKeyBase64Url,
        node_private_key_test_only = node.PrivateKeyBase64Url,
        node_fingerprint = node.Fingerprint,
    },
    members = new[]
    {
        new { party_id = LisaId, connection_id = LisaConn, public_key = lisa.PublicKeyBase64Url, private_key_test_only = lisa.PrivateKeyBase64Url, fingerprint = lisa.Fingerprint },
        new { party_id = DanielId, connection_id = DanielConn, public_key = daniel.PublicKeyBase64Url, private_key_test_only = daniel.PrivateKeyBase64Url, fingerprint = daniel.Fingerprint },
    },
    vectors = new[]
    {
        PacketVector("telling_lisa_to_node", telling, EnvelopeClass.Session, Envelope.DeliverySession, T0,
            node.PublicKeyBase64Url, lisa, node, ephTelling, 0xA0, lisa.PublicKeyBase64Url),
        PacketVector("delivery_node_to_daniel", deliveryDaniel, EnvelopeClass.Session, Envelope.DeliverySession, T0 + 60,
            daniel.PublicKeyBase64Url, node, daniel, ephDelivery, 0xB0, node.PublicKeyBase64Url),
        PacketVector("acknowledgement_daniel_to_node", ackDaniel, EnvelopeClass.Session, Envelope.DeliverySession, T0 + 120,
            node.PublicKeyBase64Url, daniel, node, ephAck, 0xC0, daniel.PublicKeyBase64Url),
        PacketVector("delivery_node_to_lisa", deliveryLisa, EnvelopeClass.StoreAndForward, Envelope.DeliveryStoreAndForward, T0 + 180,
            lisa.PublicKeyBase64Url, node, lisa, ephLisaDelivery, 0xD0, node.PublicKeyBase64Url),
    },
    session_challenge = new
    {
        connection_id = DanielConn,
        client_nonce = clientNonce,
        server_nonce = serverNonce,
        transcript_hex = Convert.ToHexString(transcript).ToLowerInvariant(),
        proof = proof,
        proof_signature_verification_only = true,
        challenge_lifetime_seconds = SessionChallenge.ChallengeLifetimeSeconds,
    },
};

string json = JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true });
File.WriteAllText(outputPath, json + "\n");
Console.WriteLine("Wrote " + outputPath);

static string FindDefaultOutput()
{
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (dir is not null)
    {
        string candidate = Path.Combine(dir.FullName, "src", "Tellhouse.Protocol", "vectors", "test-vectors.json");
        if (Directory.Exists(Path.Combine(dir.FullName, "src", "Tellhouse.Protocol"))) return candidate;
        dir = dir.Parent;
    }
    return Path.Combine("src", "Tellhouse.Protocol", "vectors", "test-vectors.json");
}
