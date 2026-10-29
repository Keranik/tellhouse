using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TellhouseBackup;

// AES-256-GCM sealing. Every seal uses a fresh random nonce, so sealing
// the same record twice yields different ciphertext.
public static class Crypto
{
    public sealed record SealedBlob(string Nonce, string Tag, string Ciphertext);

    public static SealedBlob Seal(byte[] key, byte[] plaintext)
    {
        if (key.Length != 32) throw new InvalidOperationException("sealing keys are 32 bytes");
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plaintext.Length];
        var tag = new byte[16];
        using var gcm = new AesGcm(key, 16);
        gcm.Encrypt(nonce, plaintext, cipher, tag);
        return new SealedBlob(Convert.ToBase64String(nonce), Convert.ToBase64String(tag), Convert.ToBase64String(cipher));
    }

    // Throws CryptographicException on a wrong key or on tampered bytes.
    // Callers treat any throw as a complete failure: nothing is opened.
    public static byte[] Open(byte[] key, SealedBlob blob)
    {
        if (key.Length != 32) throw new InvalidOperationException("sealing keys are 32 bytes");
        var nonce = Convert.FromBase64String(blob.Nonce);
        var tag = Convert.FromBase64String(blob.Tag);
        var cipher = Convert.FromBase64String(blob.Ciphertext);
        var plain = new byte[cipher.Length];
        using var gcm = new AesGcm(key, 16);
        gcm.Decrypt(nonce, cipher, tag, plain);
        return plain;
    }

    public static string Sha256Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    public static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);
    public static string FromUtf8(byte[] b) => Encoding.UTF8.GetString(b);
}

// The household content key, held in the node keystore file. The keystore
// lives beside the node, never inside the database and never inside a
// backup. Reconciliation note: the protocol designs the household
// content key as a keypair on the node. This tool models the backup
// sealing half as a symmetric 32 byte key in the node keystore, because
// a backup is sealed data, not a packet to a reader. This keystore
// entry maps to the protocol's household content key identifier;
// the backup format carries a keyId so that mapping is a rename,
// not a format change.
public sealed class ContentKeyStore
{
    public string KeyId { get; set; } = "";
    public string KeyBase64 { get; set; } = "";

    public byte[] Key => Convert.FromBase64String(KeyBase64);

    public static ContentKeyStore LoadOrCreate(string path)
    {
        if (File.Exists(path))
        {
            var ks = JsonSerializer.Deserialize<ContentKeyStore>(File.ReadAllText(path))
                     ?? throw new InvalidOperationException("keystore file is unreadable");
            if (ks.Key.Length != 32) throw new InvalidOperationException("keystore key must be 32 bytes");
            return ks;
        }
        var fresh = new ContentKeyStore
        {
            KeyId = "household-content-" + Guid.NewGuid().ToString("N")[..12],
            KeyBase64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(fresh, new JsonSerializerOptions { WriteIndented = true }));
        return fresh;
    }

    public static ContentKeyStore Load(string path)
    {
        if (!File.Exists(path)) throw new InvalidOperationException("no household content key at " + path);
        return JsonSerializer.Deserialize<ContentKeyStore>(File.ReadAllText(path))
               ?? throw new InvalidOperationException("keystore file is unreadable");
    }
}
