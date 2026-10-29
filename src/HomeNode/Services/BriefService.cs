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
                    ? " (waiting on a person's answer)"
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
                Section = "Promises still open",
                Text = $"{maker} promised: {promise.CommitmentText}.{due}"
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
    // the audience decision (spec C.2.1) stores when a stated mark, a
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
