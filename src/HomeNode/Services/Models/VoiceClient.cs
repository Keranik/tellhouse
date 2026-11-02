using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HomeNode.Services.Models;

// Hearing: Muse Voice Transcribe, file transcription in push-to-talk
// mode. The person holds the mic on Home, the browser records a short
// 16 kHz mono 16-bit WAV, and the words come back into the message box
// for the person to read, change, and send. Nothing is sent to the
// house's record until they press Send.
//
// The house tells the transcriber its own vocabulary as keywords: the
// household's names and the supplies it tracks, so "Daniel" and
// "gauze" come back spelled the way the record spells them.
public sealed record HeardClip(string Transcript, int? AudioDurationMs, string? SessionId, int ElapsedMs, string Model);

public interface IVoiceClient
{
    Task<HeardClip> TranscribeAsync(byte[] wav, IReadOnlyList<string> keywords, CancellationToken ct = default);
}

public sealed class MetaVoiceClient : IVoiceClient
{
    private readonly MetaApi _api;

    public MetaVoiceClient(MetaApi api)
    {
        _api = api;
    }

    public async Task<HeardClip> TranscribeAsync(byte[] wav, IReadOnlyList<string> keywords, CancellationToken ct = default)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var requestPart = new JsonObject
        {
            ["model"] = _api.Models.VoiceModel,
            ["audioEncoding"] = "WAV",
            ["mode"] = "PUSH_TO_TALK"
        };
        if (keywords.Count > 0)
        {
            requestPart["keywords"] = new JsonArray(keywords.Select(k => (JsonNode?)JsonValue.Create(k)).ToArray());
        }

        using var form = new MultipartFormDataContent();
        var json = new StringContent(requestPart.ToJsonString(), Encoding.UTF8, "application/json");
        form.Add(json, "request");
        var audio = new ByteArrayContent(wav);
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(audio, "audio", "clip.wav");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        using var request = _api.Request(HttpMethod.Post, _api.Models.VoicePath);
        request.Content = form;
        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(MetaApi.Failure(response.StatusCode, text));
        }
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        var transcript = ReadTranscript(root);
        int? duration = root.TryGetProperty("audioDurationMs", out var d) && d.TryGetInt32(out var ms) ? ms : null;
        var session = root.TryGetProperty("sessionId", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        var elapsed = (int)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return new HeardClip(transcript, duration, session, elapsed, _api.Models.VoiceModel);
    }

    // The whole transcript, or, when the response carries only turns,
    // the turns' text joined in order.
    private static string ReadTranscript(JsonElement root)
    {
        if (root.TryGetProperty("transcript", out var t) && t.ValueKind == JsonValueKind.String)
        {
            return (t.GetString() ?? "").Trim();
        }
        if (root.TryGetProperty("turns", out var turns) && turns.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var turn in turns.EnumerateArray())
            {
                foreach (var name in new[] { "transcript", "text" })
                {
                    if (turn.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String)
                    {
                        parts.Add(p.GetString()!.Trim());
                        break;
                    }
                }
            }
            return string.Join(" ", parts.Where(p => p.Length > 0));
        }
        return "";
    }
}

// The browser's clip, checked before anything is sent anywhere: a
// RIFF/WAVE file, uncompressed PCM, mono, 16-bit, at 16 or 24 kHz, and
// no longer than the house allows for one message.
public static class WavCheck
{
    public const int MaxSeconds = 60;
    public const int MaxBytes = 24_000 * 2 * MaxSeconds + 4096;

    public static string? Problem(byte[] wav)
    {
        if (wav.Length < 44 || wav.Length > MaxBytes)
        {
            return "The recording was empty or too long.";
        }
        if (Encoding.ASCII.GetString(wav, 0, 4) != "RIFF" || Encoding.ASCII.GetString(wav, 8, 4) != "WAVE")
        {
            return "The recording was not a WAV file.";
        }
        var pos = 12;
        while (pos + 8 <= wav.Length)
        {
            var id = Encoding.ASCII.GetString(wav, pos, 4);
            var size = BitConverter.ToInt32(wav, pos + 4);
            if (size < 0)
            {
                break;
            }
            if (id == "fmt " && pos + 8 + 16 <= wav.Length)
            {
                var format = BitConverter.ToInt16(wav, pos + 8);
                var channels = BitConverter.ToInt16(wav, pos + 10);
                var rate = BitConverter.ToInt32(wav, pos + 12);
                var bits = BitConverter.ToInt16(wav, pos + 22);
                if (format != 1 || channels != 1 || bits != 16 || (rate != 16000 && rate != 24000))
                {
                    return "The recording was not 16-bit mono at 16 or 24 kHz.";
                }
                var seconds = (wav.Length - 44) / (double)(rate * 2);
                return seconds > MaxSeconds ? "The recording was longer than a minute." : null;
            }
            pos += 8 + size + (size % 2);
        }
        return "The recording had no format header.";
    }
}
