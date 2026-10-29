using System.Text.Json;
using System.Text.Json.Nodes;
using HomeNode.Data;
using HomeNode.Services.Extraction;
using Microsoft.EntityFrameworkCore;

namespace HomeNode.Services;

// The outcome of the audience decision for one event, computed once
// at write time (audience decision clause 1). When AskPersonId is set, the
// event is stored at the interim (narrowest) audience and an ask is
// owed to that person; BaselineClass and BaselineRule carry the
// audience the event would have had without the ask, so an answer
// can restore it and nothing wider.
public class AudienceResolution
{
    public required SensitivityClass Class { get; init; }
    public required string? Rule { get; init; }
    public int? AskPersonId { get; init; }
    public string? AskReason { get; init; }
    public string? BaselineClass { get; init; }
    public string? BaselineRule { get; init; }
    public List<int> BaselineAudienceIds { get; init; } = new();
    public List<int> FinalAudienceIds { get; init; } = new();
}

// The deterministic half of the audience decision rules, clauses 1 to 10. The model
// proposes event shapes and, under clause 3, stated audiences quoted
// from the teller's words; everything in this service is plain code
// over the record: ceilings, clamping, defaults, the ask, and
// storage (clause 10). Nothing here runs at read time, and a stored
// audience is never recomputed: a default change touches no event.
//
// The fences, in the order DecideAsync applies them:
//   clause 2, the class ceiling from the event's shape;
//   clause 6, a health mention takes no default and no stated mark:
//     its audience is its class rule, its route, and the subject's
//     health mention level choice (the household's advance setting),
//     and defaults grant nothing across any connection, because no
//     default here ever consults Consent or any connection state;
//   clause 9, a person-free telling resolves by the ceiling alone,
//     no default applies and no ask is generated, and the circle
//     member who closes it is its ask equivalent (see
//     ConfirmationService.CloseRecordPendingAsync);
//   clause 3, stated marks fenced: quoted clause verbatim in the
//     transcript, named persons in the circle, clamped to the
//     ceiling, narrow-only where the speaker tells about another
//     person, conflicting marks in one telling cancel;
//   clause 5, whose default governs: the subject's default governs
//     an event about a person, the speaker's governs their own
//     tellings about no one in particular, and where both apply the
//     narrower resolution governs, which this service implements by
//     taking every applicable default as a narrowing candidate and
//     keeping the narrowest. A default narrows discretionary
//     membership only: persons named structurally by the class rule
//     (a promise's maker and promisee, a health mention's route
//     person, an event's subject and author) are in every candidate
//     set and cannot be removed by a default. A default never alters
//     routing, which is the household's standing setting;
//   clause 6 again: no candidate ever widens past the ceiling. The
//     one stated exception is clause 6's own: a journal entry leaves
//     Personal only by the speaker's stated marking in that telling,
//     so a stated Household mark on the speaker's own journal entry
//     lifts it to Household, while a Household default never does;
//   clause 7, the ask, rationed to decisions genuinely open, as
//     coded in DecideAsync below.
public class AudienceDecisionService
{
    // The ask's lapse clock: 24 hours from its presentation, the same
    // span the confirmation flow gives an unanswered proposal. The
    // rules fix that the clock runs from presentation; the span is
    // this build's stated choice.
    public static readonly TimeSpan AskLapseSpan = TimeSpan.FromHours(24);

    private readonly AppDbContext _db;

    public AudienceDecisionService(AppDbContext db)
    {
        _db = db;
    }

    // Clause 2: the ceiling. The event's shape sets the widest
    // audience it may ever have. Structural persons are the ones the
    // class rule names; the audience set is who may ever be surfaced
    // the event under the ceiling alone.
    private static (SensitivityClass Class, JsonObject Rule, HashSet<int> Structural, HashSet<int> Audience)
        CeilingFor(ExtractionInput input, ExtractionEvent ev)
    {
        var circle = input.Context.Persons.Select(p => p.PersonId).ToHashSet();
        var subject = ev.SubjectPersonId;
        var author = ev.AuthorPersonId;
        switch (ev.Type)
        {
            case "HealthMention":
            {
                // The route the prompt names is the route this rule
                // stores, so the words a person confirms and the
                // dispatch that follows can never disagree.
                // A health mention goes to the route person, and
                // when the teller is the route person reporting
                // their own mention, it goes to the backup instead:
                // the stored route becomes the backup, and no
                // fallback to the speaker is stored, because a
                // mention must never dispatch back to the person
                // who reported it when the prompt named the backup.
                var selfReportByRoutePerson =
                    author is int selfAuthor
                    && input.Context.DefaultRoutePersonId is int defaultRoute
                    && selfAuthor == defaultRoute
                    && (subject is null || subject == selfAuthor);
                var effectiveRoute = selfReportByRoutePerson
                    ? input.Context.BackupRoutePersonId
                    : input.Context.DefaultRoutePersonId;
                var rule = new JsonObject { ["Class"] = "Restricted" };
                rule["RoutePersonId"] = effectiveRoute is int route ? JsonValue.Create(route) : null;
                rule["BackupPersonId"] = !selfReportByRoutePerson && input.Context.BackupRoutePersonId is int backup
                    ? JsonValue.Create(backup)
                    : null;
                var structural = new HashSet<int>();
                if (subject is int s) structural.Add(s);
                if (effectiveRoute is int r) structural.Add(r);
                return (SensitivityClass.Restricted, rule, structural, new HashSet<int>(structural));
            }
            case "Journal":
            {
                var rule = new JsonObject { ["Class"] = "Personal" };
                var structural = new HashSet<int>();
                if ((subject ?? author) is int focus) structural.Add(focus);
                return (SensitivityClass.Personal, rule, structural, new HashSet<int>(structural));
            }
            case "Commitment":
            {
                var rule = new JsonObject
                {
                    ["Class"] = "Named",
                    ["MakerPersonId"] = author is int maker ? JsonValue.Create(maker) : null
                };
                var structural = new HashSet<int>();
                if (author is int a) structural.Add(a);
                if (ev.Details.TryGetPropertyValue("PromiseePersonId", out var promisee) && promisee is not null)
                {
                    rule["PromiseePersonId"] = promisee.DeepClone();
                    if (promisee is JsonValue pv && pv.TryGetValue<int>(out var pid)) structural.Add(pid);
                }
                return (SensitivityClass.Named, rule, structural, new HashSet<int>(structural));
            }
            // A stated turn (the stated-turn audience case from the
            // house layer's extraction, which the audience defaults
            // did not carry): the telling names
            // its person, and the stored rule routes the reminder to
            // the assigned person and to nobody else, whatever the
            // household's routing says later.
            case "Task" when ev.Details.TryGetPropertyValue("TurnPersonId", out var turnNode)
                && turnNode is not null && turnNode.GetValue<int>() is int turnPersonId:
            {
                var rule = new JsonObject
                {
                    ["Class"] = "Named",
                    ["RoutePersonId"] = JsonValue.Create(turnPersonId),
                    ["SubjectPersonId"] = subject is int ts ? JsonValue.Create(ts) : null,
                    ["AuthorPersonId"] = author is int ta ? JsonValue.Create(ta) : null
                };
                var structural = new HashSet<int> { turnPersonId };
                if (subject is int s2) structural.Add(s2);
                if (author is int a2) structural.Add(a2);
                return (SensitivityClass.Named, rule, structural, new HashSet<int>(structural));
            }
            case "Task" when subject is not null && author is not null && subject != author:
            {
                var rule = new JsonObject
                {
                    ["Class"] = "Named",
                    ["SubjectPersonId"] = JsonValue.Create(subject.Value),
                    ["AuthorPersonId"] = JsonValue.Create(author.Value)
                };
                var structural = new HashSet<int> { subject.Value, author.Value };
                return (SensitivityClass.Named, rule, structural, new HashSet<int>(structural));
            }
            default:
            {
                var rule = new JsonObject { ["Class"] = "Household" };
                var structural = new HashSet<int>();
                if (subject is int s) structural.Add(s);
                if (author is int a) structural.Add(a);
                return (SensitivityClass.Household, rule, structural, circle);
            }
        }
    }

    public async Task<AudienceResolution> DecideAsync(ExtractionInput input, ExtractionEvent ev)
    {
        var (ceilingClass, ceilingRule, structural, ceilingAudience) = CeilingFor(input, ev);
        var circle = input.Context.Persons.Select(p => p.PersonId).ToHashSet();
        var subject = ev.SubjectPersonId;
        var author = ev.AuthorPersonId;

        AudienceResolution CeilingOnly() => new()
        {
            Class = ceilingClass,
            Rule = ceilingRule.ToJsonString(),
            FinalAudienceIds = Sorted(ceilingAudience)
        };

        // Clause 6: the health mention exclusion. No default, no
        // stated mark, no ask ever applies to a health mention.
        if (ev.Type == "HealthMention")
        {
            return CeilingOnly();
        }
        // Clause 9: a person-free telling resolves by the ceiling
        // alone. (A telling captured during an outage has no audience
        // until it is extracted on reconnection; this procedure runs
        // then, because extraction is when it is called, and any ask
        // it reaches starts its clock at presentation, below.)
        if (subject is null && author is null)
        {
            return CeilingOnly();
        }

        var focus = (subject ?? author)!.Value;
        var narrowest = new HashSet<int>(structural) { focus };
        var aboutAnother = subject is not null && author is not null && subject != author;

        // Clause 3: the stated audience, fenced. A mark counts only
        // when its Clause is the teller's words verbatim in the
        // transcript and every person it names is in the circle.
        // Conflicting stated marks in one telling cancel, and the
        // step yields nothing.
        HashSet<int>? statedSet = null;
        var statedLift = false;
        var unclearRaised = false;
        var valid = ev.StatedMarks.Where(m =>
            !string.IsNullOrWhiteSpace(m.Clause)
            && input.Transcript.Contains(m.Clause.Trim(), StringComparison.OrdinalIgnoreCase)
            && m.PersonIds.All(circle.Contains)
            && (m.Kind is "Personal" or "Household" or "Named" or "Unclear")
            && (m.Kind != "Named" || m.PersonIds.Count > 0)).ToList();
        unclearRaised = valid.Any(m => m.Kind == "Unclear");
        var resolved = valid.Where(m => m.Kind != "Unclear").ToList();
        var signatures = resolved
            .Select(m => m.Kind + ":" + string.Join(",", Sorted(m.PersonIds.ToHashSet())))
            .Distinct().ToList();
        if (signatures.Count == 1)
        {
            var mark = resolved[0];
            switch (mark.Kind)
            {
                case "Personal":
                    statedSet = new HashSet<int>(structural) { focus };
                    break;
                case "Named":
                {
                    var named = new HashSet<int>(mark.PersonIds);
                    named.UnionWith(structural);
                    if (aboutAnother)
                    {
                        // Narrow only: a stated audience about another
                        // person can never add someone that person
                        // would not otherwise reach.
                        named.IntersectWith(ceilingAudience);
                        named.UnionWith(structural);
                    }
                    else if (ceilingClass == SensitivityClass.Personal)
                    {
                        // Clamped to the Personal ceiling: a journal
                        // entry leaves Personal by a stated Household
                        // marking (below), not by naming more people.
                        named = new HashSet<int>(structural) { focus };
                    }
                    statedSet = named;
                    break;
                }
                case "Household":
                    if (ceilingClass == SensitivityClass.Personal && !aboutAnother)
                    {
                        // Clause 6's stated exception: the speaker's
                        // own journal entry, marked shared in this
                        // telling, leaves Personal for Household.
                        statedSet = new HashSet<int>(circle);
                        statedLift = true;
                    }
                    else
                    {
                        statedSet = new HashSet<int>(ceilingAudience);
                    }
                    break;
            }
        }

        // Clauses 4, 5, and 8: the governing defaults. The applicable
        // persons are the subject (an event about a person) and the
        // author (their own telling); where both apply, the narrower
        // resolution governs. A minor with no row holds Ask until set
        // (clause 8): that is the effective default, not a stored one.
        var applicable = new List<int>();
        if (subject is int sId) applicable.Add(sId);
        if (author is int aId && !applicable.Contains(aId)) applicable.Add(aId);
        var rows = await _db.PersonAudienceDefaults
            .Where(d => d.HouseholdId == input.Context.HouseholdId && applicable.Contains(d.HolderPersonId))
            .ToListAsync();
        var effective = new Dictionary<int, AudienceDefaultValue?>();
        foreach (var personId in applicable)
        {
            var row = rows.FirstOrDefault(d => d.HolderPersonId == personId);
            if (row is not null)
            {
                effective[personId] = row.Value;
            }
            else
            {
                var slice = input.Context.Persons.FirstOrDefault(p => p.PersonId == personId);
                effective[personId] = slice is { MinorFlag: true } ? AudienceDefaultValue.Ask : null;
            }
        }
        var governingAsk = effective.Values.Any(v => v == AudienceDefaultValue.Ask);
        var anyDefault = effective.Values.Any(v => v is not null);

        // Narrowing candidates: the stated set and each applicable
        // Private default. Household defaults add no candidate (they
        // leave the telling at the shape's class), and no candidate
        // can widen past the ceiling, because each is built from the
        // ceiling set, the structural set, and the focus person.
        var candidates = new List<HashSet<int>>();
        if (statedSet is not null && !statedLift)
        {
            candidates.Add(statedSet);
        }
        foreach (var personId in applicable)
        {
            if (effective[personId] == AudienceDefaultValue.Private)
            {
                candidates.Add(new HashSet<int>(structural) { focus });
            }
        }
        var finalSet = new HashSet<int>(ceilingAudience);
        var lifted = false;
        if (candidates.Count > 0)
        {
            finalSet = candidates
                .OrderBy(c => c.Count)
                .ThenBy(c => string.Join(",", Sorted(c)), StringComparer.Ordinal)
                .First();
        }
        else if (statedLift)
        {
            finalSet = new HashSet<int>(circle);
            lifted = true;
        }

        (SensitivityClass, string) Render(HashSet<int> set)
        {
            if (set.SetEquals(ceilingAudience) && !lifted)
            {
                return (ceilingClass, ceilingRule.ToJsonString());
            }
            if (lifted && set.SetEquals(circle))
            {
                return (SensitivityClass.Household, """{"Class":"Household"}""");
            }
            var rule = (JsonObject)ceilingRule.DeepClone();
            var derived = set.Count == 1 ? SensitivityClass.Personal : SensitivityClass.Named;
            rule["Class"] = derived.ToString();
            rule["AudiencePersonIds"] = new JsonArray(Sorted(set).Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());
            return (derived, rule.ToJsonString());
        }

        // Clause 7: the ask step, rationed. It engages only where a
        // decision is genuinely open: a governing default says Ask;
        // or stated words raised sharing and left it unclear and no
        // default resolves it; or (the same fact from the other side)
        // the telling concerns a person whose default says Ask. And
        // it never engages about material already resolved to its
        // narrowest state: when the ceiling audience is the narrowest
        // set, there is nothing open to ask about.
        string? askReason = null;
        if (governingAsk)
        {
            askReason = "DefaultAsk";
        }
        else if (unclearRaised && statedSet is null && !anyDefault)
        {
            askReason = "StatedUnclear";
        }
        if (askReason is not null && ceilingAudience.Count > narrowest.Count)
        {
            // The person entitled to answer: the subject for a telling
            // about them, the speaker for their own telling.
            var askPerson = aboutAnother ? subject!.Value : (author ?? subject)!.Value;
            var (baseClass, baseRule) = Render(finalSet);
            var (interimClass, interimRule) = Render(narrowest);
            // An interim audience derived from the baseline rule keeps
            // the baseline's structural keys; Render against the
            // narrowest set from the ceiling rule does the same,
            // because structural keys live on the ceiling rule.
            _ = baseRule;
            return new AudienceResolution
            {
                Class = interimClass,
                Rule = interimRule,
                AskPersonId = askPerson,
                AskReason = askReason,
                BaselineClass = baseClass.ToString(),
                BaselineRule = baseRule,
                BaselineAudienceIds = Sorted(finalSet),
                FinalAudienceIds = Sorted(narrowest)
            };
        }

        var (finalClass, finalRule) = Render(finalSet);
        return new AudienceResolution
        {
            Class = finalClass,
            Rule = finalRule,
            FinalAudienceIds = Sorted(finalSet)
        };
    }

    // Writes the ask row for an event DecideAsync held at its
    // narrowest audience, and flags the event. One ask per event.
    public async Task<AudienceAsk> CreateAskAsync(Event ev, AudienceResolution resolution, DateTime nowUtc)
    {
        if (resolution.AskPersonId is not int askPerson)
        {
            throw new InvalidOperationException("This resolution carries no ask.");
        }
        if (await _db.AudienceAsks.AnyAsync(a => a.EventId == ev.Id))
        {
            throw new InvalidOperationException($"Event {ev.Id} already has an audience ask.");
        }
        var ask = new AudienceAsk
        {
            EventId = ev.Id,
            AskedPersonId = askPerson,
            Reason = resolution.AskReason ?? "",
            State = AudienceAskState.Pending,
            BaselineAudienceIds = IdsToJson(resolution.BaselineAudienceIds),
            InterimAudienceIds = IdsToJson(resolution.FinalAudienceIds),
            BaselineClass = resolution.BaselineClass ?? "",
            BaselineRule = resolution.BaselineRule,
            CreatedAtUtc = nowUtc
        };
        _db.AudienceAsks.Add(ask);
        ev.AudienceAskPending = true;
        await _db.SaveChangesAsync();
        return ask;
    }

    // Presentation starts the lapse clock (clause 7: the clock runs
    // from presentation). Called by a surface at the moment it shows
    // the ask to the person entitled to answer it, and by nothing
    // else: an ask nobody has been shown cannot lapse.
    public async Task<AudienceAsk> PresentAskAsync(int askId, DateTime nowUtc)
    {
        var ask = await LoadAskAsync(askId);
        if (ask.State == AudienceAskState.Pending)
        {
            ask.State = AudienceAskState.Presented;
            ask.PresentedAtUtc = nowUtc;
            await _db.SaveChangesAsync();
        }
        return ask;
    }

    public async Task<List<AudienceAsk>> PendingAsksForAsync(int personId, DateTime nowUtc)
    {
        var asks = await _db.AudienceAsks
            .Where(a => a.AskedPersonId == personId
                        && (a.State == AudienceAskState.Pending || a.State == AudienceAskState.Presented))
            .OrderBy(a => a.CreatedAtUtc)
            .ToListAsync();
        foreach (var ask in asks.Where(a => a.State == AudienceAskState.Pending))
        {
            ask.State = AudienceAskState.Presented;
            ask.PresentedAtUtc = nowUtc;
        }
        if (asks.Any(a => a.State == AudienceAskState.Presented && a.PresentedAtUtc == nowUtc))
        {
            await _db.SaveChangesAsync();
        }
        return asks;
    }

    // The entitled person's answer (clause 7). The answer may confirm
    // the baseline (Household restores exactly the recorded baseline
    // class and rule) or narrow further (Private, or Named persons
    // clamped to the baseline); it can never widen past the baseline
    // recorded at write time. Anyone who is not the person the ask
    // was presented to cannot answer it.
    public async Task<AudienceAsk> AnswerAskAsync(
        int askId, int actorPersonId, string answer, IEnumerable<int>? namedPersonIds, DateTime nowUtc)
    {
        var ask = await _db.AudienceAsks
            .Include(a => a.Event)
            .FirstOrDefaultAsync(a => a.Id == askId)
            ?? throw new InvalidOperationException($"Audience ask {askId} does not exist.");
        if (ask.State is not (AudienceAskState.Pending or AudienceAskState.Presented))
        {
            throw new InvalidOperationException($"Audience ask {askId} is {ask.State}; it can no longer be answered.");
        }
        if (actorPersonId != ask.AskedPersonId)
        {
            throw new InvalidOperationException("Only the person the house asked may answer this ask.");
        }
        var ev = ask.Event!;
        var baseline = ParseIds(ask.BaselineAudienceIds);
        var structural = StructuralOf(ev);
        var focus = ev.SubjectPersonId ?? ev.AuthorPersonId ?? ask.AskedPersonId;
        HashSet<int> chosen;
        switch (answer)
        {
            case "Household":
                chosen = new HashSet<int>(baseline);
                break;
            case "Private":
                chosen = new HashSet<int>(structural) { focus };
                break;
            case "Named":
            {
                var ids = (namedPersonIds ?? Enumerable.Empty<int>()).ToList();
                if (ids.Count == 0)
                {
                    throw new InvalidOperationException("A Named answer names at least one person.");
                }
                var circleIds = await _db.Persons
                    .Where(p => p.HouseholdId == ev.HouseholdId)
                    .Select(p => p.Id)
                    .ToListAsync();
                if (ids.Any(id => !circleIds.Contains(id)))
                {
                    throw new InvalidOperationException("A Named answer may name only people in the circle.");
                }
                chosen = new HashSet<int>(ids);
                chosen.UnionWith(structural);
                chosen.IntersectWith(baseline.Union(structural));
                break;
            }
            default:
                throw new InvalidOperationException($"'{answer}' is not an audience answer.");
        }

        if (answer == "Household" && ask.BaselineRule is not null)
        {
            ev.SensitivityClass = Enum.Parse<SensitivityClass>(ask.BaselineClass);
            ev.AudienceRule = ask.BaselineRule;
        }
        else
        {
            var derived = chosen.Count == 1 ? SensitivityClass.Personal : SensitivityClass.Named;
            ev.SensitivityClass = derived;
            JsonObject rule;
            try
            {
                rule = string.IsNullOrWhiteSpace(ev.AudienceRule)
                    ? new JsonObject()
                    : JsonNode.Parse(ev.AudienceRule) as JsonObject ?? new JsonObject();
            }
            catch (JsonException)
            {
                rule = new JsonObject();
            }
            rule["Class"] = derived.ToString();
            rule["AudiencePersonIds"] = new JsonArray(Sorted(chosen).Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());
            ev.AudienceRule = rule.ToJsonString();
        }
        ev.AudienceAskPending = false;
        ask.State = AudienceAskState.Answered;
        ask.Answer = answer;
        ask.AnsweredByPersonId = actorPersonId;
        ask.AnsweredAtUtc = nowUtc;
        await _db.SaveChangesAsync();
        return ask;
    }

    // The ask lapse: a presented ask unanswered for the lapse span
    // lapses, and the event stays exactly where it was stored, at its
    // narrowest audience. Silence never widens (clause 7). Returns
    // how many asks lapsed.
    public async Task<int> LapseAsksAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var cutoff = nowUtc - AskLapseSpan;
        var expired = await _db.AudienceAsks
            .Include(a => a.Event)
            .Where(a => a.State == AudienceAskState.Presented
                        && a.PresentedAtUtc != null
                        && a.PresentedAtUtc <= cutoff)
            .ToListAsync(ct);
        foreach (var ask in expired)
        {
            ask.State = AudienceAskState.Lapsed;
            if (ask.Event is not null)
            {
                ask.Event.AudienceAskPending = false;
            }
        }
        await _db.SaveChangesAsync(ct);
        return expired.Count;
    }

    // Clause 4 and clause 8: defaults by telling, with read-back.

    public async Task<PersonAudienceDefault?> GetDefaultRowAsync(int householdId, int holderPersonId) =>
        await _db.PersonAudienceDefaults
            .FirstOrDefaultAsync(d => d.HouseholdId == householdId && d.HolderPersonId == holderPersonId);

    // The effective default: a stored row, or Ask for a minor who
    // holds no row yet (clause 8: a minor's default is Ask until
    // set), or null for anyone else (no default; the shape decides).
    public async Task<AudienceDefaultValue?> GetEffectiveDefaultAsync(int householdId, int holderPersonId)
    {
        var row = await GetDefaultRowAsync(householdId, holderPersonId);
        if (row is not null)
        {
            return row.Value;
        }
        var person = await _db.Persons.FirstOrDefaultAsync(p => p.Id == holderPersonId && p.HouseholdId == householdId);
        return person is { MinorFlag: true } ? AudienceDefaultValue.Ask : null;
    }

    // One telling about a default. The holder telling about their own
    // default produces a read-back and waits for the holder's
    // disposal; nothing is applied yet. Anyone else's instruction
    // about a person's default is recorded as a proposal to that
    // person and is never applied by the instruction itself. An
    // adult's proposal to widen a minor's default takes the same
    // road: it waits for the minor's own act (clause 8).
    public async Task<AudienceDefaultRequest> ProposeByTellingAsync(
        int householdId, int holderPersonId, int requestedByPersonId,
        AudienceDefaultValue value, string tellingText, string channel, DateTime nowUtc)
    {
        var holder = await LoadPersonAsync(householdId, holderPersonId);
        var requester = await LoadPersonAsync(householdId, requestedByPersonId);
        var holderFirst = holder.DisplayName.Split(' ')[0];
        var request = new AudienceDefaultRequest
        {
            HouseholdId = householdId,
            HolderPersonId = holderPersonId,
            RequestedByPersonId = requestedByPersonId,
            Value = value,
            TellingText = tellingText,
            Channel = channel,
            CreatedAtUtc = nowUtc
        };
        if (requestedByPersonId != holderPersonId)
        {
            request.State = AudienceDefaultRequestState.ProposalWaiting;
            request.ReadBackText =
                $"Recorded as a proposal to {holderFirst}: the audience default {value}, " +
                $"proposed by {requester.DisplayName.Split(' ')[0]}. It is not applied. " +
                $"Only {holderFirst} can set it, by their own telling.";
        }
        else
        {
            request.State = AudienceDefaultRequestState.PendingReadBack;
            request.ReadBackText = value switch
            {
                AudienceDefaultValue.Private =>
                    "You are setting your audience default to Private. Your unspecified tellings " +
                    "will resolve to just you, plus anyone the shape of the telling names. " +
                    "Say yes to set it, or no to leave it as it is.",
                AudienceDefaultValue.Household =>
                    "You are setting your audience default to Household. Your unspecified tellings " +
                    "will stay at the shape's own audience, up to the household, and no wider. " +
                    "Say yes to set it, or no to leave it as it is.",
                AudienceDefaultValue.Ask =>
                    "You are setting your audience default to Ask. Before the audience of one of " +
                    "your unspecified tellings is set, the house will ask you, and will hold the " +
                    "telling at its narrowest audience until you answer. " +
                    "Say yes to set it, or no to leave it as it is.",
                _ => ""
            };
        }
        _db.AudienceDefaultRequests.Add(request);
        await _db.SaveChangesAsync();
        return request;
    }

    // The holder's yes to the read-back: the disposal is recorded and
    // the default applies, forward-looking only.
    public async Task<AudienceDefaultRequest> ConfirmReadBackAsync(int requestId, int actorPersonId, DateTime nowUtc)
    {
        var request = await LoadRequestAsync(requestId);
        if (request.State != AudienceDefaultRequestState.PendingReadBack)
        {
            throw new InvalidOperationException($"Default request {requestId} is {request.State}; no read-back is waiting.");
        }
        if (actorPersonId != request.HolderPersonId)
        {
            throw new InvalidOperationException("Only the holder may dispose of their own read-back.");
        }
        await ApplyDefaultAsync(request.HouseholdId, request.HolderPersonId, request.Value, nowUtc);
        request.State = AudienceDefaultRequestState.Applied;
        request.DisposedByPersonId = actorPersonId;
        request.DisposedAtUtc = nowUtc;
        await _db.SaveChangesAsync();
        return request;
    }

    public async Task<AudienceDefaultRequest> DeclineReadBackAsync(int requestId, int actorPersonId, DateTime nowUtc)
    {
        var request = await LoadRequestAsync(requestId);
        if (request.State != AudienceDefaultRequestState.PendingReadBack)
        {
            throw new InvalidOperationException($"Default request {requestId} is {request.State}; no read-back is waiting.");
        }
        if (actorPersonId != request.HolderPersonId)
        {
            throw new InvalidOperationException("Only the holder may dispose of their own read-back.");
        }
        request.State = AudienceDefaultRequestState.Declined;
        request.DisposedByPersonId = actorPersonId;
        request.DisposedAtUtc = nowUtc;
        await _db.SaveChangesAsync();
        return request;
    }

    // The holder's own act on a waiting proposal: for a minor, this
    // is the minor's act a widening proposal waits for (clause 8);
    // for anyone, a proposal becomes a default only when its holder
    // takes it up. The holder may also decline it, which ends it.
    public async Task<AudienceDefaultRequest> AcceptProposalAsync(int requestId, int actorPersonId, DateTime nowUtc)
    {
        var request = await LoadRequestAsync(requestId);
        if (request.State != AudienceDefaultRequestState.ProposalWaiting)
        {
            throw new InvalidOperationException($"Default request {requestId} is {request.State}; no proposal is waiting.");
        }
        if (actorPersonId != request.HolderPersonId)
        {
            throw new InvalidOperationException("Only the holder may take up a proposal about their own default.");
        }
        await ApplyDefaultAsync(request.HouseholdId, request.HolderPersonId, request.Value, nowUtc);
        request.State = AudienceDefaultRequestState.Applied;
        request.DisposedByPersonId = actorPersonId;
        request.DisposedAtUtc = nowUtc;
        await _db.SaveChangesAsync();
        return request;
    }

    public async Task<AudienceDefaultRequest> DeclineProposalAsync(int requestId, int actorPersonId, DateTime nowUtc)
    {
        var request = await LoadRequestAsync(requestId);
        if (request.State != AudienceDefaultRequestState.ProposalWaiting)
        {
            throw new InvalidOperationException($"Default request {requestId} is {request.State}; no proposal is waiting.");
        }
        if (actorPersonId != request.HolderPersonId)
        {
            throw new InvalidOperationException("Only the holder may decline a proposal about their own default.");
        }
        request.State = AudienceDefaultRequestState.Declined;
        request.DisposedByPersonId = actorPersonId;
        request.DisposedAtUtc = nowUtc;
        await _db.SaveChangesAsync();
        return request;
    }

    // Clause 8: a minor's default is set at enrollment by the adult
    // who enrolls them. The enrollment act applies directly and is
    // recorded as such.
    public async Task<AudienceDefaultRequest> SetAtEnrollmentAsync(
        int householdId, int minorPersonId, int enrollingAdultPersonId,
        AudienceDefaultValue value, DateTime nowUtc)
    {
        var minor = await LoadPersonAsync(householdId, minorPersonId);
        var adult = await LoadPersonAsync(householdId, enrollingAdultPersonId);
        if (!minor.MinorFlag)
        {
            throw new InvalidOperationException("Enrollment setting is for a minor's default.");
        }
        if (adult.MinorFlag || adult.Id == minor.Id)
        {
            throw new InvalidOperationException("A minor's enrollment default is set by the adult who enrolls them.");
        }
        await ApplyDefaultAsync(householdId, minorPersonId, value, nowUtc);
        var request = new AudienceDefaultRequest
        {
            HouseholdId = householdId,
            HolderPersonId = minorPersonId,
            RequestedByPersonId = enrollingAdultPersonId,
            Value = value,
            State = AudienceDefaultRequestState.Applied,
            TellingText = "(set at enrollment)",
            Channel = "enrollment",
            CreatedAtUtc = nowUtc,
            DisposedByPersonId = enrollingAdultPersonId,
            DisposedAtUtc = nowUtc
        };
        _db.AudienceDefaultRequests.Add(request);
        await _db.SaveChangesAsync();
        return request;
    }

    // Clause 8: a circle adult may narrow a minor's default at any
    // time; the narrowing applies at once. Anything wider than the
    // minor's current effective default is not a narrowing: it is
    // recorded as a proposal and waits for the minor's own act.
    public async Task<AudienceDefaultRequest> NarrowMinorDefaultAsync(
        int householdId, int adultPersonId, int minorPersonId,
        AudienceDefaultValue value, DateTime nowUtc)
    {
        var minor = await LoadPersonAsync(householdId, minorPersonId);
        var adult = await LoadPersonAsync(householdId, adultPersonId);
        if (!minor.MinorFlag)
        {
            throw new InvalidOperationException("This path narrows a minor's default; the person named is not a minor.");
        }
        if (adult.MinorFlag || adult.Id == minor.Id)
        {
            throw new InvalidOperationException("Only a circle adult may narrow a minor's default.");
        }
        var current = await GetEffectiveDefaultAsync(householdId, minorPersonId)
            ?? AudienceDefaultValue.Ask;
        if (AudienceDefaultRank.Of(value) >= AudienceDefaultRank.Of(current))
        {
            return await ProposeByTellingAsync(
                householdId, minorPersonId, adultPersonId, value,
                "(an adult proposes a wider default for a minor)", "telling", nowUtc);
        }
        await ApplyDefaultAsync(householdId, minorPersonId, value, nowUtc);
        var request = new AudienceDefaultRequest
        {
            HouseholdId = householdId,
            HolderPersonId = minorPersonId,
            RequestedByPersonId = adultPersonId,
            Value = value,
            State = AudienceDefaultRequestState.Applied,
            TellingText = "(a circle adult narrows a minor's default)",
            Channel = "telling",
            CreatedAtUtc = nowUtc,
            DisposedByPersonId = adultPersonId,
            DisposedAtUtc = nowUtc
        };
        _db.AudienceDefaultRequests.Add(request);
        await _db.SaveChangesAsync();
        return request;
    }

    private async Task ApplyDefaultAsync(int householdId, int holderPersonId, AudienceDefaultValue value, DateTime nowUtc)
    {
        var row = await GetDefaultRowAsync(householdId, holderPersonId);
        if (row is null)
        {
            _db.PersonAudienceDefaults.Add(new PersonAudienceDefault
            {
                HouseholdId = householdId,
                HolderPersonId = holderPersonId,
                Value = value,
                SetAtUtc = nowUtc
            });
        }
        else
        {
            row.Value = value;
            row.SetAtUtc = nowUtc;
        }
        await _db.SaveChangesAsync();
    }

    private static HashSet<int> StructuralOf(Event ev)
    {
        var structural = new HashSet<int>();
        if (ev.SubjectPersonId is int s) structural.Add(s);
        if (ev.AuthorPersonId is int a) structural.Add(a);
        if (!string.IsNullOrWhiteSpace(ev.AudienceRule))
        {
            try
            {
                using var doc = JsonDocument.Parse(ev.AudienceRule);
                foreach (var key in new[] { "RoutePersonId", "MakerPersonId", "PromiseePersonId" })
                {
                    if (doc.RootElement.TryGetProperty(key, out var value)
                        && value.ValueKind == JsonValueKind.Number
                        && value.TryGetInt32(out var n))
                    {
                        structural.Add(n);
                    }
                }
            }
            catch (JsonException)
            {
            }
        }
        return structural;
    }

    private async Task<Person> LoadPersonAsync(int householdId, int personId) =>
        await _db.Persons.FirstOrDefaultAsync(p => p.Id == personId && p.HouseholdId == householdId)
        ?? throw new InvalidOperationException($"Person {personId} is not in household {householdId}.");

    private async Task<AudienceDefaultRequest> LoadRequestAsync(int requestId) =>
        await _db.AudienceDefaultRequests.FirstOrDefaultAsync(r => r.Id == requestId)
        ?? throw new InvalidOperationException($"Default request {requestId} does not exist.");

    private async Task<AudienceAsk> LoadAskAsync(int askId) =>
        await _db.AudienceAsks.FirstOrDefaultAsync(a => a.Id == askId)
        ?? throw new InvalidOperationException($"Audience ask {askId} does not exist.");

    private static List<int> Sorted(HashSet<int> ids) => ids.OrderBy(id => id).ToList();

    private static string IdsToJson(IEnumerable<int> ids) =>
        JsonSerializer.Serialize(ids.OrderBy(id => id).ToList());

    private static HashSet<int> ParseIds(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<int>>(json)?.ToHashSet() ?? new HashSet<int>();
        }
        catch (JsonException)
        {
            return new HashSet<int>();
        }
    }
}
