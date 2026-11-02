using System.Net.Http.Headers;
using HomeNode.Services.Extraction;
using Microsoft.Extensions.Options;

namespace HomeNode.Services.Models;

// The house's other Meta models, beside the message reader. Every one
// of them is called through the Meta Model API with the same key the
// reader uses (MODEL_API_KEY, read from the environment only and never
// logged), and only on a Live instance: off Live, hearing, seeing and
// drawing are simply unavailable, and the review runs on the house's
// own rules, labelled as such.
public class ModelOptions
{
    // Muse Voice Transcribe, file transcription (push to talk).
    public string VoiceModel { get; set; } = "muse-voice-transcribe-1.0";
    public string VoicePath { get; set; } = "/asr/transcribe";

    // Muse Wonder, the review of the record.
    public string ReviewModel { get; set; } = "muse-wonder-1.0";
    public string ReviewEffort { get; set; } = "medium";
    public int ReviewMinutesBetween { get; set; } = 10;

    // SAM 3.1, the shelf check: one noun phrase per request.
    public string SeeModel { get; set; } = "sam-3.1";
    public double SeeScoreThreshold { get; set; } = 0.5;

    // What SAM is asked to look for, per tracked supply, when the
    // supply's name is not something a camera can see by that name.
    public Dictionary<string, string> ShelfPrompts { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["heart pills"] = "pill bottle",
        ["pads"] = "package of incontinence pads",
        ["wipes"] = "package of wet wipes",
        ["gauze"] = "box of gauze pads"
    };

    // Muse Image, picture cards.
    public string DrawModel { get; set; } = "muse-image-1.0";
}

public sealed class MetaApi
{
    public const string BaseUrl = "https://api.meta.ai/v1";

    private readonly ExtractionOptions _extraction;

    public MetaApi(IOptions<ExtractionOptions> extraction, IOptions<ModelOptions> models)
    {
        _extraction = extraction.Value;
        Models = models.Value;
    }

    public ModelOptions Models { get; }

    // A Live instance with a key: the models can be called.
    public bool Live =>
        _extraction.Mode.Equals("Live", StringComparison.OrdinalIgnoreCase);

    public bool KeyPresent => Key() is not null;

    public bool Callable => Live && KeyPresent;

    public static string? Key()
    {
        var fromEnv = Environment.GetEnvironmentVariable("MODEL_API_KEY");
        return string.IsNullOrWhiteSpace(fromEnv) ? null : fromEnv.Trim();
    }

    public HttpRequestMessage Request(HttpMethod method, string path)
    {
        var key = Key() ?? throw new InvalidOperationException(
            "No Meta Model API credential is available: MODEL_API_KEY is not set.");
        var request = new HttpRequestMessage(method, BaseUrl + (path.StartsWith('/') ? path : "/" + path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return request;
    }

    // Status and a short excerpt only: an error body carries no
    // credential, and none is ever added to a message.
    public static string Failure(System.Net.HttpStatusCode status, string body) =>
        $"Meta Model API returned {(int)status}: {body[..Math.Min(300, body.Length)]}";
}
