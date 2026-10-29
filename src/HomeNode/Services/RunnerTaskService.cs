using HomeNode.Data;
using HomeNode.Services.Extraction;
using Microsoft.EntityFrameworkCore;

namespace HomeNode.Services;

// One task as its named runner may see it (the runner rule):
// the task's own basic details only, projected at read time from
// the task event's structured fields: what, when, where, the
// subject's name, the author's name. The projection never carries
// the underlying telling's body and never carries its audience
// list, and it is not a widening: the event's stored class and
// audience rule are neither read into the line nor changed by it.
public record RunnerTaskProjection(
    int TaskEventId,
    int PlanId,
    string What,
    string? When,
    string? Where,
    string? SubjectName,
    string? AuthorName,
    bool Done)
{
    // The brief line, composed from the projected fields and
    // nothing else: the words are built here, never copied from
    // the telling.
    public string Line
    {
        get
        {
            var core = What;
            if (When is not null)
            {
                core += $", {When}";
            }
            if (Where is not null)
            {
                core += $" at {Where}";
            }
            var sentences = new List<string> { core + "." };
            if (SubjectName is not null && !What.Contains(SubjectName, StringComparison.OrdinalIgnoreCase))
            {
                sentences.Add($"For {SubjectName}.");
            }
            if (AuthorName is not null)
            {
                sentences.Add($"Told by {AuthorName}.");
            }
            return string.Join(" ", sentences);
        }
    }

    public static RunnerTaskProjection Build(Event taskEvent, Person? subject, Person? author)
    {
        var taskText = ExtractionDetailsReader.GetString(taskEvent.Details, "TaskText");
        var kind = ExtractionDetailsReader.GetString(taskEvent.Details, "TaskKind");
        var subjectFirst = subject?.DisplayName.Split(' ')[0];
        var what = !string.IsNullOrWhiteSpace(taskText)
            ? taskText
            : kind switch
            {
                "Pickup" => subjectFirst is not null ? $"Pick {subjectFirst} up" : "A pickup",
                "Errand" => "An errand",
                _ => "A task"
            };
        if (kind == "Pickup" && subjectFirst is not null
            && !what.Contains(subjectFirst, StringComparison.OrdinalIgnoreCase))
        {
            // A pickup's stored text can be a bare gerund ("picking
            // up"); the basic detail a runner needs is who is being
            // picked up, which the task's subject field carries.
            what = $"Pick {subjectFirst} up";
        }
        return new RunnerTaskProjection(
            taskEvent.Id,
            0,
            what,
            ExtractionDetailsReader.GetString(taskEvent.Details, "DueText")
                ?? ExtractionDetailsReader.GetString(taskEvent.Details, "WhenText"),
            ExtractionDetailsReader.GetString(taskEvent.Details, "PlaceText"),
            subjectFirst,
            author?.DisplayName.Split(' ')[0],
            taskEvent.ConfirmationState == ConfirmationState.Done);
    }

    // Every task projected to one person: the tasks sourced into
    // elements of a confirmed plan (Acknowledged or Completed)
    // whose named runner this person currently is. The binding is
    // read from the plan's current runner, so a reassignment moves
    // the view on the next read: the former runner's projection
    // is gone, the new runner's is there.
    public static async Task<List<RunnerTaskProjection>> ProjectedForAsync(AppDbContext db, int personId)
    {
        var plans = await db.Plans
            .Include(p => p.Elements)
            .Where(p => p.RunnerPersonId == personId
                && (p.State == PlanState.Acknowledged || p.State == PlanState.Completed))
            .OrderBy(p => p.Id)
            .ToListAsync();
        var projections = new List<RunnerTaskProjection>();
        foreach (var plan in plans)
        {
            foreach (var element in plan.Elements
                .Where(e => e.State != PlanElementState.Dropped)
                .OrderBy(e => e.Id))
            {
                foreach (var sourceId in PlanAssemblyService.SourceEventIds(element))
                {
                    var taskEvent = await db.Events
                        .Include(e => e.SubjectPerson)
                        .Include(e => e.AuthorPerson)
                        .FirstOrDefaultAsync(e => e.Id == sourceId && e.Type == EventType.Task);
                    if (taskEvent is null)
                    {
                        continue;
                    }
                    projections.Add(
                        Build(taskEvent, taskEvent.SubjectPerson, taskEvent.AuthorPerson)
                            with { PlanId = plan.Id });
                }
            }
        }
        // One projection per task, however many elements or plans
        // source it: the runner's view of a task is the task.
        return projections
            .GroupBy(p => p.TaskEventId)
            .Select(g => g.First())
            .ToList();
    }
}

// The runner side of a task: who may complete it, and the
// completion itself. Completion is the runner's act alone: a
// person inside the telling's audience who is not the named
// runner cannot complete the task, and the runner completes it
// without ever opening the telling. The completion moves the
// task event from Confirmed to Done and writes exactly one audit
// row naming the runner as its actor, through the runner channel.
public class RunnerTaskService
{
    private readonly AppDbContext _db;

    public RunnerTaskService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<List<RunnerTaskProjection>> ProjectedTasksForAsync(int personId) =>
        await RunnerTaskProjection.ProjectedForAsync(_db, personId);

    public async Task CompleteTaskAsync(int taskEventId, int personId)
    {
        var taskEvent = await _db.Events.FirstOrDefaultAsync(e => e.Id == taskEventId)
            ?? throw new InvalidOperationException($"Event {taskEventId} does not exist.");
        if (taskEvent.Type != EventType.Task)
        {
            throw new InvalidOperationException($"Event {taskEventId} is not a task.");
        }
        var bound = await IsNamedRunnerAsync(taskEventId, personId);
        if (!bound)
        {
            throw new InvalidOperationException(
                "Only the task's named runner can complete it.");
        }
        if (taskEvent.ConfirmationState != ConfirmationState.Confirmed)
        {
            throw new InvalidOperationException(
                $"Task {taskEventId} is {taskEvent.ConfirmationState}; only a confirmed task can be completed by its runner.");
        }
        taskEvent.ConfirmationState = ConfirmationState.Done;
        _db.EventAudits.Add(new EventAudit
        {
            ObjectKind = "Event",
            EventId = taskEvent.Id,
            FromState = ConfirmationState.Confirmed,
            ToState = nameof(ConfirmationState.Done),
            ActorPersonId = personId,
            Channel = "runner",
            AtUtc = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
    }

    // Whether this person is the named runner of a confirmed plan
    // carrying an element sourced from this task event: the one
    // binding that gives a person the runner's view and the
    // runner's completion.
    public async Task<bool> IsNamedRunnerAsync(int taskEventId, int personId)
    {
        var plans = await _db.Plans
            .Include(p => p.Elements)
            .Where(p => p.RunnerPersonId == personId
                && (p.State == PlanState.Acknowledged || p.State == PlanState.Completed))
            .ToListAsync();
        return plans.Any(p => p.Elements.Any(e =>
            e.State != PlanElementState.Dropped
            && PlanAssemblyService.SourceEventIds(e).Contains(taskEventId)));
    }
}
