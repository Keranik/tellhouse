using System.Security.Cryptography;

namespace Tellhouse.Protocol;

/// <summary>
/// Sealed blob layout (CONTRACT.md section 5), a single byte string:
///
///   ephemeral public point   65 bytes, uncompressed P-256
///   AES-GCM nonce            12 bytes
///   ciphertext               same length as the plaintext
///   AES-GCM tag              16 bytes
///
/// Key agreement: ECDH between the ephemeral private key and the
/// recipient public key (for openings: the recipient private key
/// and the ephemeral public point). The shared secret is the raw
/// 32 byte X coordinate.
/// Key derivation: HKDF-SHA256, IKM the shared secret, salt the 65
/// ephemeral public point bytes, info the ASCII label
/// "tellhouse/seal/v1", output 32 bytes.
/// Encryption: AES-256-GCM. Additional authenticated data is the
/// canonical envelope without the ciphertext_hash field
/// (Envelope.AadBytes), so a blob cannot be transplanted onto a
/// different envelope; the hash field itself is bound by the
/// packet signature and the intake hash check. A fresh ephemeral keypair and a fresh
/// nonce per packet mean the same plaintext twice seals to
/// different blobs.
/// </summary>
public static class Sealer
{
    public const int EphemeralPointLength = 65;
    public const int NonceLength = 12;
    public const int TagLength = 16;
    private static readonly byte[] Info = "tellhouse/seal/v1"u8.ToArray();

    /// <summary>
    /// Deterministic seal, for test vectors only. Production code
    /// calls the overload without ephemeral and nonce arguments.
    /// </summary>
    public static byte[] Seal(
        byte[] plainBytes,
        byte[] envelopeCanonicalBytes,
        string recipientPublicKeyBase64Url,
        IdentityKeypair ephemeral,
        byte[] nonce)
    {
        if (nonce.Length != NonceLength) throw new ProtocolException("Seal nonce must be 12 bytes.");
        byte[] recipientPoint = Base64Url.Decode(recipientPublicKeyBase64Url);
        using var eph = ephemeral.CreateEcdh();
        using var recip = IdentityKeypair.CreateEcdhForPublic(recipientPoint);
        byte[] shared = eph.DeriveRawSecretAgreement(recip.PublicKey);
        byte[] key = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, ephemeral.PublicKeyBytes, Info);
        byte[] cipher = new byte[plainBytes.Length];
        byte[] tag = new byte[TagLength];
        using (var gcm = new AesGcm(key, TagLength))
        {
            gcm.Encrypt(nonce, plainBytes, cipher, tag, envelopeCanonicalBytes);
        }
        CryptographicOperations.ZeroMemory(key);
        CryptographicOperations.ZeroMemory(shared);
        byte[] blob = new byte[EphemeralPointLength + NonceLength + cipher.Length + TagLength];
        ephemeral.PublicKeyBytes.CopyTo(blob, 0);
        nonce.CopyTo(blob, EphemeralPointLength);
        cipher.CopyTo(blob, EphemeralPointLength + NonceLength);
        tag.CopyTo(blob, blob.Length - TagLength);
        return blob;
    }

    public static byte[] Seal(
        byte[] plainBytes,
        byte[] envelopeCanonicalBytes,
        string recipientPublicKeyBase64Url)
    {
        using var ephemeral = IdentityKeypair.Generate();
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceLength);
        return Seal(plainBytes, envelopeCanonicalBytes, recipientPublicKeyBase64Url, ephemeral, nonce);
    }

    public static byte[] Open(
        byte[] blob,
        byte[] envelopeCanonicalBytes,
        IdentityKeypair recipient)
    {
        if (blob.Length < EphemeralPointLength + NonceLength + TagLength)
            throw new ProtocolException("Sealed blob is too short.");
        byte[] ephPoint = blob.AsSpan(0, EphemeralPointLength).ToArray();
        byte[] nonce = blob.AsSpan(EphemeralPointLength, NonceLength).ToArray();
        int cipherLength = blob.Length - EphemeralPointLength - NonceLength - TagLength;
        byte[] cipher = blob.AsSpan(EphemeralPointLength + NonceLength, cipherLength).ToArray();
        byte[] tag = blob.AsSpan(blob.Length - TagLength, TagLength).ToArray();
        using var mine = recipient.CreateEcdh();
        using var eph = IdentityKeypair.CreateEcdhForPublic(ephPoint);
        byte[] shared;
        try
        {
            shared = mine.DeriveRawSecretAgreement(eph.PublicKey);
        }
        catch (CryptographicException ex)
        {
            throw new ProtocolException("Ephemeral point is not usable: " + ex.Message);
        }
        byte[] key = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, ephPoint, Info);
        byte[] plain = new byte[cipherLength];
        try
        {
            using var gcm = new AesGcm(key, TagLength);
            gcm.Decrypt(nonce, cipher, tag, plain, envelopeCanonicalBytes);
        }
        catch (CryptographicException)
        {
            throw new ProtocolException("Sealed packet failed to open: wrong key, wrong envelope, or tampered bytes.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(shared);
        }
        return plain;
    }
}
