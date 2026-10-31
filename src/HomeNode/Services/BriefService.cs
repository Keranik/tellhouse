using System.Text.Json;
using HomeNode.Data;
using HomeNode.Services.Extraction;
using Microsoft.EntityFrameworkCore;

namespace HomeNode.Services;

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
//
// One section is not audience-filtered record content: "Tasks to
// run", the runner rule. A task's named runner sees the
// task's own basic details (what, when, where, the subject's and
// the author's names) projected from the task's structured
// fields, because they are the person who must run it. The
// projection is read at brief time from the plan's current
// runner binding; it never carries the telling's body or its
// audience list, and it neither reads nor changes the stored
// audience: the telling itself stays exactly as fenced as it
// was, in this brief and everywhere else.
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
                    ? " (waiting for a person to confirm)"
                    : "";
            lines.Add(new BriefLine
            {
                Section = "From the record",
                Text = $"{who}: {ev.Body}{stateNote}"
            });
        }

        // The runner's tasks: basic details only, projected
        // from the task fields, for the person the plan names as
        // runner. A task already run drops out of the brief; it
        // stays on the record as Done.
        var runnerTasks = await RunnerTaskProjection.ProjectedForAsync(_db, personId);
        foreach (var task in runnerTasks.Where(t => !t.Done))
        {
            lines.Add(new BriefLine
            {
                Section = "Tasks to run",
                Text = task.Line
            });
        }

        // A promise's due time is a household wall clock
        // time (DueAtLocal), so "now" for the past-due wording is
        // the household's local time, converted from UTC through
        // the household's own zone. The server clock (UTC in the
        // deployed containers) used to decide this, and at 12:54 PM
        // Chicago the brief already called a 2:00 PM promise past.
        var household = await _db.Households.FirstOrDefaultAsync(h => h.Id == person.HouseholdId);
        TimeZoneInfo householdZone;
        try
        {
            householdZone = TimeZoneInfo.FindSystemTimeZoneById(household?.TimeZoneId ?? "America/Chicago");
        }
        catch (TimeZoneNotFoundException)
        {
            householdZone = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
        }
        var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, householdZone);
        var promises = await _db.Promises
            .Include(p => p.PromisorPerson)
            .Include(p => p.PromiseePerson)
            .Where(p => p.HouseholdId == person.HouseholdId && p.State == PromiseState.Made)
            .ToListAsync();
        var promiseSources = await _db.Events
            .Where(e => promises.Select(p => p.SourceEventId).Contains(e.Id))
            .ToDictionaryAsync(e => e.Id);
        foreach (var promise in promises.Where(p => PromiseVisibleTo(
                     p, promiseSources.GetValueOrDefault(p.SourceEventId), personId)))
        {
            var maker = promise.PromisorPersonId == personId ? "You" : promise.PromisorPerson?.DisplayName.Split(' ')[0];
            var due = promise.DueAtLocal is { } dueAt && dueAt < now
                ? $" Still open; it was for {dueAt:dddd, MMM d, h:mm tt}."
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

    public static bool IsVisibleTo(Event ev, int personId) =>
        IsVisibleTo(ev.SensitivityClass, ev.AudienceRule, ev.SubjectPersonId, ev.AuthorPersonId, personId);

    // The same stored-audience read for an event in the extraction
    // slice, where the class and rule travel in string form. The
    // class name is parsed back to the stored class and the one
    // rule below decides; a slice event whose class cannot be
    // determined is visible to nobody, failing closed exactly as
    // the record surfaces do.
    public static bool IsVisibleTo(ExtractionEvent ev, int personId) =>
        Enum.TryParse<SensitivityClass>(ev.SensitivityClass, out var storedClass)
        && IsVisibleTo(storedClass, ev.AudienceRule, ev.SubjectPersonId, ev.AuthorPersonId, personId);

    // The no-reader rule for a slice event, the same rule the front
    // door's recent list applies when no reader is named: only the
    // house's shared entries (Open and Household classes) are
    // visible at all, and an undeterminable class shows to nobody.
    public static bool IsVisibleWithNoReader(ExtractionEvent ev) =>
        Enum.TryParse<SensitivityClass>(ev.SensitivityClass, out var storedClass)
        && storedClass is SensitivityClass.Open or SensitivityClass.Household;

    private static bool IsVisibleTo(
        SensitivityClass sensitivityClass, string? audienceRule,
        int? subjectPersonId, int? authorPersonId, int personId)
    {
        switch (sensitivityClass)
        {
            case SensitivityClass.Open:
            case SensitivityClass.Household:
                return true;
            case SensitivityClass.Personal:
                return subjectPersonId == personId
                    || RuleNamesPerson(audienceRule, personId);
            case SensitivityClass.Restricted:
            case SensitivityClass.Named:
            {
                if (subjectPersonId == personId || authorPersonId == personId)
                {
                    return true;
                }
                return RuleNamesPerson(audienceRule, personId);
            }
            default:
                return false;
        }
    }

    // Whether the stored audience rule names this person: by one of
    // the class rule's person keys, or by the AudiencePersonIds list
    // the audience decision stores when a stated mark, a
    // default, or an ask answer narrows an event to named people.
    // This reads the stored rule only; nothing is recomputed at read
    // time, and no model is ever asked who should see an event
    // (clause 10).
    private static bool RuleNamesPerson(string? audienceRule, int personId)
    {
        if (string.IsNullOrWhiteSpace(audienceRule))
        {
            return false;
        }
        try
        {
            using var doc = JsonDocument.Parse(audienceRule);
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
            if (root.TryGetProperty("AudiencePersonIds", out var ids)
                && ids.ValueKind == JsonValueKind.Array)
            {
                foreach (var id in ids.EnumerateArray())
                {
                    if (id.ValueKind == JsonValueKind.Number && id.GetInt32() == personId)
                    {
                        return true;
                    }
                }
            }
        }
        catch (JsonException)
        {
        }
        return false;
    }

    // A promise's visibility is the stored audience of the telling
    // that borned it: the source event's class and
    // rule, read exactly as every other event is read. A commitment
    // is stored Named to its maker and its promisee, so a promise
    // with no promisee is visible to its maker alone under that
    // rule, never to everyone by virtue of having no promisee. A
    // promise whose source telling was stored Household is visible
    // to the house, because the stored rule says so. A promise
    // whose source event cannot be found fails closed to its maker
    // and its promisee, the narrowest reading its own fields
    // support.
    public static bool PromiseVisibleTo(Promise promise, Event? sourceEvent, int personId)
    {
        if (sourceEvent is not null)
        {
            return IsVisibleTo(sourceEvent, personId);
        }
        return promise.PromisorPersonId == personId
            || promise.PromiseePersonId == personId;
    }

    // The no-reader form of the promise rule, matching the front
    // door's no-reader rule for events: only a promise whose source
    // telling is a shared class (Open or Household) shows at all,
    // and a promise with no source event shows to nobody.
    public static bool PromiseVisibleWithNoReader(Promise promise, Event? sourceEvent) =>
        sourceEvent is not null && IsVisibleWithNoReader(sourceEvent);

    // The no-reader rule for a record event: only the house's
    // shared entries (Open and Household classes) are visible at
    // all when no reader is named.
    public static bool IsVisibleWithNoReader(Event ev) =>
        ev.SensitivityClass is SensitivityClass.Open or SensitivityClass.Household;
}
