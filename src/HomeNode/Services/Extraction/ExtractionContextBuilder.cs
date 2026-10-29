using System.Text.Json.Nodes;
using HomeNode.Data;
using Microsoft.EntityFrameworkCore;

namespace HomeNode.Services.Extraction;

// Builds the extraction context slice from the real
// tables: persons and their roles, the household's route defaults, the
// alert levels in the household's own words, the schedules a person set,
// glossary terms, supply items with their aliases, and the recent record
// the lookup path may read. The pipeline sees only this slice;
// what is not in the tables does not exist for it. Person ids in
// the slice are the database's ids, so a run against a different seed
// resolves the same names to that seed's rows.
public class ExtractionContextBuilder
{
    private readonly AppDbContext _db;

    public ExtractionContextBuilder(AppDbContext db)
    {
        _db = db;
    }

    public async Task<ExtractionContext> BuildAsync(int householdId, int recentEventCount = 20)
    {
        var household = await _db.Households.FirstAsync(h => h.Id == householdId);
        var persons = await _db.Persons
            .Where(p => p.HouseholdId == householdId)
            .OrderBy(p => p.Id)
            .ToListAsync();
        var levels = await _db.AlertLevels
            .Where(l => l.HouseholdId == householdId)
            .OrderBy(l => l.Id)
            .ToListAsync();
        var schedules = await _db.HouseholdSchedules
            .Where(s => s.HouseholdId == householdId)
            .OrderBy(s => s.Id)
            .ToListAsync();
        var glossary = await _db.GlossaryTerms
            .Where(g => g.HouseholdId == householdId)
            .OrderBy(g => g.Id)
            .ToListAsync();
        var supplies = await _db.SupplyItems
            .Where(s => s.HouseholdId == householdId)
            .OrderBy(s => s.Id)
            .ToListAsync();
        var recent = await _db.Events
            .Where(e => e.HouseholdId == householdId)
            .OrderByDescending(e => e.CreatedAtUtc)
            .Take(recentEventCount)
            .OrderBy(e => e.CreatedAtUtc)
            .ToListAsync();

        return new ExtractionContext
        {
            HouseholdId = householdId,
            Persons = persons.Select(p => new ExtractionSlicePerson
            {
                PersonId = p.Id,
                DisplayName = p.DisplayName,
                FirstName = p.DisplayName.Split(' ')[0],
                Role = p.Role.ToString(),
                MinorFlag = p.MinorFlag
            }).ToList(),
            DefaultRoutePersonId = household.DefaultRoutePersonId,
            BackupRoutePersonId = household.BackupRoutePersonId,
            AlertLevels = levels.Select(l => new ExtractionSliceLevel
            {
                LevelId = l.LevelKey,
                Label = l.Label,
                RouteToPersonId = l.RouteToPersonId,
                BackupPersonId = l.BackupPersonId
            }).ToList(),
            HealthMentionLevelId = household.HealthMentionLevelKey,
            SupplyItems = supplies.Select(s => new ExtractionSliceSupply
            {
                SupplyItemId = s.Id,
                Name = s.Name,
                Aliases = ParseAliases(s)
            }).ToList(),
            Schedules = schedules.Select(s => new ExtractionSliceSchedule
            {
                ScheduleId = s.Id,
                Label = s.Label,
                SetByPersonId = s.SetByPersonId,
                TimeLocal = s.TimeLocal
            }).ToList(),
            RecentEvents = recent.Select(ToExtractionEvent).ToList(),
            Glossary = glossary.Select(g => new ExtractionSliceGlossary
            {
                Term = g.Term,
                Meaning = g.Meaning
            }).ToList(),
            OpenPrompt = null
        };
    }

    public static List<string> ParseAliases(SupplyItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Aliases))
        {
            return new List<string>();
        }
        try
        {
            var node = JsonNode.Parse(item.Aliases);
            if (node is JsonArray array)
            {
                return array.Select(n => n?.GetValue<string>() ?? "").Where(s => s.Length > 0).ToList();
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // A malformed alias list resolves nothing rather than guessing.
        }
        return new List<string>();
    }

    public static ExtractionEvent ToExtractionEvent(Event ev)
    {
        JsonObject details = new();
        if (!string.IsNullOrWhiteSpace(ev.Details))
        {
            try
            {
                if (JsonNode.Parse(ev.Details) is JsonObject parsed)
                {
                    details = parsed;
                }
            }
            catch (System.Text.Json.JsonException)
            {
                details = new JsonObject();
            }
        }
        return new ExtractionEvent
        {
            Type = ev.Type.ToString(),
            Body = ev.Body,
            SubjectPersonId = ev.SubjectPersonId,
            AuthorPersonId = ev.AuthorPersonId,
            ConfirmationState = ev.ConfirmationState.ToString(),
            SensitivityClass = ev.SensitivityClass.ToString(),
            AudienceRule = ev.AudienceRule,
            RelatedSupplyItemId = ev.RelatedSupplyItemId,
            Details = details
        };
    }
}
