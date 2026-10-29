using System.Text.RegularExpressions;
using HouseholdCoordinator.Data;
using HouseholdCoordinator.Services.Extraction;
using Microsoft.EntityFrameworkCore;

namespace HouseholdCoordinator.Services;

// The deterministic whereabouts lookup: a person asks the house where
// someone is, and the house answers from the record, naming the stated
// time (a heading answer carries the time it was stated, so nobody
// mistakes an old position for a current one). The fences: health
// mentions are never a
// source, and neither is any entry whose words carry a
// medication-taking instruction; and the stored audience decides who
// may hear an entry at all. A refusal and an absence get the same
// words, so the answer never confirms that something exists behind
// the fence.
public class LookupService
{
    private readonly AppDbContext _db;

    public LookupService(AppDbContext db)
    {
        _db = db;
    }

    private static readonly Regex TakingInstruction = new(
        @"\b(take|skip|stop taking)\b[^.]*\b(pill|pills|ones|medicine|extra)\b",
        RegexOptions.IgnoreCase);

    public async Task<string> AnswerWhereaboutsAsync(int askerPersonId, int subjectPersonId)
    {
        var subject = await _db.Persons.FirstOrDefaultAsync(p => p.Id == subjectPersonId);
        if (subject is null)
        {
            return SpokenTemplates.DoNotKnow;
        }
        var candidates = await _db.Events
            .Where(e => e.HouseholdId == subject.HouseholdId
                        && e.Type == EventType.Whereabouts
                        && e.SubjectPersonId == subjectPersonId
                        && (e.ConfirmationState == ConfirmationState.Confirmed
                            || e.ConfirmationState == ConfirmationState.Done))
            .OrderByDescending(e => e.CreatedAtUtc)
            .ToListAsync();
        foreach (var ev in candidates)
        {
            if (ev.Type == EventType.HealthMention || TakingInstruction.IsMatch(ev.Body))
            {
                continue;
            }
            if (!BriefService.IsVisibleTo(ev, askerPersonId))
            {
                continue;
            }
            var firstName = subject.DisplayName.Split(' ')[0];
            var household = await _db.Households.FirstAsync(h => h.Id == subject.HouseholdId);
            var tz = TimeZoneInfo.FindSystemTimeZoneById(household.TimeZoneId ?? "America/Chicago");
            var stated = TimeZoneInfo.ConvertTimeFromUtc(ev.CreatedAtUtc, tz);
            return $"At {stated:h:mm tt}, {firstName} said: {ev.Body}";
        }
        return SpokenTemplates.DoNotKnow;
    }
}
