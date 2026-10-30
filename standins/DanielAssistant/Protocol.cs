using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DanielAssistant;

// Lisa's own implementation of the Tellhouse Protocol Contract
// version 1.0 (frozen). Written inside this project from
// the contract document and the shared test vectors only. This
// project references no Tellhouse library, no node code, and no
// other stand-in's code.
public static class Protocol
{
    public const string SealInfo = "tellhouse/seal/v1";
    public const string SigLabel = "tellhouse/sig/v1";
    public const string SessionLabel = "tellhouse/session/v1";

    // Base64url, RFC 4648 section 5, no padding (contract section 1).
    public static string B64Url(byte[] b) =>
        Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static byte[] UnB64Url(string s)
    {
        var t = s.Replace('-', '+').Replace('_', '/');
        while (t.Length % 4 != 0) t += "=";
        return Convert.FromBase64String(t);
    }
    public static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    // Key file permissions: keys.json holds this assistant's private key, so it
    // is created owner read/write only (0600) whatever the process
    // umask says, and a key file left readable by an older build is
    // tightened on load, on platforms that carry file modes.
    public static void WriteKeyFile(string path, string contents)
    {
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        using (var stream = new FileStream(path, options))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(contents);
        }
        TightenKeyFile(path);
    }

    public static void TightenKeyFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // Canonical serialization (contract section 2): UTF-8 JSON, no
    // whitespace, object keys sorted ordinal recursively, arrays in
    // order, strings escaped only as quote, backslash, or a six
    // character unicode escape with lowercase hex for control
    // characters, integers in base 10, true, false, null.
    public static string Canonical(JsonNode? node)
    {
        var sb = new StringBuilder();
        WriteCanonical(node, sb);
        return sb.ToString();
    }

    static void WriteCanonical(JsonNode? node, StringBuilder sb)
    {
        if (node == null) { sb.Append("null"); return; }
        if (node is JsonObject obj)
        {
            sb.Append('{');
            bool first = true;
            foreach (var kv in obj.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                if (!first) sb.Append(',');
                first = false;
                WriteString(kv.Key, sb);
                sb.Append(':');
                WriteCanonical(kv.Value, sb);
            }
            sb.Append('}');
            return;
        }
        if (node is JsonArray arr)
        {
            sb.Append('[');
            bool first = true;
            foreach (var item in arr)
            {
                if (!first) sb.Append(',');
                first = false;
                WriteCanonical(item, sb);
            }
            sb.Append(']');
            return;
        }
        if (node is JsonValue val)
        {
            if (val.TryGetValue<string>(out var s)) { WriteString(s, sb); return; }
            if (val.TryGetValue<bool>(out var b)) { sb.Append(b ? "true" : "false"); return; }
            if (val.TryGetValue<long>(out var l)) { sb.Append(l.ToString()); return; }
            if (val.TryGetValue<int>(out var i)) { sb.Append(i.ToString()); return; }
            throw new InvalidOperationException("non-integer number has no canonical form");
        }
        throw new InvalidOperationException("unknown JSON node");
    }

    static void WriteString(string s, StringBuilder sb)
    {
        sb.Append('"');
        foreach (var ch in s)
        {
            if (ch == '"') sb.Append("\\\"");
            else if (ch == '\\') sb.Append("\\\\");
            else if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4"));
            else sb.Append(ch);
        }
        sb.Append('"');
    }

    public static byte[] CanonicalBytes(JsonNode node) => Encoding.UTF8.GetBytes(Canonical(node));

    // One identity keypair per party (contract section 1), used for
    // both ECDH sealing and ECDSA signing, with domain separation
    // carried by the derivation and signature labels.
    public static (string d, string x, string y) NewKeyPair()
    {
        using var e = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var p = e.ExportParameters(true);
        return (B64Url(p.D!), B64Url(p.Q.X!), B64Url(p.Q.Y!));
    }

    public static byte[] PubRaw(string xB64, string yB64)
    {
        var raw = new byte[65];
        raw[0] = 0x04;
        UnB64Url(xB64).CopyTo(raw, 1);
        UnB64Url(yB64).CopyTo(raw, 33);
        return raw;
    }

    public static (string x, string y) XYFromPubRaw(byte[] raw) =>
        (B64Url(raw[1..33]), B64Url(raw[33..65]));

    public static string Fingerprint(string xB64, string yB64) =>
        Hex(SHA256.HashData(PubRaw(xB64, yB64)));

    static ECParameters Params(string d, string x, string y) => new()
    {
        Curve = ECCurve.NamedCurves.nistP256,
        D = d == "" ? null : UnB64Url(d),
        Q = new ECPoint { X = UnB64Url(x), Y = UnB64Url(y) }
    };

    // ECDSA P-256 over SHA-256, IEEE P1363 form (contract section 6).
    public static string Sign(string d, string x, string y, byte[] data)
    {
        using var e = ECDsa.Create();
        e.ImportParameters(Params(d, x, y));
        return B64Url(e.SignData(data, HashAlgorithmName.SHA256));
    }

    public static bool Verify(string x, string y, byte[] data, string sigB64Url)
    {
        try
        {
            using var e = ECDsa.Create();
            e.ImportParameters(Params("", x, y));
            return e.VerifyData(data, UnB64Url(sigB64Url), HashAlgorithmName.SHA256);
        }
        catch { return false; }
    }

    static byte[] DeriveKey(byte[] sharedSecret, byte[] ephemeralPubRaw) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, 32,
            ephemeralPubRaw, Encoding.ASCII.GetBytes(SealInfo));

    // Raw ECDH agreement (contract section 5, step 1): the shared
    // secret is the raw 32 byte X coordinate of the agreement
    // point. The platform ECDiffieHellman DeriveKeyMaterial call
    // does not return that value (measured against the frozen
    // vectors: it returns a further derived material), so the
    // agreement is computed here directly as a P-256 scalar
    // multiplication. Demo scale code; it is not constant time.
    static readonly System.Numerics.BigInteger CurveP =
        System.Numerics.BigInteger.Parse(
            "115792089210356248762697446949407573530086143415290314195533631308867097853951");
    static readonly System.Numerics.BigInteger CurveA = CurveP - 3;

    static System.Numerics.BigInteger Mod(System.Numerics.BigInteger v)
    {
        var r = v % CurveP;
        return r.Sign < 0 ? r + CurveP : r;
    }

    static System.Numerics.BigInteger ModInv(System.Numerics.BigInteger v) =>
        System.Numerics.BigInteger.ModPow(Mod(v), CurveP - 2, CurveP);

    static (System.Numerics.BigInteger x, System.Numerics.BigInteger y)? PointAdd(
        (System.Numerics.BigInteger x, System.Numerics.BigInteger y)? P,
        (System.Numerics.BigInteger x, System.Numerics.BigInteger y)? Q)
    {
        if (P == null) return Q;
        if (Q == null) return P;
        var (x1, y1) = P.Value;
        var (x2, y2) = Q.Value;
        System.Numerics.BigInteger m;
        if (x1 == x2 && Mod(y1 + y2) == 0) return null;
        if (x1 == x2 && y1 == y2)
            m = Mod(Mod(3 * x1 * x1 + CurveA) * ModInv(2 * y1));
        else
            m = Mod(Mod(y2 - y1) * ModInv(x2 - x1));
        var x3 = Mod(m * m - x1 - x2);
        var y3 = Mod(m * (x1 - x3) - y1);
        return (x3, y3);
    }

    static byte[] To32(System.Numerics.BigInteger v)
    {
        var b = v.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (b.Length == 32) return b;
        var out32 = new byte[32];
        Array.Copy(b, 0, out32, 32 - b.Length, b.Length);
        return out32;
    }

    public static byte[] RawAgreement(string privDB64, byte[] pubRaw)
    {
        var d = new System.Numerics.BigInteger(
            UnB64Url(privDB64), isUnsigned: true, isBigEndian: true);
        (System.Numerics.BigInteger x, System.Numerics.BigInteger y)? point =
            (new System.Numerics.BigInteger(pubRaw[1..33], isUnsigned: true, isBigEndian: true),
             new System.Numerics.BigInteger(pubRaw[33..65], isUnsigned: true, isBigEndian: true));
        (System.Numerics.BigInteger x, System.Numerics.BigInteger y)? result = null;
        var k = d;
        var addend = point;
        while (!k.IsZero)
        {
            if (!k.IsEven) result = PointAdd(result, addend);
            addend = PointAdd(addend, addend);
            k >>= 1;
        }
        if (result == null) throw new CryptographicException("agreement at infinity");
        return To32(result.Value.x);
    }

    // Sealing (contract section 5). Blob layout: ephemeral public
    // point (65), nonce (12), ciphertext, tag (16) last. Authenticated
    // data is supplied by the caller (the canonical envelope without
    // the ciphertext_hash field). Fixed ephemeral key and nonce exist
    // only so the shared test vectors can be reproduced byte for
    // byte in the selftest; live packets always use fresh values.
    public static byte[] Seal(byte[] recipientPubRaw, byte[] plain, byte[] aad,
        string? fixedEphD = null, byte[]? fixedEphPubRaw = null, byte[]? fixedNonce = null)
    {
        string ephD;
        byte[] ephPubRaw;
        if (fixedEphD != null && fixedEphPubRaw != null)
        {
            ephD = fixedEphD;
            ephPubRaw = fixedEphPubRaw;
        }
        else
        {
            var (gd, gx, gy) = NewKeyPair();
            ephD = gd;
            ephPubRaw = PubRaw(gx, gy);
        }
        var shared = RawAgreement(ephD, recipientPubRaw);
        var key = DeriveKey(shared, ephPubRaw);
        var nonce = fixedNonce ?? RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var g = new AesGcm(key, 16))
            g.Encrypt(nonce, plain, cipher, tag, aad);
        var blob = new byte[65 + 12 + cipher.Length + 16];
        ephPubRaw.CopyTo(blob, 0);
        nonce.CopyTo(blob, 65);
        cipher.CopyTo(blob, 77);
        tag.CopyTo(blob, 77 + cipher.Length);
        return blob;
    }

    // Opening. Throws on any failure; callers refuse the packet.
    // There is no plaintext fallback in this project.
    public static byte[] Open(string d, string x, string y, byte[] blob, byte[] aad)
    {
        if (blob.Length < 65 + 12 + 16)
            throw new CryptographicException("sealed blob too short");
        var ephRaw = blob[..65];
        var nonce = blob[65..77];
        var cipher = blob[77..^16];
        var tag = blob[^16..];
        var shared = RawAgreement(d, ephRaw);
        var key = DeriveKey(shared, ephRaw);
        var plain = new byte[cipher.Length];
        using var g = new AesGcm(key, 16);
        g.Decrypt(nonce, cipher, tag, plain, aad);
        return plain;
    }

    // Envelope (contract section 3): exactly the eight envelope fields.
    public static JsonObject Envelope(string packetId, string connectionId, string deliveryMode,
        string envelopeClass, string householdEndpoint, long issuedAt, long ciphertextLength,
        string ciphertextHash) => new()
        {
            ["ciphertext_hash"] = ciphertextHash,
            ["ciphertext_length"] = ciphertextLength,
            ["connection_id"] = connectionId,
            ["delivery_mode"] = deliveryMode,
            ["envelope_class"] = envelopeClass,
            ["household_endpoint"] = householdEndpoint,
            ["issued_at"] = issuedAt,
            ["packet_id"] = packetId
        };

    // Authenticated data for sealing: the canonical envelope without
    // the ciphertext_hash field (contract section 5, step 4).
    public static byte[] AadBytes(JsonObject envelope)
    {
        var copy = new JsonObject();
        foreach (var kv in envelope)
            if (kv.Key != "ciphertext_hash")
                copy[kv.Key] = kv.Value?.DeepClone();
        return CanonicalBytes(copy);
    }

    // Packet signature input (contract section 6).
    public static byte[] SignatureInput(JsonObject envelope, byte[] blob)
    {
        var env = CanonicalBytes(envelope);
        var input = new byte[SigLabel.Length + 1 + env.Length + 1 + blob.Length];
        Encoding.ASCII.GetBytes(SigLabel).CopyTo(input, 0);
        input[SigLabel.Length] = 0x0A;
        env.CopyTo(input, SigLabel.Length + 1);
        input[SigLabel.Length + 1 + env.Length] = 0x0A;
        blob.CopyTo(input, SigLabel.Length + 1 + env.Length + 1);
        return input;
    }

    // Build a complete wire packet: exactly ciphertext, envelope,
    // signature (contract section 3).
    public static JsonObject BuildPacket(
        string signD, string signX, string signY,
        byte[] recipientPubRaw, JsonObject plainPacket,
        string connectionId, string deliveryMode, string envelopeClass,
        string householdEndpoint, string packetId, long issuedAt)
    {
        var plainBytes = CanonicalBytes(plainPacket);
        long blobLength = plainBytes.Length + 65 + 12 + 16;
        var envNoHash = Envelope(packetId, connectionId, deliveryMode, envelopeClass,
            householdEndpoint, issuedAt, blobLength, "");
        envNoHash.Remove("ciphertext_hash");
        // AAD is the canonical form of the seven field envelope.
        var aadEnv = Envelope(packetId, connectionId, deliveryMode, envelopeClass,
            householdEndpoint, issuedAt, blobLength, "placeholder");
        var aad = AadBytes(aadEnv);
        var blob = Seal(recipientPubRaw, plainBytes, aad);
        if (blob.Length != blobLength) throw new InvalidOperationException("blob length drift");
        var hash = B64Url(SHA256.HashData(blob));
        var envelope = Envelope(packetId, connectionId, deliveryMode, envelopeClass,
            householdEndpoint, issuedAt, blobLength, hash);
        var signature = Sign(signD, signX, signY, SignatureInput(envelope, blob));
        return new JsonObject
        {
            ["ciphertext"] = B64Url(blob),
            ["envelope"] = envelope,
            ["signature"] = signature
        };
    }

    public static bool VerifyPacket(string signX, string signY, JsonObject wire)
    {
        try
        {
            var envelope = wire["envelope"]!.AsObject();
            var blob = UnB64Url(wire["ciphertext"]!.GetValue<string>());
            if (B64Url(SHA256.HashData(blob)) != envelope["ciphertext_hash"]!.GetValue<string>())
                return false;
            if (blob.Length != envelope["ciphertext_length"]!.GetValue<long>())
                return false;
            return Verify(signX, signY, SignatureInput(envelope, blob),
                wire["signature"]!.GetValue<string>());
        }
        catch { return false; }
    }

    public static byte[] OpenPacket(string d, string x, string y, JsonObject wire)
    {
        var envelope = wire["envelope"]!.AsObject();
        var blob = UnB64Url(wire["ciphertext"]!.GetValue<string>());
        return Open(d, x, y, blob, AadBytes(envelope));
    }

    // Session open transcript (contract section 7, step 3).
    public static byte[] Transcript(string connectionId, byte[] clientNonce, byte[] serverNonce)
    {
        var conn = Encoding.ASCII.GetBytes(connectionId);
        var bytes = new List<byte>();
        bytes.AddRange(Encoding.ASCII.GetBytes(SessionLabel));
        bytes.Add(0x0A);
        bytes.AddRange(conn);
        bytes.Add(0x0A);
        bytes.AddRange(clientNonce);
        bytes.Add(0x0A);
        bytes.AddRange(serverNonce);
        return bytes.ToArray();
    }


    // Local mesh transport. In this sandbox, connections made
    // through dual-mode (IPv4-mapped IPv6) sockets are reset by
    // the network interception layer, while plain IPv4 sockets
    // pass; that was measured directly against local servers.
    // The protocol does not care which socket family carries it,
    // so every HTTP client in this project forces an IPv4 socket
    // and never uses a proxy for mesh traffic.
    public static HttpClient LocalHttp() => new(new SocketsHttpHandler
    {
        UseProxy = false,
        ConnectCallback = async (ctx, ct) =>
        {
            var sock = new System.Net.Sockets.Socket(
                System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Stream,
                System.Net.Sockets.ProtocolType.Tcp);
            await sock.ConnectAsync(ctx.DnsEndPoint.Host, ctx.DnsEndPoint.Port, ct);
            return new System.Net.Sockets.NetworkStream(sock, ownsSocket: true);
        }
    })
    { Timeout = TimeSpan.FromSeconds(20) };

    public static string InviteCredential(string inviteCode, string fingerprint) =>
        Hex(HKDF.DeriveKey(HashAlgorithmName.SHA256,
            Encoding.UTF8.GetBytes(inviteCode), 32,
            Encoding.UTF8.GetBytes("invite:" + fingerprint),
            Encoding.ASCII.GetBytes("tellhouse-door-v1")));
}

// Choosing which session challenge to answer (contract section 7).
//
// The relay poll that hunts for the challenge is destructive: it
// takes every queued packet for this connection. A challenge
// delivery from an earlier, interrupted open attempt can still be
// sitting in that queue (a restarted process, a knock whose answer
// never completed). The node keeps ONE outstanding challenge per
// connection: a new knock consumes the old challenge and issues a
// new one. Answering the stale delivery mixes this attempt's
// client nonce with the old server nonce, the node rightly
// rejects the proof, and the current challenge delivery has
// already been taken by the same poll, so it can never be
// answered at all. The rules below, applied to every challenge
// the hunt sees, keep the answer paired with the challenge the
// node actually holds open:
//
//   a challenge issued before the latest open attempt was sent
//   (allowing two seconds of clock skew) answers a superseded
//   knock and is never answered;
//   a challenge older than 110 seconds is dead (the challenge
//   lifetime is 120 seconds) and is never answered;
//   among the rest, the most recently issued challenge is the
//   node's outstanding one, and it pairs with the open attempt
//   sent most recently before it was issued.
//
// Timestamps are whole seconds, so two knocks sent inside one
// second, and the two challenges they draw, carry identical
// times and cannot be ordered by time at all (this
// tie was the intermittent session
// exchange failure; the proof came out built across the two
// exchanges and the node rejected it). Order, not time, breaks
// the tie: attempts are recorded in the order the knocks were
// sent and candidates in the order the relay delivered them,
// which is the order the node issued them, so among equals the
// LAST attempt and the LAST candidate are the current pair.
public static class SessionHunt
{
    public sealed record Attempt(string ClientNonce, long SentAt);
    public sealed record Candidate(string PacketId, string ServerNonce, long IssuedAt);
    public sealed record Choice(string PacketId, string ServerNonce, string ClientNonce);

    public static bool IsAnswerable(Attempt latestAttempt, Candidate c, long now) =>
        c.IssuedAt >= latestAttempt.SentAt - 2 && now - c.IssuedAt <= 110;

    public static Choice? Choose(IReadOnlyList<Attempt> attempts,
        IReadOnlyList<Candidate> candidates, long now)
    {
        if (attempts.Count == 0) return null;
        // The latest attempt is the last one sent (list order),
        // not the greatest SentAt: on a tie MaxBy returns the
        // FIRST maximal element, which is the superseded knock.
        var latest = attempts[^1];
        Candidate? best = null;
        foreach (var c in candidates)
        {
            if (!IsAnswerable(latest, c, now)) continue;
            // >=, not >: on an IssuedAt tie the later delivered
            // candidate is the later issued one, and it, not the
            // superseded first delivery, is the node's
            // outstanding challenge.
            if (best == null || c.IssuedAt >= best.IssuedAt) best = c;
        }
        if (best == null) return null;
        // The attempt the outstanding challenge answers is the
        // most recent knock sent before it was issued, again in
        // list order so a same-second tie pairs the newest
        // challenge with the newest knock, never across the two.
        Attempt paired = latest;
        foreach (var a in attempts)
        {
            if (a.SentAt <= best.IssuedAt + 2) paired = a;
        }
        return new Choice(best.PacketId, best.ServerNonce, paired.ClientNonce);
    }
}

// Session freshness, the rule EnsureSessionAsync runs on.
// The node accepts session class packets within 30
// minutes of a verified session open proof. This stand-in treats
// a proof as fresh for 20 of those minutes, leaving margin for
// the node's own poll and processing, and re-opens through the
// real knock, challenge, and proof exchange once its recorded
// proof is older than that, or was never recorded at all. A
// trigger that cannot re-open must not send on a stale proof and
// report the telling as sent: the node will refuse it with "No
// open session" and record nothing.
public static class SessionFreshness
{
    public const long FreshWindowSeconds = 20 * 60;

    public static bool IsFresh(long openedAt, long now) =>
        openedAt > 0 && now >= openedAt && now - openedAt < FreshWindowSeconds;

    public static bool IsStale(long openedAt, long now) => !IsFresh(openedAt, now);
}
