using System.Text;
using System.Text.Json;

namespace HomeNode.Services;

// Configuration for the judge-drivable Test Send control, read from
// the Demo section of configuration. Each entry names one stand-in
// assistant, the trigger endpoint on that stand-in, and the scripted
// act the stand-in sends when triggered. The act id names one of the
// stand-in's own scripted tellings; the telling's words live in the
// stand-in's own script file, never in configuration here and never
// in the trigger request. Empty by default: an instance with no
// stand-ins configured shows no live control, only the explanation.
public class DemoOptions
{
    public List<StandInEndpoint> StandinEndpoints { get; set; } = new();
}

public class StandInEndpoint
{
    public string Name { get; set; } = "";
    public string PersonName { get; set; } = "";
    public string TriggerUrl { get; set; } = "";
    public string ActId { get; set; } = "";
}

public class TestSendResult
{
    public required string StandInName { get; init; }
    // One of: sent, queued-unrecorded, unreachable, unconfigured.
    // "queued-unrecorded" is the relay stopped variant surfacing
    // honestly, in the stand-in's own terms: the stand-in accepted
    // the trigger and holds its own telling in its own outbox,
    // marked unrecorded; nothing landed in the record.
    public required string Status { get; init; }
    public string? PacketId { get; init; }
    public required string Note { get; init; }
}

// The Test Send control surface's only job: a remote trigger. The
// request this service sends carries a trigger verb, a request id,
// a timestamp, and the act id the stand-in's trigger endpoint
// requires, and NO telling content of any kind: no text field exists
// in the payload, so a judge's click cannot inject words into the
// record. The stand-in answers by sending its OWN sealed telling
// through the real ingress (knock state checked, signature verified,
// valve basis required, sealed packet through the relay, opened and
// extracted on the node). This service holds no database reference
// and no path into the record; the node suite asserts both.
//
// Wire shape note: the monolith reference payload carried
// exactly trigger, requestId, and requestedAtUtc. The real stand-ins
// refuse a trigger with no act id (their endpoint answers 400, "act
// id required"), so the act id rides along as a fourth, identifier
// only field. The stand-in's real shape wins over the reference
// shape; the no-content rule is unchanged, and the stand-in refuses
// any request that tries to carry a content field at all.
public class TestSendService
{
    private readonly HttpClient _http;
    private readonly IReadOnlyList<StandInEndpoint> _standIns;

    public TestSendService(HttpClient http, IEnumerable<StandInEndpoint> standIns)
    {
        _http = http;
        _standIns = standIns.ToList();
    }

    public IReadOnlyList<StandInEndpoint> ConfiguredStandIns => _standIns;

    // The exact payload shape sent to a stand-in trigger endpoint.
    // Kept as one method so the suite can serialize it and assert
    // that no content bearing field exists. The act field is an
    // identifier naming the stand-in's own scripted telling; it
    // carries none of the telling's words.
    public static string BuildTriggerPayload(string requestId, DateTime requestedAtUtc, string actId)
    {
        return JsonSerializer.Serialize(new
        {
            trigger = "test-send",
            requestId,
            requestedAtUtc,
            act = actId
        });
    }

    public async Task<TestSendResult> TriggerAsync(string standInName)
    {
        var standIn = _standIns.FirstOrDefault(
            s => s.Name.Equals(standInName, StringComparison.OrdinalIgnoreCase));
        if (standIn is null)
        {
            return new TestSendResult
            {
                StandInName = standInName,
                Status = "unconfigured",
                Note = "No stand-in with that name is configured on this home base. Nothing was sent."
            };
        }
        // The two failure shapes this service can detect before
        // sending: no usable trigger address, or no act configured.
        // In both it sends nothing and says so.
        if (string.IsNullOrWhiteSpace(standIn.TriggerUrl)
            || !Uri.TryCreate(standIn.TriggerUrl, UriKind.Absolute, out var triggerUri)
            || (triggerUri.Scheme != Uri.UriSchemeHttp && triggerUri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(standIn.ActId))
        {
            return new TestSendResult
            {
                StandInName = standIn.Name,
                Status = "unconfigured",
                Note = "That stand-in has no usable trigger configured on this home base. Nothing was sent."
            };
        }
        var payload = BuildTriggerPayload(
            $"ts-{Guid.NewGuid():N}"[..20], DateTime.UtcNow, standIn.ActId);
        try
        {
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(standIn.TriggerUrl, content);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                return new TestSendResult
                {
                    StandInName = standIn.Name,
                    Status = "unreachable",
                    Note = $"The stand-in answered with HTTP {(int)response.StatusCode} and sent nothing. Nothing landed in the record from this click."
                };
            }
            // The stand-in's own answer, read in its own terms. Its
            // sent shape is { act, packet_id, relay_status,
            // relay_result }; its relay stopped shape is HTTP 202
            // with { act, packet_id, result: "queued locally,
            // unrecorded" }. The stub shape ({ status,
            // packetId }) is read too, so the suite's stub answers
            // and the real stand-in's answers surface the same way.
            string status = "sent";
            string? packetId = null;
            string? standInWords = null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.TryGetProperty("packet_id", out var pid) && pid.ValueKind == JsonValueKind.String)
                {
                    packetId = pid.GetString();
                }
                if (root.TryGetProperty("packetId", out var p) && p.ValueKind == JsonValueKind.String)
                {
                    packetId = p.GetString();
                }
                if (root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String)
                {
                    standInWords = r.GetString();
                    if (standInWords is not null
                        && standInWords.Contains("unrecorded", StringComparison.OrdinalIgnoreCase))
                    {
                        status = "queued-unrecorded";
                    }
                }
                if (root.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String)
                {
                    var word = s.GetString() ?? "";
                    if (word is "queued" or "queued-unrecorded")
                    {
                        status = "queued-unrecorded";
                    }
                }
                if (root.TryGetProperty("note", out var n) && n.ValueKind == JsonValueKind.String)
                {
                    standInWords = n.GetString() ?? standInWords;
                }
            }
            catch (JsonException)
            {
                // A stand-in that answers without the status shape
                // still counts as triggered; the record is the
                // evidence, not this note.
            }
            var note = status == "queued-unrecorded"
                ? "The stand-in holds its telling in its own outbox, marked unrecorded. It has not landed in the record. This is what a stopped relay looks like from the house."
                : "The stand-in was triggered and sent its own telling through the relay. Watch the record for it, landing with its provenance.";
            if (standInWords is not null && status == "sent")
            {
                note += $" The stand-in answered: {standInWords}";
            }
            return new TestSendResult
            {
                StandInName = standIn.Name,
                Status = status,
                PacketId = packetId,
                Note = note
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new TestSendResult
            {
                StandInName = standIn.Name,
                Status = "unreachable",
                Note = "The stand-in did not answer. Nothing landed in the record from this click."
            };
        }
    }
}
