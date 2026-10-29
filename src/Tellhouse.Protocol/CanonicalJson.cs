using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Tellhouse.Protocol;

/// <summary>
/// Canonical serialization, the single byte form every signature,
/// hash and seal in this protocol is computed over.
///
/// Rules (CONTRACT.md section 2):
/// 1. UTF-8 JSON, no whitespace anywhere.
/// 2. Object keys sorted by ordinal (byte wise) string comparison,
///    recursively, at every depth.
/// 3. Arrays keep their order.
/// 4. String values are written as raw UTF-8 with exactly three
///    escapes: double quote as backslash quote, backslash as double
///    backslash, and any control character below 0x20 as a six
///    character unicode escape with lowercase hex. Shortcut escapes
///    (backslash n, backslash t and friends) are never used. Object
///    keys are stricter: printable ASCII only, never a double quote
///    or a backslash, enforced by ValidateString.
/// 5. Numbers are integers in the range of a signed 64 bit value,
///    written in base 10 with no leading zeros and no fraction or
///    exponent. Booleans are true or false. Null is null.
/// Any input that cannot be written under these rules throws, it is
/// never silently normalized.
/// </summary>
public static class CanonicalJson
{
    public static byte[] Serialize(JsonElement value)
    {
        var sb = new StringBuilder();
        Write(value, sb);
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    public static byte[] SerializeObject(IEnumerable<KeyValuePair<string, JsonElement>> fields)
    {
        using var doc = JsonDocument.Parse(BuildObjectJson(fields));
        return Serialize(doc.RootElement);
    }

    private static string BuildObjectJson(IEnumerable<KeyValuePair<string, JsonElement>> fields)
    {
        var sb = new StringBuilder();
        sb.Append('{');
        bool first = true;
        foreach (var kv in fields)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append(JsonSerializer.Serialize(kv.Key));
            sb.Append(':');
            sb.Append(kv.Value.GetRawText());
        }
        sb.Append('}');
        return sb.ToString();
    }

    public static void ValidateString(string value, string what)
    {
        foreach (char c in value)
        {
            if (c < 0x20 || c > 0x7E || c == '"' || c == '\\')
            {
                throw new ProtocolException(
                    "String for " + what + " is outside the canonical ASCII set: " +
                    "only printable ASCII except double quote and backslash is allowed.");
            }
        }
    }

    private static void WriteString(string s, StringBuilder sb)
    {
        sb.Append('"');
        foreach (char c in s)
        {
            if (c == '"') sb.Append("\\\"");
            else if (c == '\\') sb.Append("\\\\");
            else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
            else sb.Append(c);
        }
        sb.Append('"');
    }

    private static void Write(JsonElement value, StringBuilder sb)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var props = new List<JsonProperty>();
                foreach (var p in value.EnumerateObject()) props.Add(p);
                props.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                var seen = new HashSet<string>(StringComparer.Ordinal);
                sb.Append('{');
                bool first = true;
                foreach (var p in props)
                {
                    if (!seen.Add(p.Name))
                        throw new ProtocolException("Duplicate object key in canonical JSON: " + p.Name);
                    ValidateString(p.Name, "object key");
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append('"').Append(p.Name).Append('"').Append(':');
                    Write(p.Value, sb);
                }
                sb.Append('}');
                break;
            }
            case JsonValueKind.Array:
            {
                sb.Append('[');
                bool first = true;
                foreach (var item in value.EnumerateArray())
                {
                    if (!first) sb.Append(',');
                    first = false;
                    Write(item, sb);
                }
                sb.Append(']');
                break;
            }
            case JsonValueKind.String:
            {
                string s = value.GetString() ?? "";
                WriteString(s, sb);
                break;
            }
            case JsonValueKind.Number:
            {
                if (!value.TryGetInt64(out long n))
                    throw new ProtocolException("Canonical JSON numbers must be integers.");
                sb.Append(n.ToString(CultureInfo.InvariantCulture));
                break;
            }
            case JsonValueKind.True: sb.Append("true"); break;
            case JsonValueKind.False: sb.Append("false"); break;
            case JsonValueKind.Null: sb.Append("null"); break;
            default:
                throw new ProtocolException("Unsupported JSON value kind: " + value.ValueKind);
        }
    }
}
