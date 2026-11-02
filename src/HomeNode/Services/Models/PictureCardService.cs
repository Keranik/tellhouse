using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HomeNode.Data;
using HomeNode.Services.Extraction;
using Microsoft.EntityFrameworkCore;

namespace HomeNode.Services.Models;

// Drawing: Muse Image makes a picture card for a message the house has
// passed on to a child, so it can be taken in without reading: a plate
// and a big clock at 6:30 for "Dinner at 6:30". It runs in the
// background after the delivery is written (a drawing takes seconds),
// and the card appears with the delivery in the child's Inbox.
//
// Only for what a whole household may see, and never for anything
// health-shaped: the card is drawn from words already shared with the
// household, and a private or health entry is never sent to a model.
// The prompt asks for no faces and no writing; the child's name is
// not sent.
public interface IDrawClient
{
    Task<(byte[] Image, string MediaType, int ElapsedMs)> DrawAsync(string prompt, CancellationToken ct = default);
}

public sealed class MetaDrawClient : IDrawClient
{
    private readonly MetaApi _api;

    public MetaDrawClient(MetaApi api)
    {
        _api = api;
    }

    public async Task<(byte[] Image, string MediaType, int ElapsedMs)> DrawAsync(string prompt, CancellationToken ct = default)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var body = new JsonObject
        {
            ["model"] = _api.Models.DrawModel,
            ["input"] = prompt
        };
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        using var request = _api.Request(HttpMethod.Post, "/responses");
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(MetaApi.Failure(response.StatusCode, text));
        }
        var image = ImageFrom(text) ?? throw new InvalidOperationException("Muse Image returned no picture.");
        var media = ShelfCheckService.MediaType(image) ?? "image/png";
        return (image, media, (int)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    // The picture is the "result" of the image_generation_call item in
    // the response's output, base64 encoded.
    public static byte[]? ImageFrom(string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson);
        if (!doc.RootElement.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        foreach (var item in output.EnumerateArray())
        {
            if (item.TryGetProperty("type", out var type) && type.GetString() == "image_generation_call"
                && item.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.String)
            {
                var b64 = result.GetString()!;
                var comma = b64.IndexOf(',');
                if (b64.StartsWith("data:", StringComparison.Ordinal) && comma > 0)
                {
                    b64 = b64[(comma + 1)..];
                }
                try
                {
                    return Convert.FromBase64String(b64);
                }
                catch (FormatException)
                {
                    return null;
                }
            }
        }
        return null;
    }
}

public sealed class PictureCardService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<PictureCardService> _logger;

    public PictureCardService(IServiceScopeFactory scopes, ILogger<PictureCardService> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    // A delivery gets a picture card only when it went to a child and
    // came from an entry the whole household may see, with no
    // health-shaped words in it.
    public static bool Eligible(Person? to, Event? source, string body) =>
        to is { MinorFlag: true }
        && source is not null
        && source.Type != EventType.HealthMention
        && source.SensitivityClass is SensitivityClass.Open or SensitivityClass.Household
        && !PrivacyRule.WordsArePrivate(body)
        && !PrivacyRule.WordsArePrivate(source.Body);

    // The drawing brief, from the delivered words: a simple scene, no
    // faces, no writing, and a big clock when the words name a time.
    // Names are taken out first: a leading "Ask Timmy to" or "Tell
    // Timmy" goes, and any household name left becomes "someone", so
    // no one's name is sent to the model.
    public static string PromptFor(string body, IEnumerable<string> names)
    {
        var words = body.Trim();
        var first = names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(Regex.Escape).ToList();
        if (first.Count > 0)
        {
            var any = string.Join("|", first);
            words = Regex.Replace(words, $@"^(ask|tell|remind)\s+({any})\s+(to\s+|that\s+)?", "", RegexOptions.IgnoreCase);
            words = Regex.Replace(words, $@"\b({any})\b", "someone", RegexOptions.IgnoreCase);
        }
        if (words.Length > 160)
        {
            words = words[..160];
        }
        var time = Regex.Match(words, @"\b(?:at\s+)?(\d{1,2})(?::(\d{2}))?\s*(am|pm|a\.m\.|p\.m\.)\b|\b(\d{1,2}):(\d{2})\b",
            RegexOptions.IgnoreCase);
        var clock = "";
        if (time.Success)
        {
            var hour = time.Groups[1].Success ? time.Groups[1].Value : time.Groups[4].Value;
            var minute = time.Groups[2].Success ? time.Groups[2].Value : time.Groups[5].Success ? time.Groups[5].Value : "00";
            clock = $" Include one large, simple analog clock with its hands at {hour}:{minute}.";
        }
        return "A simple, friendly picture card for a young child, showing this household message as one clear scene: \""
            + words + "\". Flat illustration, bright soft colours, rounded shapes, plain light background."
            + " Do not draw any faces. Do not write any words, letters or numbers." + clock;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DrawPendingAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Picture cards: a pass failed");
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(4), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task DrawPendingAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<MetaApi>();
        if (!api.Callable)
        {
            return;
        }
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var draw = scope.ServiceProvider.GetRequiredService<IDrawClient>();
        var since = DateTime.UtcNow.AddMinutes(-15);
        var recent = await db.AlertDeliveries
            .Include(d => d.ToPerson)
            .Include(d => d.Event)
            .Where(d => d.DeliveredAtUtc >= since && d.ToPerson != null && d.ToPerson.MinorFlag)
            .ToListAsync(ct);
        if (recent.Count == 0)
        {
            return;
        }
        var names = (await db.Persons.Select(p => p.DisplayName).ToListAsync(ct))
            .SelectMany(n => n.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Distinct().ToList();
        var ids = recent.Select(d => d.Id).ToList();
        var done = (await db.ModelCalls
            .Where(c => c.Purpose == ModelCallPurpose.Draw && c.DeliveryId != null && ids.Contains(c.DeliveryId.Value))
            .Select(c => c.DeliveryId!.Value).ToListAsync(ct)).ToHashSet();
        foreach (var delivery in recent.Where(d => !done.Contains(d.Id) && Eligible(d.ToPerson, d.Event, d.BodyText)))
        {
            var call = new ModelCall
            {
                HouseholdId = delivery.Event!.HouseholdId,
                Purpose = ModelCallPurpose.Draw,
                Model = api.Models.DrawModel,
                Engine = "live",
                PersonId = delivery.ToPersonId,
                DeliveryId = delivery.Id,
                EventId = delivery.EventId,
                AtUtc = DateTime.UtcNow
            };
            try
            {
                var (image, media, ms) = await draw.DrawAsync(PromptFor(delivery.BodyText, names), ct);
                call.Image = image;
                call.ImageType = media;
                call.ElapsedMs = ms;
                call.Outcome = ModelCallOutcome.Done;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Recorded as tried, so it is not tried again on every
                // pass; the delivery stands without a card.
                _logger.LogWarning(ex, "Muse Image did not draw a card for delivery {DeliveryId}", delivery.Id);
                call.Outcome = ModelCallOutcome.Failed;
            }
            db.ModelCalls.Add(call);
            await db.SaveChangesAsync(ct);
        }
    }
}
