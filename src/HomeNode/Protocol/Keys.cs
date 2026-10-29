using System.Security.Cryptography;

namespace HomeNode.Protocol;

/// <summary>
/// One identity keypair per party, on NIST P-256. The same keypair
/// is used for ECDH sealing and for ECDSA signing; the contract
/// states this plainly as a demo scale simplification with domain
/// separation carried by the signature and key derivation labels
/// (CONTRACT.md sections 4 and 5), not by separate keys.
///
/// Public key encoding everywhere on the wire and in the vectors:
/// the uncompressed SEC 1 point, 65 bytes, 0x04 || X (32) || Y (32),
/// base64url encoded.
/// Private key encoding (local stores and test vectors only, never
/// on the wire): the 32 byte scalar, base64url encoded.
/// </summary>
public sealed class IdentityKeypair : IDisposable
{
    private readonly byte[] _privateScalar;
    private readonly byte[] _publicPoint;

    private IdentityKeypair(byte[] privateScalar, byte[] publicPoint)
    {
        _privateScalar = privateScalar;
        _publicPoint = publicPoint;
    }

    public string PrivateKeyBase64Url => Base64Url.Encode(_privateScalar);
    public string PublicKeyBase64Url => Base64Url.Encode(_publicPoint);
    public byte[] PublicKeyBytes => (byte[])_publicPoint.Clone();

    /// <summary>
    /// Public key fingerprint: lowercase hex SHA-256 of the 65 byte
    /// public point. This is the fingerprint the door ceremony
    /// stores on the node (CONTRACT.md section 7).
    /// </summary>
    public string Fingerprint =>
        Convert.ToHexString(SHA256.HashData(_publicPoint)).ToLowerInvariant();

    public static IdentityKeypair Generate()
    {
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        ECParameters p = ecdh.ExportParameters(true);
        return new IdentityKeypair(p.D!, BuildPoint(p.Q));
    }

    /// <summary>
    /// Rebuilds a keypair from exported scalar and point. Used by
    /// local credential stores and by the test vector loader. The
    /// pair is imported once into ECDSA and once into ECDH so a
    /// mismatched scalar and point are rejected here, not later.
    /// </summary>
    public static IdentityKeypair FromExported(string privateScalarBase64Url, string publicPointBase64Url)
    {
        byte[] d = Base64Url.Decode(privateScalarBase64Url);
        byte[] point = Base64Url.Decode(publicPointBase64Url);
        if (d.Length != 32) throw new ProtocolException("Private scalar must be 32 bytes.");
        if (point.Length != 65 || point[0] != 0x04) throw new ProtocolException("Public point must be a 65 byte uncompressed point.");
        var kp = new IdentityKeypair(d, point);
        using var _a = kp.CreateEcdsa();
        using var _b = kp.CreateEcdh();
        return kp;
    }

    public static (byte[] X, byte[] Y) SplitPoint(byte[] point)
    {
        if (point.Length != 65 || point[0] != 0x04) throw new ProtocolException("Public point must be a 65 byte uncompressed point.");
        byte[] x = point.AsSpan(1, 32).ToArray();
        byte[] y = point.AsSpan(33, 32).ToArray();
        return (x, y);
    }

    public static byte[] BuildPoint(ECPoint q)
    {
        if (q.X is null || q.Y is null || q.X.Length != 32 || q.Y.Length != 32)
            throw new ProtocolException("Unexpected P-256 point shape.");
        byte[] point = new byte[65];
        point[0] = 0x04;
        q.X.CopyTo(point, 1);
        q.Y.CopyTo(point, 33);
        return point;
    }

    internal ECParameters Parameters(bool includePrivate)
    {
        var (x, y) = SplitPoint(_publicPoint);
        return new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = x, Y = y },
            D = includePrivate ? (byte[])_privateScalar.Clone() : null,
        };
    }

    internal ECDsa CreateEcdsa() => ECDsa.Create(Parameters(true));

    internal ECDiffieHellman CreateEcdh() => ECDiffieHellman.Create(Parameters(true));

    internal static ECDiffieHellman CreateEcdhForPublic(byte[] publicPoint)
    {
        var (x, y) = SplitPoint(publicPoint);
        return ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = x, Y = y },
        });
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_privateScalar);
    }
}
