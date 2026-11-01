using System.Text.RegularExpressions;
using HomeNode.Data;
using HomeNode.Services.Extraction;
using Microsoft.EntityFrameworkCore;

namespace HomeNode.Services;

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

    // The same lookup in parts, for a surface that renders the answer
    // in its own layout (name, household time, the person's words).
    // Null when the record holds no answer the asker may hear; a
    // refusal and an absence return the same null, exactly as the
    // spoken form returns the same sentence for both.
    public async Task<(string FirstName, DateTime StatedLocal, string Body)?> AnswerWhereaboutsPartsAsync(
        int askerPersonId, int subjectPersonId)
    {
        var subject = await _db.Persons.FirstOrDefaultAsync(p => p.Id == subjectPersonId);
        if (subject is null)
        {
            return null;
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
            var household = await _db.Households.FirstAsync(h => h.Id == subject.HouseholdId);
            var tz = TimeZoneInfo.FindSystemTimeZoneById(household.TimeZoneId ?? "America/Chicago");
            var stated = TimeZoneInfo.ConvertTimeFromUtc(ev.CreatedAtUtc, tz);
            return (subject.DisplayName.Split(' ')[0], stated, ev.Body);
        }
        return null;
    }

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
