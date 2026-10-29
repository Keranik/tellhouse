using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace HouseholdCoordinator.Services.Extraction;

// The extraction pipeline's JSON contract. The event cap is five
// events with the fold rule (overflow folds into the fifth event,
// with a fold note), Commitment and Task are event types, and
// HouseholdNote carries DecisionTopic and DecisionState keys.
// Deliberately NOT in this contract: four further spoken templates
// that were considered and not adopted, and any rules for input
// arriving from outside the house. These DTOs are the one shape
// both pipeline clients (live API and local mock) speak, and the
// shape the validator checks.

public class ExtractionPersonRef
{
    public int PersonId { get; set; }
    public string DisplayName { get; set; } = "";
    public string Role { get; set; } = "";
}

public class ExtractionSlicePerson
{
    public int PersonId { get; set; }
    public string DisplayName { get; set; } = "";
    public string FirstName { get; set; } = "";
    public string Role { get; set; } = "";
    public bool MinorFlag { get; set; }
}

public class ExtractionSliceLevel
{
    public string LevelId { get; set; } = "";
    public string Label { get; set; } = "";
    public int? RouteToPersonId { get; set; }
    public int? BackupPersonId { get; set; }
}

public class ExtractionSliceSupply
{
    public int SupplyItemId { get; set; }
    public string Name { get; set; } = "";
    public List<string> Aliases { get; set; } = new();
}

public class ExtractionSliceSchedule
{
    public int ScheduleId { get; set; }
    public string Label { get; set; } = "";
    public int SetByPersonId { get; set; }
    public string TimeLocal { get; set; } = "";
}

public class ExtractionSliceGlossary
{
    public string Term { get; set; } = "";
    public string Meaning { get; set; } = "";
}

public class ExtractionOpenPrompt
{
    public string Kind { get; set; } = "";
    public string? PromptId { get; set; }
    public string? Text { get; set; }
}

public class ExtractionContext
{
    public int HouseholdId { get; set; }
    public List<ExtractionSlicePerson> Persons { get; set; } = new();
    public int? DefaultRoutePersonId { get; set; }
    public int? BackupRoutePersonId { get; set; }
    public List<ExtractionSliceLevel> AlertLevels { get; set; } = new();
    public string? HealthMentionLevelId { get; set; }
    public List<ExtractionSliceSupply> SupplyItems { get; set; } = new();
    public List<ExtractionSliceSchedule> Schedules { get; set; } = new();
    public List<ExtractionEvent> RecentEvents { get; set; } = new();
    public List<ExtractionSliceGlossary> Glossary { get; set; } = new();
    public ExtractionOpenPrompt? OpenPrompt { get; set; }
}

public class ExtractionInput
{
    public string UtteranceId { get; set; } = "";
    public string Transcript { get; set; } = "";
    public string SourceType { get; set; } = "voice";
    public DateTimeOffset OccurredAtLocal { get; set; }
    public ExtractionPersonRef? Speaker { get; set; }
    public ExtractionContext Context { get; set; } = new();
}

public class ExtractionEvent
{
    public string Type { get; set; } = "";
    public string Body { get; set; } = "";
    public int? SubjectPersonId { get; set; }
    public int? AuthorPersonId { get; set; }
    public string ConfirmationState { get; set; } = "Proposed";
    public int? RelatedSupplyItemId { get; set; }
    public JsonObject Details { get; set; } = new();
}

public class ExtractionAction
{
    public string ActionType { get; set; } = "";
    public int? TargetPersonId { get; set; }
    public int EventIndex { get; set; }
    public JsonObject Payload { get; set; } = new();
}

public class ExtractionAlertDecision
{
    public bool Fire { get; set; }
    public string FiresOn { get; set; } = "Never";
    public string? LevelId { get; set; }
    public int? RouteToPersonId { get; set; }
    public int? BackupPersonId { get; set; }
    public string Reason { get; set; } = "NoActionProposed";

    public static ExtractionAlertDecision None() => new();
}

public class ExtractionOutput
{
    public string UtteranceId { get; set; } = "";
    public List<ExtractionEvent> Events { get; set; } = new();
    public ExtractionAction? ProposedAction { get; set; }
    public string? ConfirmationPrompt { get; set; }
    public string? SpokenResponse { get; set; }
    public ExtractionAlertDecision AlertDecision { get; set; } = ExtractionAlertDecision.None();
    public bool ClarificationNeeded { get; set; }
}

public static class ExtractionJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static JsonObject ToJsonObject<T>(T value) =>
        JsonSerializer.SerializeToNode(value, Options)!.AsObject();

    public static T FromJson<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options)
        ?? throw new InvalidOperationException($"Could not parse {typeof(T).Name} from JSON.");
}
