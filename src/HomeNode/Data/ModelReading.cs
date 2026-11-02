namespace HomeNode.Data;

// One message read by the extraction pipeline, kept so the house's
// surfaces can show what the model did with it: which engine read the
// words, how long the reading took, what the engine returned, and
// which record entries came of it. Written once, when the message is
// read; never changed afterwards. What happened to the entries next
// (a person confirmed, a delivery went out, nobody answered) is read
// from the record's own rows at display time, never copied here.
//
// What a row keeps depends on what the message became:
//
//   A message that produced record entries keeps its words, the
//   speaker, the engine's structured output, and the house's reply.
//   That content is on the record already, and every surface shows a
//   reading only to a reader every one of its entries' stored
//   audiences admits (ActivityService), so the row widens nothing.
//
//   A message that produced no entries (a question, a request to
//   clarify, a reading the house's checks refused, or nothing at
//   all) keeps no words, no output, no reply, and no speaker. Such a
//   message leaves nothing on the record by design, and this row is
//   counted telemetry only: the engine, the time it took, and the
//   kind of outcome.
//
// The table carries no foreign keys on purpose: a restored snapshot
// that predates it gets the table from the startup compatibility
// step (Program.cs) with no other schema change.
public class ModelReading
{
    public int Id { get; set; }
    public int HouseholdId { get; set; }
    public string UtteranceId { get; set; } = "";

    // The input source type the pipeline received: "tap" for Home,
    // "federated" for a member's assistant over the sealed relay.
    public string Channel { get; set; } = "";

    // Which engine produced the structured output: "live" (Muse
    // Spark over the Meta Model API), "mock" (the deterministic local
    // stand-in), "rule" (the stated-turn pre-pass, which runs before
    // any engine and answers whose-turn messages itself), or the
    // name another extraction client gives itself.
    public string Engine { get; set; } = "";

    // The model that served the call, as the API reported it, or the
    // configured model id; null when no model was called.
    public string? Model { get; set; }
    public int ElapsedMs { get; set; }
    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }

    // What the message became: Entries, Answered, Clarify, Refused,
    // or Silent. See ModelReadingOutcome.
    public string Outcome { get; set; } = "";

    // Content, kept only when Outcome is Entries (see above).
    public int? SpeakerPersonId { get; set; }
    public string? Words { get; set; }
    public string? OutputJson { get; set; }
    public string? Reply { get; set; }

    // The record entries written from this message, as a JSON array
    // of event ids in the order the engine returned them.
    public string? EventIds { get; set; }
    public int EventCount { get; set; }

    // What the engine was given to read the words against, as counts
    // only (people, supplies, schedules, recent entries the speaker
    // may see): never the content of the slice itself.
    public string? ContextSummary { get; set; }

    // The record's clock for this message: the same instant its
    // entries carry as CreatedAtUtc.
    public DateTime ReadAtUtc { get; set; }
}

public static class ModelReadingOutcome
{
    public const string Entries = "Entries";
    public const string Answered = "Answered";
    public const string Clarify = "Clarify";
    public const string Refused = "Refused";
    public const string Silent = "Silent";
}

public static class ModelReadingEngine
{
    public const string Live = "live";
    public const string Mock = "mock";
    public const string Rule = "rule";
    // Read by the house's own deterministic reader because the message
    // was private (the Private switch, or health-shaped words): no
    // model saw it. See PrivacyRule.
    public const string Private = "private";
}
