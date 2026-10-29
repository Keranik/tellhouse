using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HomeNode.Protocol;

namespace HomeNode.Services;

// The node's side of the sealed relay (see relay/CONTRACT.md).
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

    public async Task<RelayPosted> PostPacketAsync(string wireJson, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/packets");
        request.Headers.Add("X-Connection-Id", _nodeConnectionId);
        request.Headers.Add("X-Connection-Token", _nodeToken);
        request.Content = new StringContent(wireJson, Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
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
        foreach (var entry in doc.RootElement.GetProperty("packets").EnumerateArray())
        {
            packets.Add(new RelayPacket(
                RebuildWireJson(entry),
                entry.GetProperty("received_at").GetInt64(),
                entry.GetProperty("expires_at").GetInt64()));
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
            flatEntry.GetProperty("packet_id").GetString() ?? "",
            flatEntry.GetProperty("household_endpoint").GetString() ?? "",
            flatEntry.GetProperty("connection_id").GetString() ?? "",
            EnvelopeClassNames.FromWire(flatEntry.GetProperty("envelope_class").GetString() ?? ""),
            flatEntry.GetProperty("delivery_mode").GetString() ?? "",
            flatEntry.GetProperty("ciphertext_length").GetInt64(),
            flatEntry.GetProperty("issued_at").GetInt64(),
            flatEntry.GetProperty("ciphertext_hash").GetString() ?? "");
        var wire = new JsonObject
        {
            ["ciphertext"] = flatEntry.GetProperty("ciphertext").GetString(),
            ["envelope"] = JsonNode.Parse(envelope.ToCanonicalJson()),
            ["signature"] = flatEntry.GetProperty("signature").GetString()
        };
        return wire.ToJsonString();
    }
}
