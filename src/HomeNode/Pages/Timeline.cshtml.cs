using HomeNode.Data;
using HomeNode.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace HomeNode.Pages;

// The family timeline: the shared record, listed plainly, newest first,
// as the person acting here may read it. The list honors the audience
// stored on each entry at write time, exactly as the brief does: an
// entry written for named people appears only to them, and a delivery
// appears only to the person it was delivered to. This page is also
// where a person answers a proposal or closes an entry nobody signed:
// every button here goes through the one confirmation dispatcher, so
// the audit trail names the person who acted.
public class TimelineModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly ConfirmationService _confirmations;

    public TimelineModel(AppDbContext db, ConfirmationService confirmations)
    {
        _db = db;
        _confirmations = confirmations;
    }

    public List<Event> Events { get; private set; } = new();
    public List<SupplyItem> Supplies { get; private set; } = new();
    public List<Person> People { get; private set; } = new();
    public List<AlertDelivery> Deliveries { get; private set; } = new();

    // The household zone record times are shown in. Stored instants
    // are true UTC; rendering converts them to this zone, the same
    // conversion LookupService performs for a spoken answer, so every
    // surface names the same clock time for the same entry.
    public TimeZoneInfo HouseholdZone { get; private set; } =
        TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");

    public DateTime ToHouseholdTime(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(utc, DateTimeKind.Utc), HouseholdZone);

    [BindProperty(SupportsGet = true)]
    public int? ActorId { get; set; }

    public string? Note => TempData["TimelineNote"] as string;

    public async Task OnGetAsync()
    {
        await LoadAsync();
    }

    public async Task<IActionResult> OnPostConfirmAsync(int eventId, int actorId)
    {
        await _confirmations.ConfirmAsync(eventId, actorId, "tap");
        TempData["TimelineNote"] = "Confirmed. The record shows who confirmed it.";
        return RedirectToPage(new { ActorId = actorId });
    }

    public async Task<IActionResult> OnPostDeclineAsync(int eventId, int actorId)
    {
        await _confirmations.DeclineAsync(eventId, actorId, "tap");
        TempData["TimelineNote"] = "Declined. Nothing was sent, and the record says so.";
        return RedirectToPage(new { ActorId = actorId });
    }

    public async Task<IActionResult> OnPostCloseAsync(int eventId, int actorId)
    {
        await _confirmations.CloseRecordPendingAsync(eventId, actorId, "tap");
        TempData["TimelineNote"] = "Closed. The record names the person who closed it.";
        return RedirectToPage(new { ActorId = actorId });
    }

    private async Task LoadAsync()
    {
        var household = await _db.Households.FirstOrDefaultAsync();
        if (household?.TimeZoneId is { } zoneId)
        {
            try
            {
                HouseholdZone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
            }
            catch (TimeZoneNotFoundException)
            {
            }
        }

        People = await _db.Persons.OrderBy(p => p.Id).ToListAsync();

        // No reader named means the house view only: the landing
        // shows what any reader may see (Open and Household
        // entries) and nothing written for named people, matching
        // the front door, the promises page, and the plans page.
        // Defaulting to the first person here would hand a bare
        // visit that person's full view, Restricted entries
        // included.
        var allEvents = await _db.Events
            .Include(e => e.AuthorPerson)
            .Include(e => e.SubjectPerson)
            .OrderByDescending(e => e.CreatedAtUtc)
            .Take(100)
            .ToListAsync();
        Events = ActorId is int actor
            ? allEvents.Where(e => BriefService.IsVisibleTo(e, actor)).ToList()
            : allEvents.Where(e => e.SensitivityClass is SensitivityClass.Open or SensitivityClass.Household).ToList();

        Supplies = await _db.SupplyItems
            .Include(s => s.AssignedPerson)
            .OrderBy(s => s.Name)
            .ToListAsync();

        Deliveries = ActorId is int deliveryViewer
            ? await _db.AlertDeliveries
                .Include(d => d.ToPerson)
                .Include(d => d.Event)
                .Where(d => d.ToPersonId == deliveryViewer)
                .OrderByDescending(d => d.DeliveredAtUtc)
                .Take(20)
                .ToListAsync()
            : new List<AlertDelivery>();
    }
}
