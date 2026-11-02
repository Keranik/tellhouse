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
        try
        {
            await _confirmations.ConfirmAsync(eventId, actorId, "tap");
            TempData["TimelineNote"] = "Confirmed.";
        }
        catch (InvalidOperationException)
        {
            // A second click on an entry that has already moved on:
            // a plain note, never the error page.
            TempData["TimelineNote"] = "Already confirmed.";
        }
        return RedirectToPage(new { ActorId = actorId });
    }

    public async Task<IActionResult> OnPostDeclineAsync(int eventId, int actorId)
    {
        try
        {
            await _confirmations.DeclineAsync(eventId, actorId, "tap");
            TempData["TimelineNote"] = "Not sent.";
        }
        catch (InvalidOperationException)
        {
            TempData["TimelineNote"] = "Already confirmed.";
        }
        return RedirectToPage(new { ActorId = actorId });
    }

    public async Task<IActionResult> OnPostCloseAsync(int eventId, int actorId)
    {
        try
        {
            await _confirmations.CloseRecordPendingAsync(eventId, actorId, "tap");
            TempData["TimelineNote"] = "Confirmed.";
        }
        catch (InvalidOperationException)
        {
            TempData["TimelineNote"] = "Already confirmed.";
        }
        return RedirectToPage(new { ActorId = actorId });
    }

    // The state words for one entry: a settled state names the
    // person who acted and the household time they acted at, read
    // from the entry's audit rows; a pending state is the one phrase
    // every surface uses.
    private readonly Dictionary<int, (string Actor, DateTime AtUtc)> _settledBy = new();

    public string StateText(Event item)
    {
        if (item.ConfirmationState == ConfirmationState.Proposed)
        {
            return ConfirmationWording.For(item.ConfirmationState);
        }
        return _settledBy.TryGetValue(item.Id, out var settled)
            ? ConfirmationWording.For(item.ConfirmationState,
                NameWording.First(settled.Actor),
                HouseholdTime.Time(settled.AtUtc, HouseholdZone))
            : ConfirmationWording.For(item.ConfirmationState);
    }

    // A supply item's name as the household says it, capitalized for
    // the table, and its count with the unit pluralized to match.
    public string SupplyName(SupplyItem item) =>
        item.Name.Length == 0
            ? item.Name
            : char.ToUpperInvariant(item.Name[0]) + item.Name[1..];

    public string HandText(SupplyItem item)
    {
        var quantity = item.QuantityOnHand.ToString("0.#");
        var unit = item.QuantityOnHand == 1m ? item.Unit : item.Unit + "s";
        return $"{quantity} {unit}";
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

        // Household view means the house entries only: the landing
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

        // The audit rows behind the listed entries' settled states,
        // so the status column can name who acted and when.
        var listedIds = Events.Select(e => e.Id).ToList();
        var audits = await _db.EventAudits
            .Include(a => a.ActorPerson)
            .Where(a => a.ObjectKind == "Event" && listedIds.Contains(a.EventId))
            .OrderBy(a => a.AtUtc)
            .ToListAsync();
        foreach (var audit in audits)
        {
            if (audit.ActorPerson is not null
                && (audit.ToState == nameof(ConfirmationState.Confirmed)
                    || audit.ToState == nameof(ConfirmationState.Done)))
            {
                _settledBy[audit.EventId] = (audit.ActorPerson.DisplayName, audit.AtUtc);
            }
        }

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
