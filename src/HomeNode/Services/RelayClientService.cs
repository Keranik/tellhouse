using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HomeNode.Protocol;

namespace HomeNode.Services;

// The node's side of the sealed relay (the relay contract).
// The relay is a dumb store-and-forward: POST /packets
// accepts a protocol wire packet, GET /inbox polls a destination
// bucket. Auth is the relay's own standing check: the connection
// id and its token in the X-Connection-Id and X-Connection-Token
// headers; the relay holds token hashes only and no keys.
//
// Forwarding is by side, per the relay contract: this client
// speaks only as the node-role connection, so its poll collects
// every queued packet for the household that the node did not
// post itself. Member-bound deliveries leave the other way: the
// node posts them, and each member collects its own. Packets the
// poll returns are dropped by the relay on forward; what the
// node does with them is intake (SealedIntakeService), and a
// telling is recorded only when the node takes it in, never
// because the relay moved it.
//
// Relay timing (received_at, expires_at) rides the poll response
// as relay generated fields, not envelope fields; this client
// surfaces them for the packet view and strips them before
// intake, rebuilding the exact wire packet (ciphertext,
// envelope, signature) the relay carried.
public class RelayClientService
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _nodeConnectionId;
    private readonly string _nodeToken;

    public RelayClientService(HttpClient http, string baseUrl, string nodeConnectionId, string nodeToken)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
        _nodeConnectionId = nodeConnectionId;
        _nodeToken = nodeToken;
    }

    public record RelayPosted(string PacketId, long ExpiresAt);

    public record RelayPacket(string WireJson, long ReceivedAt, long ExpiresAt);

    // Poll entries dropped as malformed by the most recent poll
    // The relay drops every packet it forwards, so
    // a malformed entry must be counted and surfaced here, never
    // allowed to throw away the valid entries polled with it.
    public int LastPollDroppedMalformed { get; private set; }

    public async Task<RelayPosted> PostPacketAsync(string wireJson, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/packets");
        request.Headers.Add("X-Connection-Id", _nodeConnectionId);
        request.Headers.Add("X-Connection-Token", _nodeToken);
        request.Content = new StringContent(wireJson, Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            // 409 duplicate: the relay already holds a
            // packet with this id, which is what a successful post
            // leaves behind when its response is lost. The packet
            // IS at the relay, so the post counts as sent; the
            // caller marks the outbox item Sent and moves on to
            // the next one instead of re-posting forever.
            string packetId = "";
            try
            {
                using var wire = JsonDocument.Parse(wireJson);
                packetId = wire.RootElement.GetProperty("envelope")
                    .GetProperty("packet_id").GetString() ?? "";
            }
            catch (JsonException) { }
            return new RelayPosted(packetId, 0);
        }
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Relay refused the packet: {(int)response.StatusCode} {body}");
        }
        using var doc = JsonDocument.Parse(body);
        return new RelayPosted(
            doc.RootElement.GetProperty("packet_id").GetString() ?? "",
            doc.RootElement.GetProperty("expires_at").GetInt64());
    }

    public async Task<List<RelayPacket>> PollHouseholdAsync(string householdEndpoint, CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/inbox?household_endpoint={Uri.EscapeDataString(householdEndpoint)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Connection-Id", _nodeConnectionId);
        request.Headers.Add("X-Connection-Token", _nodeToken);
        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Relay refused the poll: {(int)response.StatusCode} {body}");
        }
        using var doc = JsonDocument.Parse(body);
        var packets = new List<RelayPacket>();
        LastPollDroppedMalformed = 0;
        foreach (var entry in doc.RootElement.GetProperty("packets").EnumerateArray())
        {
            // Per-packet isolation: the relay has
            // already dropped every entry in this response, so an
            // entry that cannot be rebuilt (a type-confused field
            // is the case in point) is skipped and counted, and
            // the valid entries are still returned for intake.
            try
            {
                packets.Add(new RelayPacket(
                    RebuildWireJson(entry),
                    Envelope.RequireInt64(entry, "received_at"),
                    Envelope.RequireInt64(entry, "expires_at")));
            }
            catch (Exception ex) when (ex is ProtocolException or JsonException
                    or FormatException or InvalidOperationException or KeyNotFoundException)
            {
                LastPollDroppedMalformed++;
            }
        }
        return packets;
    }

    // The relay's poll shape is flat: the eight envelope fields,
    // the two relay timing fields, ciphertext and signature, side
    // by side. The wire packet nests the envelope; this rebuilds
    // it field for field, dropping only the relay timing.
    public static string RebuildWireJson(JsonElement flatEntry)
    {
        // The envelope is rebuilt through the protocol record, so
        // the nested object is in the contract's canonical form;
        // the relay's flat field order does not survive into the
        // wire packet. Relay timing fields are dropped.
        var envelope = new Envelope(
            Envelope.RequireString(flatEntry, "packet_id"),
            Envelope.RequireString(flatEntry, "household_endpoint"),
            Envelope.RequireString(flatEntry, "connection_id"),
            EnvelopeClassNames.FromWire(Envelope.RequireString(flatEntry, "envelope_class")),
            Envelope.RequireString(flatEntry, "delivery_mode"),
            Envelope.RequireInt64(flatEntry, "ciphertext_length"),
            Envelope.RequireInt64(flatEntry, "issued_at"),
            Envelope.RequireString(flatEntry, "ciphertext_hash"));
        var wire = new JsonObject
        {
            ["ciphertext"] = Envelope.RequireString(flatEntry, "ciphertext"),
            ["envelope"] = JsonNode.Parse(envelope.ToCanonicalJson()),
            ["signature"] = Envelope.RequireString(flatEntry, "signature")
        };
        return wire.ToJsonString();
    }
}
