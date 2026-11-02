using System.Text.Json;
using HomeNode.Data;
using HomeNode.Services.Extraction;
using Microsoft.EntityFrameworkCore;

namespace HomeNode.Services.Models;

// The shelf check: a person photographs the supply shelf, and SAM 3.1
// looks for each thing the house tracks, by name. The house's own
// supply list is the prompt list: one noun phrase per supply ("package
// of wet wipes"), one request each, side by side. One box is one thing
// seen; the counts and outlines come back over the photo.
//
// SAM is never asked to look for a medicine: a supply whose name is
// health-shaped (PrivacyRule) is left off the list, and the page says
// so. The photo is the person's own choice to share; the browser
// shrinks it before it leaves the device, and the house keeps it with
// the result so the outlines can be shown over it.
//
// Nothing is written to the record by a shelf check. Where SAM sees
// none of something, the page offers the sentence "We're out of …"
// for the message box; the person sends it, or not.
public sealed record ShelfCount(int SupplyId, string Name, string Prompt, int Count, List<SeenBox> Boxes, int ElapsedMs, string? Problem);

public sealed class ShelfResult
{
    public int Width { get; set; }
    public int Height { get; set; }
    public List<ShelfCount> Counts { get; set; } = new();
    public List<string> NotAsked { get; set; } = new();
}

public sealed class ShelfCheckService
{
    public const int MaxBytes = 6 * 1024 * 1024;

    private readonly AppDbContext _db;
    private readonly MetaApi _api;
    private readonly ISeeClient _see;

    public ShelfCheckService(AppDbContext db, MetaApi api, ISeeClient see)
    {
        _db = db;
        _api = api;
        _see = see;
    }

    public bool Available => _api.Callable;

    // What SAM will be asked to look for, and what it never will be.
    public static (List<(SupplyItem Supply, string Prompt)> Asked, List<string> NotAsked) PromptsFor(
        IEnumerable<SupplyItem> supplies, ModelOptions options)
    {
        var asked = new List<(SupplyItem, string)>();
        var notAsked = new List<string>();
        foreach (var supply in supplies.OrderBy(s => s.Id))
        {
            if (PrivacyRule.WordsArePrivate(supply.Name))
            {
                notAsked.Add(supply.Name);
                continue;
            }
            var prompt = options.ShelfPrompts.TryGetValue(supply.Name, out var phrase) && !string.IsNullOrWhiteSpace(phrase)
                ? phrase
                : supply.Name;
            if (asked.Count < 16)
            {
                asked.Add((supply, prompt));
            }
        }
        return (asked, notAsked);
    }

    // The photo, checked before it goes anywhere: a JPEG, PNG or WebP by
    // its own first bytes, not by what the browser called it.
    public static string? MediaType(byte[] bytes)
    {
        if (bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return "image/jpeg";
        }
        if (bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        {
            return "image/png";
        }
        if (bytes.Length > 12 && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F'
            && bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P')
        {
            return "image/webp";
        }
        return null;
    }

    public async Task<ModelCall> CheckAsync(int householdId, int? personId, byte[] photo, string mediaType,
        int width, int height, CancellationToken ct = default)
    {
        var supplies = await _db.SupplyItems.Where(s => s.HouseholdId == householdId).ToListAsync(ct);
        var (asked, notAsked) = PromptsFor(supplies, _api.Models);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var counts = await Task.WhenAll(asked.Select(async a =>
        {
            try
            {
                var seen = await _see.FindAsync(photo, mediaType, a.Prompt, ct);
                return new ShelfCount(a.Supply.Id, a.Supply.Name, a.Prompt, seen.Boxes.Count, seen.Boxes, seen.ElapsedMs, null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new ShelfCount(a.Supply.Id, a.Supply.Name, a.Prompt, 0, new List<SeenBox>(), 0,
                    "SAM 3.1 did not answer for this one.");
            }
        }));
        var result = new ShelfResult
        {
            Width = Math.Clamp(width, 1, 8000),
            Height = Math.Clamp(height, 1, 8000),
            Counts = counts.ToList(),
            NotAsked = notAsked
        };
        var call = new ModelCall
        {
            HouseholdId = householdId,
            Purpose = ModelCallPurpose.See,
            Model = _api.Models.SeeModel,
            Engine = "live",
            Outcome = counts.All(c => c.Problem is not null) ? ModelCallOutcome.Failed
                : counts.Sum(c => c.Count) == 0 ? ModelCallOutcome.Empty
                : ModelCallOutcome.Done,
            ElapsedMs = (int)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            PersonId = personId,
            OutputJson = JsonSerializer.Serialize(result),
            Image = photo,
            ImageType = mediaType,
            AtUtc = DateTime.UtcNow
        };
        _db.ModelCalls.Add(call);
        await _db.SaveChangesAsync(ct);
        return call;
    }

    public async Task<ModelCall?> LatestAsync(int householdId) =>
        await _db.ModelCalls
            .Where(c => c.HouseholdId == householdId && c.Purpose == ModelCallPurpose.See)
            .OrderByDescending(c => c.Id)
            .FirstOrDefaultAsync();

    public static ShelfResult? Read(ModelCall call)
    {
        if (call.OutputJson is null)
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<ShelfResult>(call.OutputJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
