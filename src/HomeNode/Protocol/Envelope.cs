using System.Text.Json;

namespace HomeNode.Protocol;

/// <summary>
/// Coarse envelope class, the only class information a relay may
/// see. Packet type (telling, delivery, acknowledgement) is never
/// an envelope value: it lives inside the sealed body only, so a
/// telling, a question and a confirmation reply inside session
/// traffic are indistinguishable to the relay.
/// </summary>
public enum EnvelopeClass
{
    Knock,
    Session,
    StoreAndForward,
}

public static class EnvelopeClassNames
{
    public static string ToWire(EnvelopeClass c) => c switch
    {
        EnvelopeClass.Knock => "knock",
        EnvelopeClass.Session => "session",
        EnvelopeClass.StoreAndForward => "store_and_forward",
        _ => throw new ProtocolException("Unknown envelope class."),
    };

    public static EnvelopeClass FromWire(string s) => s switch
    {
        "knock" => EnvelopeClass.Knock,
        "session" => EnvelopeClass.Session,
        "store_and_forward" => EnvelopeClass.StoreAndForward,
        _ => throw new ProtocolException("Unknown envelope class: " + s),
    };
}

/// <summary>
/// The relay visible envelope. Its fields are exactly the relay-visible
/// metadata list and nothing else:
///
///   packet_id            unique id of this packet
///   household_endpoint   routing address of the household node
///   connection_id        enrolled connection id or routing pseudonym
///   envelope_class       knock, session or store_and_forward
///   delivery_mode        session or store_and_forward carriage
///   ciphertext_length    byte length of the sealed blob
///   issued_at            timing: unix seconds when the packet was issued
///   ciphertext_hash      base64url SHA-256 of the sealed blob
///
/// There is deliberately no material class field, no packet type
/// field, no sender name field and no subject field. Adding any
/// field to this record is a contract change, not an extension.
/// </summary>
public sealed record Envelope(
    string PacketId,
    string HouseholdEndpoint,
    string ConnectionId,
    EnvelopeClass Class,
    string DeliveryMode,
    long CiphertextLength,
    long IssuedAt,
    string CiphertextHash)
{
    public const string DeliverySession = "session";
    public const string DeliveryStoreAndForward = "store_and_forward";

    public static readonly string[] FieldNames =
    {
        "ciphertext_hash", "ciphertext_length", "connection_id",
        "delivery_mode", "envelope_class", "household_endpoint",
        "issued_at", "packet_id",
    };

    public void Validate()
    {
        RequireId(PacketId, "packet_id");
        RequireId(HouseholdEndpoint, "household_endpoint");
        RequireId(ConnectionId, "connection_id");
        if (DeliveryMode != DeliverySession && DeliveryMode != DeliveryStoreAndForward)
            throw new ProtocolException("delivery_mode must be session or store_and_forward.");
        if (CiphertextLength <= 0 || CiphertextLength > Packet.MaxCiphertextLength)
            throw new ProtocolException("ciphertext_length out of bounds.");
        if (IssuedAt <= 0) throw new ProtocolException("issued_at must be positive unix seconds.");
        byte[] hash = Base64Url.Decode(CiphertextHash);
        if (hash.Length != 32) throw new ProtocolException("ciphertext_hash must decode to 32 bytes.");
    }

    internal static void RequireId(string value, string what)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128)
            throw new ProtocolException(what + " must be 1 to 128 characters.");
        CanonicalJson.ValidateString(value, what);
    }

    public byte[] CanonicalBytes()
    {
        var fields = new List<KeyValuePair<string, JsonElement>>
        {
            new("packet_id", JsonSerializer.SerializeToElement(PacketId)),
            new("household_endpoint", JsonSerializer.SerializeToElement(HouseholdEndpoint)),
            new("connection_id", JsonSerializer.SerializeToElement(ConnectionId)),
            new("envelope_class", JsonSerializer.SerializeToElement(EnvelopeClassNames.ToWire(Class))),
            new("delivery_mode", JsonSerializer.SerializeToElement(DeliveryMode)),
            new("ciphertext_length", JsonSerializer.SerializeToElement(CiphertextLength)),
            new("issued_at", JsonSerializer.SerializeToElement(IssuedAt)),
            new("ciphertext_hash", JsonSerializer.SerializeToElement(CiphertextHash)),
        };
        return CanonicalJson.SerializeObject(fields);
    }

    /// <summary>
    /// Authenticated data for the seal: the canonical form of the
    /// envelope without the ciphertext_hash field. The hash cannot
    /// be inside its own authenticated data (it is computed from
    /// the sealed blob), so it is bound instead by the packet
    /// signature, which covers the full canonical envelope plus
    /// the blob, and by the intake check that recomputes it.
    /// Every other envelope field is bound by the seal itself.
    /// </summary>
    public byte[] AadBytes()
    {
        var fields = new List<KeyValuePair<string, JsonElement>>
        {
            new("packet_id", JsonSerializer.SerializeToElement(PacketId)),
            new("household_endpoint", JsonSerializer.SerializeToElement(HouseholdEndpoint)),
            new("connection_id", JsonSerializer.SerializeToElement(ConnectionId)),
            new("envelope_class", JsonSerializer.SerializeToElement(EnvelopeClassNames.ToWire(Class))),
            new("delivery_mode", JsonSerializer.SerializeToElement(DeliveryMode)),
            new("ciphertext_length", JsonSerializer.SerializeToElement(CiphertextLength)),
            new("issued_at", JsonSerializer.SerializeToElement(IssuedAt)),
        };
        return CanonicalJson.SerializeObject(fields);
    }

    public string ToCanonicalJson() => System.Text.Encoding.UTF8.GetString(CanonicalBytes());

    public static Envelope Parse(string canonicalJson)
    {
        using var doc = JsonDocument.Parse(canonicalJson);
        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new ProtocolException("Envelope must be a JSON object.");
        var names = new List<string>();
        foreach (var p in root.EnumerateObject()) names.Add(p.Name);
        names.Sort(StringComparer.Ordinal);
        if (!names.SequenceEqual(FieldNames))
        {
            throw new ProtocolException(
                "Envelope fields must be exactly the relay-visible list. Got: " + string.Join(",", names));
        }
        var env = new Envelope(
            root.GetProperty("packet_id").GetString() ?? "",
            root.GetProperty("household_endpoint").GetString() ?? "",
            root.GetProperty("connection_id").GetString() ?? "",
            EnvelopeClassNames.FromWire(root.GetProperty("envelope_class").GetString() ?? ""),
            root.GetProperty("delivery_mode").GetString() ?? "",
            root.GetProperty("ciphertext_length").GetInt64(),
            root.GetProperty("issued_at").GetInt64(),
            root.GetProperty("ciphertext_hash").GetString() ?? "");
        env.Validate();
        // The parsed form must re-serialize to the identical bytes,
        // or the bytes that were signed are not the bytes we hold.
        if (!env.CanonicalBytes().SequenceEqual(System.Text.Encoding.UTF8.GetBytes(canonicalJson)))
            throw new ProtocolException("Envelope is not in canonical form.");
        return env;
    }
}
