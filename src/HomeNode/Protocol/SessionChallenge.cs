using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HomeNode.Protocol;

/// <summary>
/// Session open challenge (CONTRACT.md section 7). This is the
/// demo substitute for a challenge per packet, disclosed as such:
///
/// 1. The member sends a knock class telling shaped packet whose
///    body kind is session_open and whose text is the client nonce
///    (base64url 16 bytes). It is sealed to the node key and signed
///    with the member key, like every packet.
/// 2. The node replies with a delivery packet, sealed to the
///    member key, whose body kind is session_challenge and whose
///    text is the server nonce (base64url 16 bytes), with an
///    expiry in the proof object.
/// 3. The member answers with an acknowledgement packet whose
///    proof object carries a signature, under the member signing
///    key, over the challenge transcript:
///
///      ASCII "tellhouse/session/v1" || 0x0A || connection_id
///      || 0x0A || client_nonce bytes || 0x0A || server_nonce bytes
///
/// 4. The node verifies the proof signature, the expiry, and that
///    both nonces match the exchange it started. Only then is the
///    session open and a first session class packet accepted.
///
/// Nonces are single use. A node keeps one outstanding challenge
/// per connection and discards it on use or on expiry.
/// </summary>
public static class SessionChallenge
{
    public const string OpenKind = "session_open";
    public const string ChallengeKind = "session_challenge";
    public const long ChallengeLifetimeSeconds = 120;

    private static readonly byte[] Label = "tellhouse/session/v1"u8.ToArray();

    public static byte[] TranscriptBytes(string connectionId, byte[] clientNonce, byte[] serverNonce)
    {
        CanonicalJson.ValidateString(connectionId, "connection_id");
        byte[] conn = Encoding.ASCII.GetBytes(connectionId);
        byte[] bytes = new byte[Label.Length + 1 + conn.Length + 1 + clientNonce.Length + 1 + serverNonce.Length];
        Label.CopyTo(bytes, 0);
        int i = Label.Length;
        bytes[i++] = 0x0A;
        conn.CopyTo(bytes, i); i += conn.Length;
        bytes[i++] = 0x0A;
        clientNonce.CopyTo(bytes, i); i += clientNonce.Length;
        bytes[i++] = 0x0A;
        serverNonce.CopyTo(bytes, i);
        return bytes;
    }

    public static string NewNonce() => Base64Url.Encode(RandomNumberGenerator.GetBytes(16));

    public static JsonElement BuildProof(string connectionId, string clientNonceB64, string serverNonceB64, IdentityKeypair memberKey)
    {
        byte[] clientNonce = Base64Url.Decode(clientNonceB64);
        byte[] serverNonce = Base64Url.Decode(serverNonceB64);
        if (clientNonce.Length != 16 || serverNonce.Length != 16)
            throw new ProtocolException("Session nonces must be 16 bytes.");
        using var ecdsa = memberKey.CreateEcdsa();
        byte[] sig = ecdsa.SignData(TranscriptBytes(connectionId, clientNonce, serverNonce),
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        string json = "{\"client_nonce\":" + JsonSerializer.Serialize(clientNonceB64) +
                      ",\"server_nonce\":" + JsonSerializer.Serialize(serverNonceB64) +
                      ",\"proof_signature\":" + JsonSerializer.Serialize(Base64Url.Encode(sig)) + "}";
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    public static void VerifyProof(
        JsonElement proof,
        string connectionId,
        string expectedClientNonceB64,
        string expectedServerNonceB64,
        string memberPublicKeyBase64Url)
    {
        string clientNonceB64 = proof.GetProperty("client_nonce").GetString() ?? "";
        string serverNonceB64 = proof.GetProperty("server_nonce").GetString() ?? "";
        string sigB64 = proof.GetProperty("proof_signature").GetString() ?? "";
        if (clientNonceB64 != expectedClientNonceB64)
            throw new ProtocolException("Session proof client nonce does not match the open request.");
        if (serverNonceB64 != expectedServerNonceB64)
            throw new ProtocolException("Session proof server nonce does not match the challenge.");
        byte[] point = Base64Url.Decode(memberPublicKeyBase64Url);
        var (x, y) = IdentityKeypair.SplitPoint(point);
        using var ecdsa = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = x, Y = y },
        });
        byte[] sig = Base64Url.Decode(sigB64);
        if (sig.Length != 64) throw new ProtocolException("Session proof signature must be 64 bytes.");
        bool ok = ecdsa.VerifyData(
            TranscriptBytes(connectionId, Base64Url.Decode(clientNonceB64), Base64Url.Decode(serverNonceB64)),
            sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        if (!ok) throw new ProtocolException("Session proof signature does not verify.");
    }
}
