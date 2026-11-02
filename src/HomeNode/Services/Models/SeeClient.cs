using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HomeNode.Services.Models;

// Seeing: SAM 3.1 through the Meta Model API's Responses endpoint,
// streamed. One noun phrase per request ("package of wet wipes"); the
// image goes in as a data URL. The answer is not JSON: the output text
// carries SAM's token grammar, one line per frame,
//
//   <Nf> then, per object, an id, <|box;x1=..;y1=..;x2=..;y2=..;w=..;h=..|>
//   and <|mask;x=..;y=..;data=..|>
//
// in the image's own pixels, with c=.. (confidence) on a token when it
// is asked for. The house reads the boxes only: one box is one thing
// seen. A finished answer with no boxes is a valid "none seen".
public sealed record SeenBox(int Left, int Top, int Right, int Bottom, double? Confidence);

public sealed record SeenConcept(string Prompt, List<SeenBox> Boxes, int ElapsedMs, string? Problem);

public interface ISeeClient
{
    Task<SeenConcept> FindAsync(byte[] image, string mediaType, string nounPhrase, CancellationToken ct = default);
}

public sealed class MetaSeeClient : ISeeClient
{
    private readonly MetaApi _api;

    public MetaSeeClient(MetaApi api)
    {
        _api = api;
    }

    public async Task<SeenConcept> FindAsync(byte[] image, string mediaType, string nounPhrase, CancellationToken ct = default)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var body = new JsonObject
        {
            ["model"] = _api.Models.SeeModel,
            ["stream"] = true,
            ["input"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        new JsonObject { ["type"] = "input_text", ["text"] = nounPhrase },
                        new JsonObject
                        {
                            ["type"] = "input_image",
                            ["image_url"] = $"data:{mediaType};base64,{Convert.ToBase64String(image)}"
                        }
                    }
                }
            },
            // Responses metadata values are strings.
            ["metadata"] = new JsonObject
            {
                ["include_confidence"] = "true",
                ["score_threshold"] = _api.Models.SeeScoreThreshold.ToString("0.##", CultureInfo.InvariantCulture)
            }
        };
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        using var request = _api.Request(HttpMethod.Post, "/responses");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        var elapsed = (int)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(MetaApi.Failure(response.StatusCode, text));
        }
        var output = SamGrammar.OutputText(text);
        return new SeenConcept(nounPhrase, SamGrammar.Boxes(output), elapsed, null);
    }
}

public static class SamGrammar
{
    private static readonly Regex BoxToken = new(@"<\|box;([^|>]*)\|>", RegexOptions.Compiled);

    // The answer's output text: the streamed deltas joined, or, when
    // the server answered with one JSON document, its output_text parts.
    public static string OutputText(string body)
    {
        var trimmed = body.TrimStart();
        if (trimmed.StartsWith('{'))
        {
            using var doc = JsonDocument.Parse(trimmed);
            return FromResponse(doc.RootElement);
        }
        var deltas = new StringBuilder();
        string? completed = null;
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }
            var payload = line[5..].Trim();
            if (payload.Length == 0 || payload == "[DONE]")
            {
                continue;
            }
            try
            {
                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;
                var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type == "response.output_text.delta" && root.TryGetProperty("delta", out var d))
                {
                    deltas.Append(d.GetString());
                }
                else if (type == "response.completed" && root.TryGetProperty("response", out var r))
                {
                    completed = FromResponse(r);
                }
            }
            catch (JsonException)
            {
                // A line that is not an event the house reads.
            }
        }
        return deltas.Length > 0 ? deltas.ToString() : completed ?? "";
    }

    private static string FromResponse(JsonElement response)
    {
        var text = new StringBuilder();
        if (response.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in output.EnumerateArray())
            {
                if (item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var part in content.EnumerateArray())
                    {
                        if (part.TryGetProperty("type", out var pt) && pt.GetString() == "output_text"
                            && part.TryGetProperty("text", out var pText))
                        {
                            text.Append(pText.GetString());
                        }
                    }
                }
            }
        }
        return text.ToString();
    }

    // The boxes in the first frame that has any: one box, one thing
    // seen. Coordinates are the image's own pixels.
    public static List<SeenBox> Boxes(string outputText)
    {
        var frames = outputText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var frame in frames)
        {
            var boxes = new List<SeenBox>();
            foreach (Match match in BoxToken.Matches(frame))
            {
                var fields = match.Groups[1].Value.Split(';', StringSplitOptions.RemoveEmptyEntries)
                    .Select(kv => kv.Split('=', 2))
                    .Where(kv => kv.Length == 2)
                    .GroupBy(kv => kv[0].Trim())
                    .ToDictionary(g => g.Key, g => g.First()[1].Trim());
                double Num(string key) =>
                    fields.TryGetValue(key, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : double.NaN;
                var x1 = Num("x1");
                var y1 = Num("y1");
                var x2 = Num("x2");
                var y2 = Num("y2");
                if (double.IsNaN(x2) && !double.IsNaN(x1) && !double.IsNaN(Num("w")))
                {
                    x2 = x1 + Num("w");
                }
                if (double.IsNaN(y2) && !double.IsNaN(y1) && !double.IsNaN(Num("h")))
                {
                    y2 = y1 + Num("h");
                }
                if (double.IsNaN(x1) || double.IsNaN(y1) || double.IsNaN(x2) || double.IsNaN(y2))
                {
                    continue;
                }
                var c = Num("c");
                boxes.Add(new SeenBox((int)Math.Round(x1), (int)Math.Round(y1), (int)Math.Round(x2), (int)Math.Round(y2),
                    double.IsNaN(c) ? null : c));
            }
            if (boxes.Count > 0)
            {
                return boxes;
            }
        }
        return new List<SeenBox>();
    }
}
