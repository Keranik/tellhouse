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

    public ModelPageModel(AppDbContext db, IOptions<ExtractionOptions> options)
    {
        _db = db;
        _options = options.Value;
    }

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
        Stats = await new ActivityService(_db).StatsAsync(ViewerId);
    }
}
