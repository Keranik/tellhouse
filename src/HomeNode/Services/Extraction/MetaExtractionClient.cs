using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace HomeNode.Services.Extraction;

public class ExtractionOptions
{
    // "Mock" (default) or "Live". The demo and the demo suite run on
    // the mock; Live is for genuinely ready runs only.
    public string Mode { get; set; } = "Mock";

    // The extraction role's model. Standard muse-spark-1.3 for
    // anything demo-facing or public; the Contributor model id is
    // for test runs with fictional data only.
    public string Model { get; set; } = "muse-spark-1.3";

    // The reserved clarification role (Muse Wonder). Declared here so
    // the seam names it; this build never calls it.
    public string ClarificationModel { get; set; } = "muse-wonder-1.0";
}

// The live path behind the model-role seam: Muse Spark over the Meta
// Model API, strict structured output against the extraction schema.
// The credential comes from the MODEL_API_KEY environment
// variable only; its value is never logged.
public class MetaExtractionClient : IExtractionClient
{
    private const string ApiUrl = "https://api.meta.ai/v1/chat/completions";

    private readonly ExtractionOptions _options;
    private readonly ILogger<MetaExtractionClient> _logger;

    public MetaExtractionClient(IOptions<ExtractionOptions> options, ILogger<MetaExtractionClient> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public string Name => "live";

    public async Task<ExtractionOutput> ExtractAsync(ExtractionInput input, CancellationToken ct = default)
    {
        var bearer = ResolveBearer()
            ?? throw new InvalidOperationException(
                "No Meta Model API credential is available: MODEL_API_KEY is not set.");

        var body = new JsonObject
        {
            ["model"] = _options.Model,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = ExtractionPrompt.System },
                new JsonObject { ["role"] = "user", ["content"] = ExtractionJson.ToJsonObject(input).ToJsonString() }
            },
            ["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject
                {
                    ["name"] = "extraction_output",
                    ["strict"] = true,
                    // A deep clone per request: a JsonNode can have only
                    // one parent, so assigning the one static schema into
                    // a second request body throws "The node already has
                    // a parent" and every live telling after the first in
                    // a process failed before any API call was made.
                    ["schema"] = ExtractionSchema.Value.DeepClone()
                }
            },
            ["reasoning_effort"] = "low",
            // Measured against the live model: its reasoning consumed
            // 510 of 512, then 1860 of 2048, completion tokens before
            // any JSON was emitted. 4096 is where every test case
            // completed.
            ["max_tokens"] = 4096,
            ["temperature"] = 0
        };

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(300) };
        using var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            // Status and a body excerpt only; the body of an error
            // response carries no credential, and none is ever added.
            throw new InvalidOperationException(
                $"Meta Model API returned {(int)response.StatusCode}: {text[..Math.Min(300, text.Length)]}");
        }
        using var doc = JsonDocument.Parse(text);
        var content = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString()
            ?? throw new InvalidOperationException("The model returned no content.");
        _logger.LogInformation("Live extraction completed for {UtteranceId} on {Model}", input.UtteranceId, _options.Model);
        return ExtractionJson.FromJson<ExtractionOutput>(content);
    }

    private static string? ResolveBearer()
    {
        var fromEnv = Environment.GetEnvironmentVariable("MODEL_API_KEY");
        return string.IsNullOrWhiteSpace(fromEnv) ? null : fromEnv.Trim();
    }
}

// The strict output schema for the live path, including the
// Commitment and Task types and their Details keys. Strict form:
// every object lists all of its keys and forbids others; unused
// keys are null.
public static class ExtractionSchema
{
    public static readonly JsonObject Value = Build();

    private static JsonObject Obj(Dictionary<string, JsonNode?> properties, bool required = true)
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject(properties.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, kv.Value))),
            ["additionalProperties"] = false
        };
        if (required)
        {
            schema["required"] = new JsonArray(properties.Keys.Select(k => (JsonNode?)JsonValue.Create(k)).ToArray());
        }
        return schema;
    }

    private static JsonObject Str() => new() { ["type"] = "string" };
    private static JsonObject StrEnum(params string[] values) =>
        new() { ["type"] = "string", ["enum"] = new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()) };
    private static JsonObject Nullable(JsonObject inner) =>
        new() { ["anyOf"] = new JsonArray { inner, new JsonObject { ["type"] = "null" } } };
    private static JsonObject Int() => new() { ["type"] = "integer" };
    private static JsonObject Bool() => new() { ["type"] = "boolean" };

    private static JsonObject Build()
    {
        var scheduleRef = Obj(new()
        {
            ["ScheduleId"] = Int(),
            ["TimeLocal"] = Str(),
            ["SetByPersonId"] = Int()
        });

        var details = Obj(new()
        {
            ["RequestKind"] = Nullable(StrEnum("Relay", "Media", "Reminder", "Bring", "Other")),
            ["TargetPersonId"] = Nullable(Int()),
            ["MedMention"] = Nullable(StrEnum("None", "Casual", "ScheduleStatement", "SelfReport")),
            ["ScheduleRef"] = Nullable(scheduleRef),
            ["Direction"] = Nullable(StrEnum("Low", "Out", "Used", "Restocked")),
            ["QuantityText"] = Nullable(Str()),
            ["UnresolvedItem"] = Nullable(Str()),
            ["EntryKind"] = Nullable(StrEnum("PromptAnswer", "FreeEntry")),
            ["PromptId"] = Nullable(Str()),
            ["SourceName"] = Nullable(Str()),
            ["ChangeText"] = Nullable(Str()),
            ["PlaceText"] = Nullable(Str()),
            ["Motion"] = Nullable(StrEnum("At", "HeadingTo", "HeadingHome", "Leaving", "Arrived", "Home")),
            ["NextText"] = Nullable(Str()),
            ["Form"] = Nullable(StrEnum("Statement", "Question")),
            ["InputMode"] = Nullable(Str()),
            ["DecisionTopic"] = Nullable(Str()),
            ["DecisionState"] = Nullable(StrEnum("Undecided", "Decided")),
            ["DecidedText"] = Nullable(Str()),
            ["CommitmentText"] = Nullable(Str()),
            ["DueText"] = Nullable(Str()),
            ["DueAtLocal"] = Nullable(Str()),
            ["PromiseePersonId"] = Nullable(Int()),
            ["TaskKind"] = Nullable(StrEnum("General", "Pickup", "Errand")),
            ["TaskText"] = Nullable(Str()),
            ["WhenText"] = Nullable(Str()),
            ["CollectPersonId"] = Nullable(Int()),
            ["HelpFlag"] = Nullable(Bool()),
            ["NeedsAssignee"] = Nullable(Bool())
        });

        var statedMark = Obj(new()
        {
            ["Kind"] = StrEnum("Personal", "Household", "Named", "Unclear"),
            ["PersonIds"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = Int()
            },
            ["Clause"] = Str()
        });

        var ev = Obj(new()
        {
            ["Type"] = StrEnum("Request", "HouseholdNote", "Supply", "Journal", "FacilityNotice", "Whereabouts", "HealthMention", "Commitment", "Task"),
            ["Body"] = Str(),
            ["SubjectPersonId"] = Nullable(Int()),
            ["AuthorPersonId"] = Nullable(Int()),
            ["ConfirmationState"] = StrEnum("Proposed", "Confirmed", "Done", "NotConfirmed"),
            ["RelatedSupplyItemId"] = Nullable(Int()),
            ["Details"] = details,
            ["StatedMarks"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = statedMark
            }
        });

        var payload = Obj(new()
        {
            ["MessageText"] = Nullable(Str()),
            ["WhatText"] = Nullable(Str()),
            ["AtLocal"] = Nullable(Str()),
            ["RemindOnText"] = Nullable(Str()),
            ["TaskText"] = Nullable(Str()),
            ["AboutEventIndex"] = Nullable(Int())
        });

        var action = Obj(new()
        {
            ["ActionType"] = StrEnum("NotifyPerson", "RelayMessage", "CreateReminder", "MediaPlay"),
            ["TargetPersonId"] = Nullable(Int()),
            ["EventIndex"] = Int(),
            ["Payload"] = payload
        });

        var alert = Obj(new()
        {
            ["Fire"] = Bool(),
            ["FiresOn"] = StrEnum("Never", "Confirmation"),
            ["LevelId"] = Nullable(Str()),
            ["RouteToPersonId"] = Nullable(Int()),
            ["BackupPersonId"] = Nullable(Int()),
            ["Reason"] = Str()
        });

        return Obj(new()
        {
            ["UtteranceId"] = Str(),
            ["Events"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = ev
            },
            ["ProposedAction"] = Nullable(action),
            ["ConfirmationPrompt"] = Nullable(Str()),
            ["SpokenResponse"] = Nullable(Str()),
            ["AlertDecision"] = alert,
            ["ClarificationNeeded"] = Bool()
        });
    }
}
