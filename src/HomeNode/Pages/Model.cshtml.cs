using HomeNode.Data;
using HomeNode.Services;
using HomeNode.Services.Extraction;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HomeNode.Pages;

// How the house uses its model, in the open: the engine and its
// settings as this instance actually runs them, the pipeline a
// message travels with who decides at each step, live counts from
// the readings on file, and the exact instructions and output schema
// the model is held to. Nothing on this page changes anything.
//
// The counts follow the same audience rule as every other surface:
// engine and timing counts cover every reading (a question leaves a
// counted row with no content), and the entry and outcome counts
// cover only what the named reader may see.
public class ModelPageModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly ExtractionOptions _options;
    private readonly HomeNode.Services.Models.ModelOptions _models;

    public ModelPageModel(AppDbContext db, IOptions<ExtractionOptions> options,
        IOptions<HomeNode.Services.Models.ModelOptions> models)
    {
        _db = db;
        _options = options.Value;
        _models = models.Value;
    }

    public HomeNode.Services.Models.ModelOptions Models => _models;

    // The other models' work, counted from ModelCalls: per purpose,
    // how many calls, how long the middle one took, and how they ended.
    public sealed record CallStat(string Purpose, int Count, int Done, int Empty, int Failed, int MedianMs);
    public Dictionary<string, CallStat> Calls { get; private set; } = new();
    public int PrivateReadings { get; private set; }
    public int ReviewFindingsKept { get; private set; }
    public int ReviewFindingsDropped { get; private set; }

    [BindProperty(SupportsGet = true)]
    public int? ViewerId { get; set; }

    public List<Person> People { get; private set; } = new();
    public ReadingStats Stats { get; private set; } = new();
    public TimeZoneInfo HouseholdZone { get; private set; } = HouseholdTime.ZoneFor(null);

    public bool Live => _options.Mode.Equals("Live", StringComparison.OrdinalIgnoreCase);
    public string ModelId => _options.Model;
    public string ClarificationModelId => _options.ClarificationModel;
    public bool KeyPresent => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MODEL_API_KEY"));

    public string SystemPrompt => ExtractionPrompt.System.Trim();
    public string Schema => ActivityService.PrettyJson(ExtractionSchema.Value.ToJsonString()) ?? "";

    public async Task OnGetAsync()
    {
        var household = await _db.Households.FirstOrDefaultAsync();
        HouseholdZone = HouseholdTime.ZoneFor(household?.TimeZoneId);
        People = await _db.Persons.OrderBy(p => p.Id).ToListAsync();
        ViewerId = CurrentPerson.Resolve(HttpContext, ViewerId, People);
        Stats = await new ActivityService(_db).StatsAsync(ViewerId);
        PrivateReadings = await _db.ModelReadings.CountAsync(r => r.Engine == ModelReadingEngine.Private);
        var calls = await _db.ModelCalls
            .Where(c => c.Engine == "live")
            .Select(c => new { c.Purpose, c.Outcome, c.ElapsedMs, c.OutputJson })
            .ToListAsync();
        foreach (var group in calls.GroupBy(c => c.Purpose))
        {
            var times = group.Where(c => c.Outcome != ModelCallOutcome.Failed).Select(c => c.ElapsedMs).OrderBy(t => t).ToList();
            Calls[group.Key] = new CallStat(group.Key, group.Count(),
                group.Count(c => c.Outcome == ModelCallOutcome.Done),
                group.Count(c => c.Outcome == ModelCallOutcome.Empty),
                group.Count(c => c.Outcome == ModelCallOutcome.Failed),
                times.Count == 0 ? 0 : times[times.Count / 2]);
        }
        foreach (var review in calls.Where(c => c.Purpose == ModelCallPurpose.Review && c.OutputJson != null))
        {
            var result = HomeNode.Services.Models.ReviewService.Read(new ModelCall { OutputJson = review.OutputJson });
            ReviewFindingsKept += result?.Findings.Count ?? 0;
            ReviewFindingsDropped += result?.Dropped ?? 0;
        }
    }
}
