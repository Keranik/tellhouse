using System.Text.Json;
using System.Text.Json.Nodes;
using HomeNode.Data;
using Microsoft.EntityFrameworkCore;

namespace HomeNode.Services.Extraction;

public class ExtractionResult
{
    public required ExtractionOutput Output { get; init; }
    public required ExtractionValidation Validation { get; init; }
    public List<Event> WrittenEvents { get; init; } = new();
    public List<Promise> PromisesBorn { get; init; } = new();
    // Audience asks opened by this telling (spec C.2.1 clause 7): the
    // events named are stored at their narrowest audience until the
    // person the house asked answers.
    public List<AudienceAsk> AsksCreated { get; init; } = new();
    public Event? ProposedEvent { get; set; }
    // What the house says back: the confirmation prompt when an action
    // is proposed, otherwise the spoken response. Null means silence.
    public string? HouseReply { get; set; }
}

// Turns one telling into record entries: extraction behind the
// model-role seam, validation against the pipeline invariants, then
// writes through the one confirmation dispatcher, so an utterance can
// never reach the record by a side door. Sensitivity class and
// audience are computed here, once, at write time, by the
// deterministic audience decision (spec C.2.1; see
// AudienceDecisionService), and stored on the event; later surfaces
// may only narrow an audience, never widen it. The model proposes
// shapes and stated audiences; it plays no part in the decision and
// no part at read time (clause 10).
public class ExtractionService
{
    private readonly AppDbContext _db;
    private readonly IExtractionClient _client;
    private readonly ConfirmationService _confirmations;
    private readonly AudienceDecisionService _audience;

    public ExtractionService(AppDbContext db, IExtractionClient client, ConfirmationService confirmations, AudienceDecisionService audience)
    {
        _db = db;
        _client = client;
        _confirmations = confirmations;
        _audience = audience;
    }

    public async Task<ExtractionResult> ProcessAsync(ExtractionInput input, CancellationToken ct = default)
    {
        // Audience fence: the recent-events slice is filtered
        // to what this input's speaker may see before anything else
        // reads it: before the pre-pass, before validation, and
        // before any extraction client, mock or live, receives the
        // input. Deterministic code enforces here, as it does at
        // every other read of the record; no client is trusted to
        // fence the slice it is handed. The speaker is whoever the
        // caller resolved for this input: the front door's named
        // speaker, or for a stand-in telling the connection's
        // enrolled person (SealedIntakeService). A speakerless
        // input gets the no-reader rule the front door's recent
        // list uses: only Open and Household events are visible.
        // Every caller builds a fresh slice per input, so fencing
        // the input's own context fences exactly the slice this
        // telling or question travels with.
        FenceRecentEventsToSpeaker(input);

        // Beat 4 stated-turn pre-pass (integration): the deterministic
        // shape runs before the client on every mode, live included,
        // so a stated turn can never land as a plain note. See
        // StatedTurnPrePass.
        var output = StatedTurnPrePass.TryExtract(input)
            ?? await _client.ExtractAsync(input, ct);
        var validation = ExtractionValidator.Validate(input, output);
        var result = new ExtractionResult { Output = output, Validation = validation };

        if (!validation.IsValid || output.ClarificationNeeded || output.Events.Count == 0)
        {
            // Clarify, lookup, silence, or an invalid output: nothing is
            // written. The reply is the only trace.
            result.HouseReply = output.SpokenResponse;
            return result;
        }

        var channel = input.SourceType;
        var actionIndex = output.ProposedAction?.EventIndex;
        for (var i = 0; i < output.Events.Count; i++)
        {
            var extracted = output.Events[i];
            var resolution = await _audience.DecideAsync(input, extracted);
            var ev = new Event
            {
                HouseholdId = input.Context.HouseholdId,
                Type = Enum.Parse<EventType>(extracted.Type),
                Body = extracted.Body,
                SubjectPersonId = extracted.SubjectPersonId,
                AuthorPersonId = extracted.AuthorPersonId,
                RelatedSupplyItemId = extracted.RelatedSupplyItemId,
                Details = extracted.Details.Count == 0 ? null : extracted.Details.ToJsonString(),
                CreatedAtUtc = input.OccurredAtLocal.UtcDateTime,
                SensitivityClass = resolution.Class,
                AudienceRule = resolution.Rule
            };

            if (output.ProposedAction is not null && actionIndex == i)
            {
                var pa = output.ProposedAction;
                var action = new EventAction
                {
                    ActionType = Enum.Parse<EventActionType>(pa.ActionType),
                    TargetPersonId = pa.TargetPersonId,
                    Payload = pa.Payload.Count == 0 ? null : pa.Payload.ToJsonString(),
                    CreatedAtUtc = DateTime.UtcNow
                };
                await _confirmations.ProposeAsync(ev, action, channel);
                result.ProposedEvent = ev;
            }
            else
            {
                await _confirmations.WriteStatementAsync(ev, channel);
            }
            if (resolution.AskPersonId is not null)
            {
                // Clause 7: the event is already stored at its
                // narrowest audience; the ask is the only trace that
                // a wider audience is waiting on a person's answer.
                var ask = await _audience.CreateAskAsync(ev, resolution, DateTime.UtcNow);
                result.AsksCreated.Add(ask);
            }
            result.WrittenEvents.Add(ev);

            if (ev.Type == EventType.Commitment)
            {
                var promise = await BirthPromiseAsync(input, ev, extracted, channel);
                if (promise is not null)
                {
                    result.PromisesBorn.Add(promise);
                }
            }
            if (ev.Type == EventType.Supply)
            {
                await ApplySupplyAdjustmentAsync(ev, extracted);
            }
        }

        result.HouseReply = output.ProposedAction is not null
            ? output.ConfirmationPrompt ?? output.SpokenResponse
            : ApplyForecastWording(input, output, result.WrittenEvents.FirstOrDefault());
        return result;
    }

    // The fence itself: keep only the slice events visible to the
    // input's speaker under the stored audience (BriefService.
    // IsVisibleTo, the brief's and lookup's own rule), or, with no
    // speaker, only the shared classes under the no-reader rule.
    private static void FenceRecentEventsToSpeaker(ExtractionInput input)
    {
        if (input.Context is null)
        {
            return;
        }
        input.Context.RecentEvents = input.Context.RecentEvents
            .Where(ev => input.Speaker is { } speaker
                ? BriefService.IsVisibleTo(ev, speaker.PersonId)
                : BriefService.IsVisibleWithNoReader(ev))
            .ToList();
    }

    // A Commitment is the birth record of a Promise: the promise
    // object arrives alongside it, with the promisor
    // from the telling, never inferred. DueAtLocal is set only when
    // the pipeline resolved the spoken due words against the clock;
    // otherwise DueText stands alone until a person confirms a time.
    private async Task<Promise?> BirthPromiseAsync(
        ExtractionInput input, Event source, ExtractionEvent extracted, string channel)
    {
        if (source.AuthorPersonId is not int promisor)
        {
            return null;
        }
        var details = extracted.Details;
        var promise = new Promise
        {
            HouseholdId = input.Context.HouseholdId,
            PromisorPersonId = promisor,
            PromiseePersonId = GetInt(details, "PromiseePersonId"),
            CommitmentText = GetString(details, "CommitmentText") ?? source.Body,
            DueText = GetString(details, "DueText"),
            SourceEventId = source.Id,
            State = PromiseState.Made
        };
        if (GetString(details, "DueAtLocal") is { } dueRaw
            && DateTimeOffset.TryParse(dueRaw, out var dueAt))
        {
            // DueAtLocal is a wall-clock time in the household's day,
            // stored as stated, not converted to this machine's zone.
            promise.DueAtLocal = dueAt.DateTime;
        }
        _db.Promises.Add(promise);
        await _db.SaveChangesAsync();
        _db.EventAudits.Add(new EventAudit
        {
            ObjectKind = "Promise",
            EventId = source.Id,
            FromState = null,
            ToState = nameof(PromiseState.Made),
            ActorPersonId = promisor,
            Channel = channel,
            AtUtc = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
        return promise;
    }

    // A landed supply event moves the count the way the words say and
    // leaves an adjustment row, the same trail the seed writes: "out"
    // and "last of" empty the shelf; a restock with a spoken number
    // adds it; anything else records the telling without inventing a
    // number.
    private async Task ApplySupplyAdjustmentAsync(Event ev, ExtractionEvent extracted)
    {
        if (ev.RelatedSupplyItemId is not int itemId)
        {
            return;
        }
        var item = await _db.SupplyItems.FirstAsync(s => s.Id == itemId);
        var direction = GetString(extracted.Details, "Direction") ?? "Low";
        var quantityText = GetString(extracted.Details, "QuantityText") ?? "";
        decimal? after = null;
        if (direction == "Out" || quantityText.StartsWith("last of", StringComparison.OrdinalIgnoreCase))
        {
            after = 0m;
        }
        else if (direction == "Restocked")
        {
            var m = System.Text.RegularExpressions.Regex.Match(quantityText, @"\d+");
            if (m.Success && decimal.TryParse(m.Value, out var n))
            {
                after = item.QuantityOnHand + n;
            }
        }
        if (after is not null)
        {
            item.QuantityOnHand = after.Value;
        }
        _db.SupplyAdjustments.Add(new SupplyAdjustment
        {
            EventId = ev.Id,
            SupplyItemId = itemId,
            Direction = direction,
            QuantityOnHandAfter = after ?? item.QuantityOnHand,
            AtUtc = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
    }

    // The forecast is deterministic code with access to the store:
    // when a supply event resolves to an item with a computable
    // run-out day, the spoken line is the forecast template carrying
    // that day; an unresolved item is answered with the plain
    // recorded line, exactly as worked example 19 fixes it.
    private string? ApplyForecastWording(ExtractionInput input, ExtractionOutput output, Event? written)
    {
        var response = output.SpokenResponse;
        if (output.Events.Count == 1 && output.Events[0].Type == "Supply")
        {
            var ev = output.Events[0];
            if (GetString(ev.Details, "UnresolvedItem") is not null)
            {
                return SpokenTemplates.Recorded;
            }
            if (written?.RelatedSupplyItemId is int itemId)
            {
                var item = _db.SupplyItems.FirstOrDefault(s => s.Id == itemId);
                if (item is not null)
                {
                    var runOut = item.GetRunOutDate(DateOnly.FromDateTime(input.OccurredAtLocal.Date));
                    if (runOut != DateOnly.MaxValue)
                    {
                        var direction = GetString(ev.Details, "Direction") ?? "Low";
                        return SpokenTemplates.SupplyForecast(
                            item.Name,
                            SpokenTemplates.DirectionWord(direction),
                            runOut.ToDateTime(TimeOnly.MinValue).ToString("dddd"));
                    }
                }
            }
        }
        return response;
    }

    private static string? GetString(JsonObject details, string key) =>
        details.TryGetPropertyValue(key, out var node) && node is not null
            ? node.GetValue<string>()
            : null;

    private static int? GetInt(JsonObject details, string key) =>
        details.TryGetPropertyValue(key, out var node) && node is not null
            ? node.GetValue<int>()
            : null;
}

// Small readers for the Details JSON other services need, so the parse
// rule lives in one place: malformed details read as absent, never as
// a guess.
public static class ExtractionDetailsReader
{
    public static string? GetString(string? detailsJson, string key)
    {
        if (string.IsNullOrWhiteSpace(detailsJson))
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(detailsJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(key, out var value)
                && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }

    public static int? GetInt(string? detailsJson, string key)
    {
        if (string.IsNullOrWhiteSpace(detailsJson))
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(detailsJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(key, out var value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt32(out var n))
            {
                return n;
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }
}
