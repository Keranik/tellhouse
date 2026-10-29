using System.Text.Json;

namespace Tellhouse.Protocol;

/// <summary>
/// The three packet types. This enum exists only inside the sealed
/// body. It never appears in the envelope (CONTRACT.md section 3).
/// </summary>
public enum PacketType
{
    Telling,
    Delivery,
    Acknowledgement,
}

public static class PacketTypeNames
{
    public static string ToWire(PacketType t) => t switch
    {
        PacketType.Telling => "telling",
        PacketType.Delivery => "delivery",
        PacketType.Acknowledgement => "acknowledgement",
        _ => throw new ProtocolException("Unknown packet type."),
    };

    public static PacketType FromWire(string s) => s switch
    {
        "telling" => PacketType.Telling,
        "delivery" => PacketType.Delivery,
        "acknowledgement" => PacketType.Acknowledgement,
        _ => throw new ProtocolException("Unknown packet type: " + s),
    };
}

/// <summary>
/// The plaintext inside a sealed packet. Serialized canonically.
/// Fields:
///   packet_type    telling, delivery or acknowledgement
///   packet_id      must equal the envelope packet_id
///   connection_id  must equal the envelope connection_id
///   sender_id      enrolled party id of the sender; the node
///                  rejects any body whose sender differs from the
///                  connection enrollment
///   recipient_id   enrolled party id of the recipient
///   told_at        unix seconds the sender told this (told time,
///                  distinct from the time the node records it)
///   packet_nonce   base64url 16 random bytes, single use, the
///                  replay guard together with packet_id
///   body           type specific JSON object, see CONTRACT.md:
///                  telling and delivery carry raw text only;
///                  acknowledgement carries acked_packet_id and a
///                  disposition word
///   proof          optional object for session open responses
/// </summary>
public sealed record PlainPacket(
    PacketType Type,
    string PacketId,
    string ConnectionId,
    string SenderId,
    string RecipientId,
    long ToldAt,
    string PacketNonce,
    JsonElement Body,
    JsonElement? Proof)
{
    public byte[] CanonicalBytes()
    {
        var fields = new List<KeyValuePair<string, JsonElement>>
        {
            new("packet_type", JsonSerializer.SerializeToElement(PacketTypeNames.ToWire(Type))),
            new("packet_id", JsonSerializer.SerializeToElement(PacketId)),
            new("connection_id", JsonSerializer.SerializeToElement(ConnectionId)),
            new("sender_id", JsonSerializer.SerializeToElement(SenderId)),
            new("recipient_id", JsonSerializer.SerializeToElement(RecipientId)),
            new("told_at", JsonSerializer.SerializeToElement(ToldAt)),
            new("packet_nonce", JsonSerializer.SerializeToElement(PacketNonce)),
            new("body", Body),
        };
        if (Proof is not null) fields.Add(new("proof", Proof.Value));
        return CanonicalJson.SerializeObject(fields);
    }

    public void ValidateAgainst(Envelope envelope)
    {
        if (PacketId != envelope.PacketId)
            throw new ProtocolException("Plain packet_id does not match the envelope.");
        if (ConnectionId != envelope.ConnectionId)
            throw new ProtocolException("Plain connection_id does not match the envelope.");
        Envelope.RequireId(SenderId, "sender_id");
        Envelope.RequireId(RecipientId, "recipient_id");
        if (ToldAt <= 0) throw new ProtocolException("told_at must be positive unix seconds.");
        byte[] nonce = Base64Url.Decode(PacketNonce);
        if (nonce.Length != 16) throw new ProtocolException("packet_nonce must decode to 16 bytes.");
        if (Body.ValueKind != JsonValueKind.Object)
            throw new ProtocolException("Packet body must be a JSON object.");
        ValidateBodyShape();
    }

    private void ValidateBodyShape()
    {
        switch (Type)
        {
            case PacketType.Telling:
            case PacketType.Delivery:
                // Raw text only. Structured event fields, proposed
                // structure or bulk history in a telling or delivery
                // body are outside the protocol and rejected here.
                RequireOnlyKeys(Body, "kind", "text");
                string kind = Body.GetProperty("kind").GetString() ?? "";
                if (kind.Length == 0) throw new ProtocolException("Body kind must be present.");
                CanonicalJson.ValidateString(kind, "body.kind");
                if (!Body.TryGetProperty("text", out JsonElement text) || text.ValueKind != JsonValueKind.String)
                    throw new ProtocolException("Telling and delivery bodies must carry a text string.");
                break;
            case PacketType.Acknowledgement:
                RequireOnlyKeys(Body, "acked_packet_id", "disposition");
                Envelope.RequireId(Body.GetProperty("acked_packet_id").GetString() ?? "", "acked_packet_id");
                string d = Body.GetProperty("disposition").GetString() ?? "";
                if (d != "received" && d != "confirmed" && d != "declined")
                    throw new ProtocolException("Unknown acknowledgement disposition: " + d);
                break;
        }
    }

    private static void RequireOnlyKeys(JsonElement obj, params string[] allowed)
    {
        var names = new List<string>();
        foreach (var p in obj.EnumerateObject()) names.Add(p.Name);
        foreach (string n in names)
        {
            if (!allowed.Contains(n))
                throw new ProtocolException("Body field is outside the closed list for this packet type: " + n);
        }
        foreach (string a in allowed)
        {
            if (!names.Contains(a))
                throw new ProtocolException("Body is missing a required field: " + a);
        }
    }

    public static PlainPacket Parse(byte[] canonicalBytes)
    {
        using var doc = JsonDocument.Parse(canonicalBytes);
        JsonElement root = doc.RootElement.Clone();
        if (root.ValueKind != JsonValueKind.Object)
            throw new ProtocolException("Plain packet must be a JSON object.");
        JsonElement? proof = root.TryGetProperty("proof", out JsonElement pr) ? pr : null;
        var plain = new PlainPacket(
            PacketTypeNames.FromWire(root.GetProperty("packet_type").GetString() ?? ""),
            root.GetProperty("packet_id").GetString() ?? "",
            root.GetProperty("connection_id").GetString() ?? "",
            root.GetProperty("sender_id").GetString() ?? "",
            root.GetProperty("recipient_id").GetString() ?? "",
            root.GetProperty("told_at").GetInt64(),
            root.GetProperty("packet_nonce").GetString() ?? "",
            root.GetProperty("body").Clone(),
            proof);
        if (!plain.CanonicalBytes().SequenceEqual(canonicalBytes))
            throw new ProtocolException("Plain packet is not in canonical form.");
        return plain;
    }

    public static JsonElement TextBody(string kind, string text)
    {
        string json = "{\"kind\":" + JsonSerializer.Serialize(kind) +
                      ",\"text\":" + JsonSerializer.Serialize(text) + "}";
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    public static JsonElement AckBody(string ackedPacketId, string disposition)
    {
        string json = "{\"acked_packet_id\":" + JsonSerializer.Serialize(ackedPacketId) +
                      ",\"disposition\":" + JsonSerializer.Serialize(disposition) + "}";
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
