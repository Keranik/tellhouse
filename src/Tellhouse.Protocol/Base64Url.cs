using System.Text;

namespace Tellhouse.Protocol;

/// <summary>
/// Base64url encoding (RFC 4648 section 5) without padding.
/// Every binary value on the wire and in the test vectors uses this
/// encoding and no other, so implementations in any language agree
/// byte for byte.
/// </summary>
public static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> data)
    {
        string s = Convert.ToBase64String(data);
        return s.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static byte[] Decode(string text)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        foreach (char c in text)
        {
            bool ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                      (c >= '0' && c <= '9') || c == '-' || c == '_';
            if (!ok) throw new FormatException("Invalid base64url character: " + c);
        }
        string b64 = text.Replace('-', '+').Replace('_', '/');
        switch (b64.Length % 4)
        {
            case 0: break;
            case 2: b64 += "=="; break;
            case 3: b64 += "="; break;
            default: throw new FormatException("Invalid base64url length.");
        }
        return Convert.FromBase64String(b64);
    }

    public static string EncodeText(string asciiText)
    {
        return Encode(Encoding.ASCII.GetBytes(asciiText));
    }
}
