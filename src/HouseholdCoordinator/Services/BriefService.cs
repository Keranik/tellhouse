using System.Text.Json;
using HouseholdCoordinator.Data;
using HouseholdCoordinator.Services.Extraction;
using Microsoft.EntityFrameworkCore;

namespace HouseholdCoordinator.Services;

public class BriefLine
{
    public required string Section { get; init; }
    public required string Text { get; init; }
}

// One person's brief, assembled from the record and filtered by the
// audience stored on each event at write time: the audience is
// computed once, when the entry is written, and surfaces
// may only narrow it). A Restricted entry reaches its subject and its
// route person; the backup sees it only when a delivery actually
// hopped, and this brief does not pretend otherwise. A Personal entry
// reaches its subject alone. A Named entry reaches the people it
// names. A supply order line is a task for the person the item is
// assigned to, so it is carried in that person's brief only: a
// medication order line never lands in a brief it was not assigned
// to, and an item assigned to nobody is carried in no brief. Open
// promises past their stated time surface as "still
// open", the neutral words: the house never calls
// a promise broken on a clock.
public class BriefService
{
    private readonly AppDbContext _db;

    public BriefService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<List<BriefLine>> BuildBriefAsync(int personId, DateOnly today)
    {
        var person = await _db.Persons.FirstAsync(p => p.Id == personId);
        var lines = new List<BriefLine>();

        var events = await _db.Events
            .Include(e => e.AuthorPerson)
            .Include(e => e.SubjectPerson)
            .Where(e => e.HouseholdId == person.HouseholdId)
            .OrderBy(e => e.CreatedAtUtc)
            .ToListAsync();

        foreach (var ev in events.Where(e => IsVisibleTo(e, personId)))
        {
            var who = ev.AuthorPersonId == personId
                ? "You said"
                : ev.AuthorPerson is not null
                    ? $"{ev.AuthorPerson.DisplayName.Split(' ')[0]} said"
                    : "Told to the house";
            var stateNote = ev.RecordPending
                ? " (not yet closed by a person)"
                : ev.ConfirmationState == ConfirmationState.Proposed
                    ? " (waiting on a person's answer)"
                    : "";
            lines.Add(new BriefLine
            {
                Section = "From the record",
                Text = $"{who}: {ev.Body}{stateNote}"
            });
        }

        var now = DateTime.Now;
        var promises = await _db.Promises
            .Include(p => p.PromisorPerson)
            .Include(p => p.PromiseePerson)
            .Where(p => p.HouseholdId == person.HouseholdId && p.State == PromiseState.Made)
            .ToListAsync();
        foreach (var promise in promises.Where(p => PromiseVisibleTo(p, personId)))
        {
            var maker = promise.PromisorPersonId == personId ? "You" : promise.PromisorPerson?.DisplayName.Split(' ')[0];
            var due = promise.DueAtLocal is { } dueAt && dueAt < now
                ? $" Still open; it was for {dueAt:h:mm tt}."
                : promise.DueText is not null ? $" For {promise.DueText}." : "";
            lines.Add(new BriefLine
            {
                Section = "Claims still open",
                Text = $"Claimed by {maker}: {promise.CommitmentText}.{due}"
            });
        }

        var supplies = await _db.SupplyItems
            .Where(s => s.HouseholdId == person.HouseholdId && s.AssignedPersonId == personId)
            .ToListAsync();
        foreach (var item in supplies)
        {
            var orderBy = item.GetOrderByDate(today);
            var runOut = item.GetRunOutDate(today);
            if (orderBy != DateOnly.MaxValue && orderBy <= today)
            {
                lines.Add(new BriefLine
                {
                    Section = "Supplies",
                    Text = runOut == DateOnly.MaxValue
                        ? $"Order {item.Name} by {orderBy:dddd, MMM d}."
                        : $"Order {item.Name} by {orderBy:dddd, MMM d} (runs out around {runOut:dddd, MMM d})."
                });
            }
            else if (item.QuantityOnHand <= 0)
            {
                lines.Add(new BriefLine
                {
                    Section = "Supplies",
                    Text = $"{item.Name} is out."
                });
            }
        }

        return lines;
    }

    public static bool IsVisibleTo(Event ev, int personId)
    {
        switch (ev.SensitivityClass)
        {
            case SensitivityClass.Open:
            case SensitivityClass.Household:
                return true;
            case SensitivityClass.Personal:
                return ev.SubjectPersonId == personId;
            case SensitivityClass.Restricted:
            case SensitivityClass.Named:
            {
                if (ev.SubjectPersonId == personId || ev.AuthorPersonId == personId)
                {
                    return true;
                }
                if (string.IsNullOrWhiteSpace(ev.AudienceRule))
                {
                    return false;
                }
                try
                {
                    using var doc = JsonDocument.Parse(ev.AudienceRule);
                    var root = doc.RootElement;
                    foreach (var key in new[] { "RoutePersonId", "MakerPersonId", "PromiseePersonId" })
                    {
                        if (root.TryGetProperty(key, out var value)
                            && value.ValueKind == JsonValueKind.Number
                            && value.GetInt32() == personId)
                        {
                            return true;
                        }
                    }
                }
                catch (JsonException)
                {
                }
                return false;
            }
            default:
                return false;
        }
    }

    private static bool PromiseVisibleTo(Promise promise, int personId)
    {
        // A promise made to the house (no named promisee) is part of the
        // shared record; a named promise belongs to its maker and its
        // promisee.
        return promise.PromiseePersonId is null
            || promise.PromisorPersonId == personId
            || promise.PromiseePersonId == personId;
    }
}
