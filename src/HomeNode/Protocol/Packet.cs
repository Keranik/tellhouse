using System.Security.Cryptography;
using System.Text;

namespace HomeNode.Protocol;

/// <summary>
/// A wire packet: envelope, sealed blob and per connection
/// signature, carried as one canonical JSON object with exactly
/// three keys: ciphertext, envelope, signature.
///
/// The relay visible metadata is the envelope only, exactly the
/// list (see Envelope). The ciphertext is opaque bytes whose
/// length and hash the envelope already states. The signature is
/// authentication material for the endpoints: ECDSA P-256 over
/// SHA-256, IEEE P1363 form (r concatenated with s, 64 bytes),
/// over the byte string:
///
///   ASCII "tellhouse/sig/v1" || 0x0A || canonical envelope bytes
///   || 0x0A || sealed blob bytes
///
/// The relay forwards the signature without interpreting it and
/// gains no content information from it. Signing keys are the
/// enrolled connection keys: a member signs with the member
/// keypair, the node signs with the household content keypair.
/// </summary>
public sealed record Packet(Envelope Envelope, string CiphertextBase64Url, string SignatureBase64Url)
{
    public const int MaxCiphertextLength = 65536;
    private static readonly byte[] SigLabel = "tellhouse/sig/v1"u8.ToArray();

    public byte[] CiphertextBytes() => Base64Url.Decode(CiphertextBase64Url);

    public static byte[] SigningBytes(byte[] envelopeCanonicalBytes, byte[] blob)
    {
        byte[] bytes = new byte[SigLabel.Length + 1 + envelopeCanonicalBytes.Length + 1 + blob.Length];
        SigLabel.CopyTo(bytes, 0);
        bytes[SigLabel.Length] = 0x0A;
        envelopeCanonicalBytes.CopyTo(bytes, SigLabel.Length + 1);
        bytes[SigLabel.Length + 1 + envelopeCanonicalBytes.Length] = 0x0A;
        blob.CopyTo(bytes, SigLabel.Length + 1 + envelopeCanonicalBytes.Length + 1);
        return bytes;
    }

    /// <summary>
    /// Builds a signed packet for a plain packet. The caller picks
    /// the envelope values except length and hash, which are always
    /// computed from the sealed blob here so they cannot drift.
    /// sealEphemeral and sealNonce exist for test vectors only.
    /// </summary>
    public static Packet Create(
        PlainPacket plain,
        string householdEndpoint,
        EnvelopeClass envelopeClass,
        string deliveryMode,
        long issuedAt,
        string recipientPublicKeyBase64Url,
        IdentityKeypair senderSigningKey,
        IdentityKeypair? sealEphemeral = null,
        byte[]? sealNonce = null)
    {
        byte[] plainBytes = plain.CanonicalBytes();
        // Blob length is fixed by the plaintext length, so the
        // envelope is fully known before sealing except its hash.
        // The seal authenticated data excludes the hash field (see
        // Envelope.AadBytes), so one seal suffices.
        long blobLength = Sealer.EphemeralPointLength + Sealer.NonceLength + plainBytes.Length + Sealer.TagLength;
        var probe = new Envelope(
            plain.PacketId, householdEndpoint, plain.ConnectionId, envelopeClass,
            deliveryMode, blobLength, issuedAt, Base64Url.Encode(new byte[32]));
        var eph = sealEphemeral ?? IdentityKeypair.Generate();
        byte[] nonce = sealNonce ?? RandomNumberGenerator.GetBytes(Sealer.NonceLength);
        byte[] blob = Sealer.Seal(plainBytes, probe.AadBytes(), recipientPublicKeyBase64Url, eph, nonce);
        var envelope = new Envelope(
            plain.PacketId, householdEndpoint, plain.ConnectionId, envelopeClass,
            deliveryMode, blob.Length, issuedAt,
            Base64Url.Encode(SHA256.HashData(blob)));
        envelope.Validate();
        plain.ValidateAgainst(envelope);
        string signature = Sign(envelope.CanonicalBytes(), blob, senderSigningKey);
        return new Packet(envelope, Base64Url.Encode(blob), signature);
    }

    public static string Sign(byte[] envelopeCanonicalBytes, byte[] blob, IdentityKeypair signingKey)
    {
        using var ecdsa = signingKey.CreateEcdsa();
        byte[] sig = ecdsa.SignData(SigningBytes(envelopeCanonicalBytes, blob),
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return Base64Url.Encode(sig);
    }

    public bool VerifySignature(string signerPublicKeyBase64Url)
    {
        byte[] point = Base64Url.Decode(signerPublicKeyBase64Url);
        var (x, y) = IdentityKeypair.SplitPoint(point);
        using var ecdsa = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = x, Y = y },
        });
        byte[] sig = Base64Url.Decode(SignatureBase64Url);
        if (sig.Length != 64) return false;
        return ecdsa.VerifyData(SigningBytes(Envelope.CanonicalBytes(), CiphertextBytes()),
            sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    /// <summary>
    /// Full endpoint intake check before opening: envelope shape,
    /// hash and length agreement, then signature. Any failure is a
    /// rejection with no event created.
    /// </summary>
    public void VerifyEnvelopeIntegrityAndSignature(string signerPublicKeyBase64Url)
    {
        Envelope.Validate();
        byte[] blob = CiphertextBytes();
        if (blob.Length != Envelope.CiphertextLength)
            throw new ProtocolException("Ciphertext length does not match the envelope.");
        string hash = Base64Url.Encode(SHA256.HashData(blob));
        if (hash != Envelope.CiphertextHash)
            throw new ProtocolException("Ciphertext hash does not match the envelope.");
        if (!VerifySignature(signerPublicKeyBase64Url))
            throw new ProtocolException("Packet signature does not verify under the enrolled connection key.");
    }

    public PlainPacket Open(IdentityKeypair recipientKey)
    {
        byte[] plainBytes = Sealer.Open(CiphertextBytes(), Envelope.AadBytes(), recipientKey);
        var plain = PlainPacket.Parse(plainBytes);
        plain.ValidateAgainst(Envelope);
        return plain;
    }

    public string ToCanonicalJson()
    {
        var fields = new List<KeyValuePair<string, System.Text.Json.JsonElement>>
        {
            new("ciphertext", System.Text.Json.JsonSerializer.SerializeToElement(CiphertextBase64Url)),
            new("envelope", System.Text.Json.JsonSerializer.SerializeToElement(
                System.Text.Json.JsonDocument.Parse(Envelope.ToCanonicalJson()).RootElement.Clone())),
            new("signature", System.Text.Json.JsonSerializer.SerializeToElement(SignatureBase64Url)),
        };
        return Encoding.UTF8.GetString(CanonicalJson.SerializeObject(fields));
    }

    public static Packet Parse(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        var names = new List<string>();
        foreach (var p in root.EnumerateObject()) names.Add(p.Name);
        names.Sort(StringComparer.Ordinal);
        if (!names.SequenceEqual(new[] { "ciphertext", "envelope", "signature" }))
            throw new ProtocolException("Wire packet must have exactly the keys ciphertext, envelope, signature.");
        var envelope = Envelope.Parse(root.GetProperty("envelope").GetRawText());
        string ciphertext = root.GetProperty("ciphertext").GetString() ?? "";
        string signature = root.GetProperty("signature").GetString() ?? "";
        if (Base64Url.Decode(ciphertext).Length == 0) throw new ProtocolException("Empty ciphertext.");
        if (Base64Url.Decode(signature).Length != 64) throw new ProtocolException("Signature must decode to 64 bytes.");
        return new Packet(envelope, ciphertext, signature);
    }
}
