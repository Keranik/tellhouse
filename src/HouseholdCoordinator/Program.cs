using HouseholdCoordinator.Data;
using HouseholdCoordinator.Services;
using HouseholdCoordinator.Services.Extraction;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

// The local demo suite runs the whole pipeline against a scratch
// slice and exits; it never starts the web host or touches the demo
// database. See Services/DemoSuite.cs.
if (args.Contains("--demo-suite"))
{
    return await DemoSuite.RunAsync();
}

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddScoped<ConfirmationService>();

// The extraction pipeline behind the model-role seam. The mode comes
// from configuration (Extraction:Mode): "Mock" runs the deterministic
// local extractor (development, tests, the demo suite; no credit
// spent), "Live" calls the Meta Model API. Default is Mock.
builder.Services.Configure<ExtractionOptions>(builder.Configuration.GetSection("Extraction"));
builder.Services.AddScoped<IExtractionClient>(sp =>
{
    var options = sp.GetRequiredService<IOptions<ExtractionOptions>>().Value;
    return options.Mode.Equals("Live", StringComparison.OrdinalIgnoreCase)
        ? ActivatorUtilities.CreateInstance<MetaExtractionClient>(sp)
        : new MockExtractionClient();
});
builder.Services.AddScoped<ExtractionContextBuilder>();
builder.Services.AddScoped<ExtractionService>();
builder.Services.AddScoped<PlanAssemblyService>();
builder.Services.AddScoped<DinnerProvenanceService>();
builder.Services.AddScoped<MemberDeliveryService>();
builder.Services.AddScoped<BriefService>();
builder.Services.AddScoped<LookupService>();

// The one timer in the system: it runs the 24-hour lapse on
// unanswered proposals (see Services/LapseService.cs).
builder.Services.AddHostedService<LapseService>();

// The shared record lives in one SQLite file beside the app. Delete the
// file to start over; the app creates it and seeds the demo household on
// the next start.
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("HouseholdCoordinator")));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
    DbSeeder.Seed(db);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();
app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

app.Run();
return 0;
