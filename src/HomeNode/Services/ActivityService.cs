using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using HomeNode.Data;
using Microsoft.EntityFrameworkCore;

namespace HomeNode.Services;

// The house's activity, message by message: what came in, what the
// model made of it, what the house's own rules decided, what a person
// decided, and where it went. Every surface that shows the model's
// work reads it from here, so the trace reads the same everywhere.
//
// Nothing here decides anything. The trace is assembled from rows the
// record already keeps (the message's reading, its entries, their
// actions, the append-only audit trail, deliveries, the sealed outbox,
// claims, supply adjustments) and only describes them.
//
// Audience: a trace is shown to a reader only when every one of its
// entries is visible to that reader under the audience stored on the
// entry at write time (BriefService.IsVisibleTo, or with no reader
// named, the shared classes only). One hidden entry hides the whole
// message, words and model output included: the check fails closed.
// "Who can see it" is computed the same way, by asking that rule about
// each person in the household, so the label can never disagree with
// the fence.
public class ActivityService
{
    private readonly AppDbContext _db;

    public ActivityService(AppDbContext db)
    {
        _db = db;
    }

    // The newest activity a reader may see, newest first.
    public async Task<ActivityFeed> BuildAsync(int? viewerId, int take = 60)
    {
        var ctx = await LoadAsync(readingId: null);
        var items = new List<ActivityItem>();
        if (ctx is null)
        {
            return new ActivityFeed();
        }
        foreach (var item in ctx.Compose())
        {
            if (ctx.VisibleTo(item, viewerId))
            {
                items.Add(item.Item);
            }
        }
        return new ActivityFeed
        {
            Items = items.OrderByDescending(i => i.AtUtc).ThenByDescending(i => i.SortId).Take(take).ToList(),
            Zone = ctx.Zone
        };
    }

    // One message's trace, for the reader named: the message a person
    // just sent, or the one whose proposal they just answered. The
    // reader is the person who sent or answered it, and the same
    // fence applies: a trace that reader may not see comes back null.
    public async Task<ActivityItem?> ForReadingAsync(int readingId, int? viewerId)
    {
        var ctx = await LoadAsync(readingId);
        if (ctx is null)
        {
            return null;
        }
        var match = ctx.Compose().FirstOrDefault(c => c.Item.ReadingId == readingId);
        return match is not null && ctx.VisibleTo(match, viewerId) ? match.Item : null;
    }

    // The reading that produced a record entry, if one is on file.
    public async Task<int?> ReadingIdForEventAsync(int eventId)
    {
        var readings = await _db.ModelReadings
            .Where(r => r.EventIds != null)
            .OrderByDescending(r => r.Id)
            .Select(r => new { r.Id, r.EventIds })
            .Take(400)
            .ToListAsync();
        foreach (var reading in readings)
        {
            if (ParseIds(reading.EventIds).Contains(eventId))
            {
                return reading.Id;
            }
        }
        return null;
    }

    // Counts for the model page: every reading on file is counted
    // (a question leaves a counted row with no content), while the
    // entry and outcome counts cover only what the reader may see.
    public async Task<ReadingStats> StatsAsync(int? viewerId)
    {
        var stats = new ReadingStats();
        var all = await _db.ModelReadings
            .Select(r => new { r.Engine, r.Outcome, r.ElapsedMs, r.PromptTokens, r.CompletionTokens })
            .ToListAsync();
        stats.Total = all.Count;
        foreach (var group in all.GroupBy(r => r.Engine))
        {
            var times = group.Select(r => r.ElapsedMs).OrderBy(t => t).ToList();
            stats.Engines.Add(new EngineStat
            {
                Engine = group.Key,
                Label = EngineLabel(group.Key),
                Count = group.Count(),
                MedianMs = times.Count == 0 ? 0 : times[times.Count / 2],
                PromptTokens = group.Sum(r => r.PromptTokens ?? 0),
                CompletionTokens = group.Sum(r => r.CompletionTokens ?? 0)
            });
        }
        stats.Engines = stats.Engines.OrderByDescending(e => e.Count).ToList();
        stats.Outcomes = all.GroupBy(r => r.Outcome)
            .ToDictionary(g => g.Key, g => g.Count());

        var feed = await BuildAsync(viewerId, take: 500);
        foreach (var item in feed.Items.Where(i => i.ReadingId is not null))
        {
            stats.VisibleMessages++;
            foreach (var entry in item.Entries)
            {
                stats.EntriesByKind[entry.KindLabel] = stats.EntriesByKind.GetValueOrDefault(entry.KindLabel) + 1;
                if (entry.Action is not null)
                {
                    stats.ActionsProposed++;
                    switch (entry.Event.ConfirmationState)
                    {
                        case ConfirmationState.Proposed:
                            stats.ActionsWaiting++;
                            break;
                        case ConfirmationState.Confirmed:
                        case ConfirmationState.Done:
                            stats.ActionsConfirmed++;
                            break;
                        case ConfirmationState.NotConfirmed:
                            stats.ActionsNotSent++;
                            break;
                    }
                }
            }
        }
        stats.Recent = feed.Items.Where(i => i.ReadingId is not null).Take(8).ToList();
        return stats;
    }

    public static string EngineLabel(string engine) => engine switch
    {
        ModelReadingEngine.Live => "Muse Spark",
        ModelReadingEngine.Mock => "Local stand-in",
        ModelReadingEngine.Rule => "Turn rule",
        ModelReadingEngine.Private => "No model (private)",
        _ => engine
    };

    // ------------------------------------------------------------------
    // Loading: one pass over the rows a trace is built from.

    private async Task<TraceContext?> LoadAsync(int? readingId)
    {
        var household = await _db.Households.FirstOrDefaultAsync();
        if (household is null)
        {
            return null;
        }
        var zone = HouseholdTime.ZoneFor(household.TimeZoneId);
        var people = await _db.Persons
            .Where(p => p.HouseholdId == household.Id)
            .OrderBy(p => p.Id)
            .ToListAsync();

        List<ModelReading> readings;
        List<Event> events;
        if (readingId is int onlyId)
        {
            readings = await _db.ModelReadings.Where(r => r.Id == onlyId).ToListAsync();
            var wanted = readings.SelectMany(r => ParseIds(r.EventIds)).Distinct().ToList();
            events = await _db.Events
                .Include(e => e.AuthorPerson)
                .Include(e => e.SubjectPerson)
                .Include(e => e.RelatedSupplyItem)
                .Where(e => wanted.Contains(e.Id))
                .ToListAsync();
        }
        else
        {
            readings = await _db.ModelReadings
                .Where(r => r.HouseholdId == household.Id && r.Outcome == ModelReadingOutcome.Entries)
                .OrderByDescending(r => r.Id)
                .Take(300)
                .ToListAsync();
            events = await _db.Events
                .Include(e => e.AuthorPerson)
                .Include(e => e.SubjectPerson)
                .Include(e => e.RelatedSupplyItem)
                .Where(e => e.HouseholdId == household.Id)
                .OrderByDescending(e => e.CreatedAtUtc)
                .Take(300)
                .ToListAsync();
            var have = events.Select(e => e.Id).ToHashSet();
            var missing = readings.SelectMany(r => ParseIds(r.EventIds)).Where(id => !have.Contains(id)).Distinct().ToList();
            if (missing.Count > 0)
            {
                events.AddRange(await _db.Events
                    .Include(e => e.AuthorPerson)
                    .Include(e => e.SubjectPerson)
                    .Include(e => e.RelatedSupplyItem)
                    .Where(e => missing.Contains(e.Id))
                    .ToListAsync());
            }
        }

        var ids = events.Select(e => e.Id).ToList();
        var actions = await _db.EventActions.Where(a => ids.Contains(a.EventId)).ToListAsync();
        var audits = await _db.EventAudits
            .Where(a => ids.Contains(a.EventId))
            .OrderBy(a => a.AtUtc).ThenBy(a => a.Id)
            .ToListAsync();
        var deliveries = await _db.AlertDeliveries
            .Where(d => ids.Contains(d.EventId))
            .ToListAsync();
        var outbox = await _db.SealedOutbox
            .Where(o => o.SourceEventId != null && ids.Contains(o.SourceEventId.Value))
            .Select(o => new OutboxRow(o.SourceEventId!.Value, o.SourceKind, o.Status, o.ToPersonId,
                o.CreatedAtUtc, o.SentAtUtc, o.AcknowledgedAtUtc))
            .ToListAsync();
        var promises = await _db.Promises
            .Include(p => p.ClosedByEvent)
            .Where(p => ids.Contains(p.SourceEventId)
                || (p.ClosedByEventId != null && ids.Contains(p.ClosedByEventId.Value)))
            .ToListAsync();
        var adjustments = await _db.SupplyAdjustments
            .Include(a => a.SupplyItem)
            .Where(a => a.EventId != null && ids.Contains(a.EventId.Value))
            .ToListAsync();

        // The other models' work on these messages: the voice clip a
        // message was heard from, and the Muse Wonder finding it was
        // drafted from.
        var readingIds = readings.Select(r => r.Id).ToList();
        var heard = await _db.ModelCalls
            .Where(c => c.Purpose == ModelCallPurpose.Hear && c.ReadingId != null && readingIds.Contains(c.ReadingId.Value))
            .ToListAsync();
        var reviews = await _db.ModelCalls
            .Where(c => c.Purpose == ModelCallPurpose.Review && c.OutputJson != null && c.OutputJson.Contains("\"SentReadingId\":"))
            .OrderByDescending(c => c.Id).Take(200)
            .ToListAsync();

        return new TraceContext(people, zone, readings, events, actions, audits, deliveries,
            outbox, promises, adjustments, heard, reviews);
    }

    internal static List<int> ParseIds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new List<int>();
        }
        try
        {
            return JsonSerializer.Deserialize<List<int>>(json) ?? new List<int>();
        }
        catch (JsonException)
        {
            return new List<int>();
        }
    }

    private sealed record OutboxRow(int EventId, string SourceKind, string Status, int ToPersonId,
        DateTime CreatedAtUtc, DateTime? SentAtUtc, DateTime? AcknowledgedAtUtc);

    // A composed item with what the fence needs: the entries behind it.
    private sealed record Composed(ActivityItem Item, List<Event> Events, bool Complete);

    // ------------------------------------------------------------------
    // Composition: the rows above, turned into traces.

    private sealed class TraceContext
    {
        private readonly List<Person> _people;
        private readonly Dictionary<int, Person> _byId;
        private readonly List<ModelReading> _readings;
        private readonly Dictionary<int, Event> _events;
        private readonly ILookup<int, EventAction> _actions;
        private readonly ILookup<int, EventAudit> _audits;
        private readonly ILookup<int, AlertDelivery> _deliveries;
        private readonly ILookup<int, OutboxRow> _outbox;
        private readonly List<Promise> _promises;
        private readonly ILookup<int, SupplyAdjustment> _adjustments;
        private readonly Dictionary<int, ModelCall> _heardByReading;
        private readonly Dictionary<int, (int CallId, Services.Models.ReviewFinding Finding)> _draftByReading = new();

        public TimeZoneInfo Zone { get; }

        public TraceContext(List<Person> people, TimeZoneInfo zone, List<ModelReading> readings,
            List<Event> events, List<EventAction> actions, List<EventAudit> audits,
            List<AlertDelivery> deliveries, List<OutboxRow> outbox, List<Promise> promises,
            List<SupplyAdjustment> adjustments, List<ModelCall> heard, List<ModelCall> reviews)
        {
            _heardByReading = heard.GroupBy(c => c.ReadingId!.Value).ToDictionary(g => g.Key, g => g.First());
            foreach (var call in reviews)
            {
                var result = Services.Models.ReviewService.Read(call);
                foreach (var finding in result?.Findings ?? new())
                {
                    if (finding.SentReadingId is int sent && !_draftByReading.ContainsKey(sent))
                    {
                        _draftByReading[sent] = (call.Id, finding);
                    }
                }
            }
            _people = people;
            _byId = people.ToDictionary(p => p.Id);
            Zone = zone;
            _readings = readings;
            _events = events.GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First());
            _actions = actions.ToLookup(a => a.EventId);
            _audits = audits.ToLookup(a => a.EventId);
            _deliveries = deliveries.ToLookup(d => d.EventId);
            _outbox = outbox.ToLookup(o => o.EventId);
            _promises = promises;
            _adjustments = adjustments.ToLookup(a => a.EventId ?? 0);
        }

        public bool VisibleTo(Composed composed, int? viewerId)
        {
            if (!composed.Complete || composed.Events.Count == 0)
            {
                return false;
            }
            return composed.Events.All(e => viewerId is int viewer
                ? BriefService.IsVisibleTo(e, viewer)
                : BriefService.IsVisibleWithNoReader(e));
        }

        public IEnumerable<Composed> Compose()
        {
            var consumed = new HashSet<int>();
            foreach (var reading in _readings)
            {
                var ids = ParseIds(reading.EventIds);
                var entries = new List<Event>();
                var complete = ids.Count > 0;
                foreach (var id in ids)
                {
                    if (_events.TryGetValue(id, out var ev))
                    {
                        entries.Add(ev);
                        consumed.Add(id);
                    }
                    else
                    {
                        complete = false;
                    }
                }
                yield return new Composed(FromReading(reading, entries), entries, complete);
            }
            foreach (var ev in _events.Values)
            {
                if (consumed.Contains(ev.Id))
                {
                    continue;
                }
                yield return new Composed(FromEntry(ev), new List<Event> { ev }, true);
            }
        }

        private string First(int? personId) =>
            personId is int id && _byId.TryGetValue(id, out var person)
                ? NameWording.First(person.DisplayName)
                : "";

        private ActivityItem FromReading(ModelReading reading, List<Event> entries)
        {
            var speakerName = reading.SpeakerPersonId is int speaker ? First(speaker) : "";
            var origin = entries.Select(e => e.ProvenanceOrigin).FirstOrDefault(o => !string.IsNullOrWhiteSpace(o));
            var channel = reading.Channel switch
            {
                "tap" => "Home",
                "federated" => origin is not null
                    ? $"{origin}, sealed relay"
                    : speakerName.Length > 0 ? $"{speakerName}'s assistant, sealed relay" : "an assistant, sealed relay",
                "voice" => "Home, by voice",
                _ => reading.Channel
            };
            var marks = StatedMarksByIndex(reading.OutputJson);
            _heardByReading.TryGetValue(reading.Id, out var heardCall);
            var drafted = _draftByReading.TryGetValue(reading.Id, out var draft) ? draft : default;
            var traced = entries.Select((e, i) => Describe(e, marks.GetValueOrDefault(i), seeded: false)).ToList();
            var item = new ActivityItem
            {
                Key = $"m{reading.Id}",
                SortId = reading.Id,
                AtUtc = reading.ReadAtUtc,
                SpeakerId = reading.SpeakerPersonId,
                SpeakerName = speakerName.Length > 0 ? speakerName : "No name given",
                ChannelLabel = channel,
                Words = reading.Words,
                ReadingId = reading.Id,
                Engine = new TraceEngine
                {
                    Kind = reading.Engine is ModelReadingEngine.Live or ModelReadingEngine.Mock or ModelReadingEngine.Rule
                        or ModelReadingEngine.Private
                        ? reading.Engine
                        : "other",
                    Label = EngineLabel(reading.Engine),
                    Model = reading.Model,
                    ElapsedMs = reading.ElapsedMs,
                    PromptTokens = reading.PromptTokens,
                    CompletionTokens = reading.CompletionTokens,
                    ContextLine = ContextLine(reading.ContextSummary, speakerName),
                    OutputJson = Pretty(reading.OutputJson),
                    Reply = reading.Reply,
                    PrivateBy = PrivateKind(reading.ContextSummary),
                    HeardModel = heardCall?.Model,
                    HeardMs = heardCall?.ElapsedMs,
                    HeardEdited = heardCall is not null && HeardEdited(heardCall.OutputJson),
                    DraftCallId = drafted.Finding is null ? null : drafted.CallId,
                    DraftCites = drafted.Finding?.EntryIds ?? new(),
                    DraftEdited = drafted.Finding?.SentEdited ?? false
                },
                Entries = traced
            };
            SetStatus(item);
            return item;
        }

        // Why a private message was kept from the models, from the
        // reading's counted summary: "switch" or "rule", never words.
        private static string? PrivateKind(string? summary)
        {
            if (string.IsNullOrWhiteSpace(summary))
            {
                return null;
            }
            return summary.Contains("\"PrivateBySwitch\"") ? Extraction.PrivacyRule.BySwitch
                : summary.Contains("\"PrivateByRule\"") ? Extraction.PrivacyRule.ByRule
                : null;
        }

        private static bool HeardEdited(string? json) =>
            json is not null && json.Contains("\"edited\":true", StringComparison.Ordinal);

        private ActivityItem FromEntry(Event ev)
        {
            var audits = _audits[ev.Id].Where(a => a.ObjectKind == "Event").ToList();
            var firstChannel = audits.FirstOrDefault()?.Channel;
            var closesClaim = _promises.Any(p => p.ClosedByEventId == ev.Id);
            // An entry with no reading, no assistant origin, and a record
            // time at or before the seed's last entry is the demo's
            // starting record: the seeder wrote it, no model read it.
            var seeded = ev.ProvenanceOrigin is null
                && ev.CreatedAtUtc <= DbSeeder.LastSeededEntryUtc;
            var channel = seeded
                ? "Demo starting record"
                : closesClaim && firstChannel == "tap"
                    ? "Claims page"
                    : ev.ProvenanceOrigin is { } origin
                        ? $"{origin}, sealed relay"
                        : firstChannel switch
                        {
                            "tap" => "Home",
                            "federated" => "an assistant, sealed relay",
                            _ => "The record"
                        };
            var entry = Describe(ev, null, seeded);
            var item = new ActivityItem
            {
                Key = $"e{ev.Id}",
                SortId = ev.Id,
                AtUtc = ev.CreatedAtUtc,
                SpeakerId = ev.AuthorPersonId,
                SpeakerName = ev.AuthorPersonId is int author ? First(author) : "The house",
                ChannelLabel = channel,
                Words = ev.Body,
                ReadingId = null,
                Engine = new TraceEngine
                {
                    Kind = seeded ? "seed" : closesClaim ? "page" : "none",
                    Label = seeded ? "Demo seed" : closesClaim ? "Written by hand" : "No reading on file"
                },
                Entries = new List<TraceEntry> { entry }
            };
            SetStatus(item);
            return item;
        }

        private static void SetStatus(ActivityItem item)
        {
            var entries = item.Entries;
            if (entries.Any(e => e.Waiting))
            {
                item.StatusKey = "waiting";
                item.StatusLabel = "Waiting for a person";
                return;
            }
            if (entries.Any(e => e.RecordPending))
            {
                item.StatusKey = "waiting";
                item.StatusLabel = "Needs confirmation";
                return;
            }
            var sentTo = entries.SelectMany(e => e.SentTo).Distinct().ToList();
            if (sentTo.Count > 0)
            {
                item.WasSent = true;
                item.StatusKey = "sent";
                item.StatusLabel = "Sent to " + JoinNames(sentTo);
                return;
            }
            if (entries.Any(e => e.Lapsed))
            {
                item.StatusKey = "stopped";
                item.StatusLabel = "Lapsed, not sent";
                return;
            }
            if (entries.Any(e => e.Event.ConfirmationState == ConfirmationState.NotConfirmed))
            {
                item.StatusKey = "stopped";
                item.StatusLabel = "Not sent";
                return;
            }
            if (entries.Any(e => e.Owed))
            {
                item.StatusKey = "owed";
                item.StatusLabel = "Confirmed, not carried out";
                return;
            }
            if (entries.Any(e => e.Event.ConfirmationState == ConfirmationState.Done))
            {
                item.StatusKey = "done";
                item.StatusLabel = "Done";
                return;
            }
            item.StatusKey = "recorded";
            item.StatusLabel = "On the record";
        }

        // ------------------------------------------------------------
        // One entry: its structured fields, its audience, its action,
        // and everything that happened to it since.

        private TraceEntry Describe(Event ev, List<StatedMark>? marks, bool seeded)
        {
            var action = _actions[ev.Id].FirstOrDefault();
            var details = ParseObject(ev.Details);
            var entry = new TraceEntry
            {
                Event = ev,
                KindLabel = EventKindWording.For(ev.Type),
                KindKey = ev.Type.ToString().ToLowerInvariant(),
                Title = "",
                AudienceLabel = AudienceLabel(ev),
                AudienceBasis = AudienceBasis(ev),
                RecordPending = ev.RecordPending && ev.ConfirmationState == ConfirmationState.Proposed,
                Waiting = !ev.RecordPending && ev.ConfirmationState == ConfirmationState.Proposed,
                StatedMarks = marks ?? new List<StatedMark>(),
                AskPending = ev.AudienceAskPending
            };
            FillFields(entry, ev, details, action);
            if (action is not null)
            {
                entry.Action = DescribeAction(ev, action);
            }
            FillOutcome(entry, ev, action, seeded);
            return entry;
        }

        private void FillFields(TraceEntry entry, Event ev, JsonObject details, EventAction? action)
        {
            void Add(string label, string? value)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    entry.Fields.Add(new EntryField(label, value.Trim()));
                }
            }
            var about = ev.SubjectPersonId is int subject ? First(subject) : null;
            switch (ev.Type)
            {
                case EventType.Request:
                {
                    var kind = Str(details, "RequestKind");
                    var target = Int(details, "TargetPersonId") ?? action?.TargetPersonId;
                    var payload = ParseObject(action?.Payload);
                    entry.Title = kind switch
                    {
                        "Relay" => target is int t ? $"A message for {First(t)}" : "A message to pass on",
                        "Media" => Str(payload, "WhatText") is { } what ? $"Put on {what}" : "Put something on",
                        "Reminder" => "A reminder",
                        "Bring" => "Something to bring",
                        _ => "A request"
                    };
                    Add("Kind", kind switch
                    {
                        "Relay" => "pass on a message",
                        "Media" => "play something",
                        "Reminder" => "reminder",
                        "Bring" => "bring something",
                        null => null,
                        _ => "request"
                    });
                    Add("For", target is int to ? First(to) : null);
                    Add("When", Str(payload, "RemindOnText"));
                    Add("What", Str(payload, "TaskText"));
                    break;
                }
                case EventType.Supply:
                {
                    var item = ev.RelatedSupplyItem?.Name ?? Str(details, "UnresolvedItem");
                    var direction = Str(details, "Direction");
                    var directionWord = direction switch
                    {
                        "Low" => "running low",
                        "Out" => "out",
                        "Used" => "used",
                        "Restocked" => "restocked",
                        _ => direction?.ToLowerInvariant()
                    };
                    entry.Title = item is not null
                        ? $"{Capitalize(item)}{(directionWord is null ? "" : $": {directionWord}")}"
                        : "A supply";
                    Add("Item", item is null ? null
                        : ev.RelatedSupplyItemId is null ? $"{item} (not a tracked item)" : item);
                    Add("Status", directionWord);
                    Add("Amount", Str(details, "QuantityText"));
                    break;
                }
                case EventType.Whereabouts:
                {
                    var place = Str(details, "PlaceText");
                    var motion = Str(details, "Motion");
                    var motionWord = motion switch
                    {
                        "At" => "at",
                        "HeadingTo" => "heading to",
                        "HeadingHome" => "heading home",
                        "Leaving" => "leaving",
                        "Arrived" => "arrived at",
                        "Home" => "home",
                        _ => null
                    };
                    var who = about ?? "Someone";
                    var isHome = string.Equals(place, "home", StringComparison.OrdinalIgnoreCase);
                    entry.Title = motion switch
                    {
                        "HeadingHome" => $"{who} is heading home",
                        "Home" => $"{who} is home",
                        "Arrived" when isHome || place is null => $"{who} is home",
                        "Arrived" => $"{who} arrived at {place}",
                        "At" or "HeadingTo" or "Leaving" when place is not null => $"{who} is {motionWord} {place}",
                        _ => place is not null ? $"{who}: {place}" : $"Where {who} is"
                    };
                    Add("Who", about);
                    Add("Place", place);
                    Add("Next", Str(details, "NextText"));
                    break;
                }
                case EventType.Commitment:
                {
                    var text = Str(details, "CommitmentText") ?? ev.Body;
                    entry.Title = $"A claim: {Unquote(text)}";
                    Add("Taken on by", ev.AuthorPersonId is int maker ? First(maker) : null);
                    Add("For", Int(details, "PromiseePersonId") is int promisee ? First(promisee) : "the household");
                    Add("Due", DueText(details));
                    break;
                }
                case EventType.Task:
                {
                    var text = Str(details, "TaskText") ?? ev.Body;
                    var kind = Str(details, "TaskKind");
                    var turn = Int(details, "TurnPersonId");
                    entry.Title = turn is int turnPerson
                        ? $"{First(turnPerson)}'s turn: {text}"
                        : kind == "Pickup" ? $"A pickup: {text}" : kind == "Errand" ? $"An errand: {text}" : $"A task: {text}";
                    Add("Kind", kind switch { "Pickup" => "pickup", "Errand" => "errand", "General" => "household job", _ => null });
                    Add("Whose turn", turn is int tp ? First(tp) : null);
                    Add("About", turn is null ? about : null);
                    Add("Collect", Int(details, "CollectPersonId") is int collect ? First(collect) : null);
                    Add("Where", Str(details, "PlaceText"));
                    Add("When", Str(details, "WhenText") ?? DueText(details));
                    if (Bool(details, "HelpFlag") == true)
                    {
                        Add("Help", "wanted");
                    }
                    if (Bool(details, "NeedsAssignee") == true)
                    {
                        Add("Who does it", "nobody named yet");
                    }
                    break;
                }
                case EventType.HealthMention:
                {
                    entry.Title = about is not null ? $"Health mention about {about}" : "A health mention";
                    Add("Form", Str(details, "Form") switch { "Question" => "a question", "Statement" => "a statement", _ => null });
                    entry.Note = "Written down in the speaker's own words. Tellhouse gives no medical advice; it can only tell a person.";
                    break;
                }
                case EventType.Journal:
                    entry.Title = about is not null ? $"{about}'s journal" : "A journal entry";
                    Add("Kind", Str(details, "EntryKind") == "PromptAnswer" ? "answer to a journal prompt" : "journal entry");
                    break;
                case EventType.FacilityNotice:
                    entry.Title = "A building notice";
                    Add("From", Str(details, "SourceName"));
                    Add("Change", Str(details, "ChangeText"));
                    break;
                default:
                {
                    var topic = Str(details, "DecisionTopic");
                    var state = Str(details, "DecisionState");
                    var mealKind = Str(details, "MealProvenance");
                    if (topic is not null)
                    {
                        entry.Title = state == "Decided" ? $"Decided: {topic}" : $"An open decision: {topic}";
                        Add("Decision", state == "Decided" ? Str(details, "DecidedText") ?? "decided" : "still open");
                    }
                    else if (mealKind is not null)
                    {
                        entry.Title = mealKind == "Fridge"
                            ? $"In the fridge: {Str(details, "ItemName")}"
                            : $"A usual dinner: {Str(details, "ItemName")}";
                    }
                    else if (Str(details, "RelayTo") is { } relayTo)
                    {
                        entry.Title = $"A note for {relayTo}";
                        Add("Sent with", Str(details, "InputMode") == "Tap" ? "a tap" : null);
                    }
                    else
                    {
                        entry.Title = "A note for the household";
                    }
                    Add("About", about);
                    break;
                }
            }
            if (ev.Type is EventType.Request or EventType.Supply or EventType.FacilityNotice
                && about is not null
                && !entry.Fields.Any(f => f.Value == about))
            {
                entry.Fields.Insert(0, new EntryField("About", about));
            }

            var promise = _promises.FirstOrDefault(p => p.SourceEventId == ev.Id);
            if (promise is not null)
            {
                entry.Claim = new ClaimView(
                    Unquote(promise.CommitmentText),
                    promise.State switch
                    {
                        PromiseState.Made => "open",
                        PromiseState.Kept => "done",
                        PromiseState.Broken => "closed, not done",
                        PromiseState.Released => "no longer needed",
                        _ => promise.State.ToString()
                    },
                    promise.State == PromiseState.Made ? null : promise.ClosedByEvent?.Body,
                    promise.ClosedAtUtc);
            }
            var adjustment = _adjustments[ev.Id].FirstOrDefault();
            if (adjustment?.SupplyItem is { } supplyItem && adjustment.QuantityOnHandAfter is decimal after)
            {
                var unit = after == 1m ? supplyItem.Unit : supplyItem.Unit + "s";
                entry.SupplyLine = $"{Capitalize(supplyItem.Name)} on hand: {after.ToString("0.#", CultureInfo.InvariantCulture)} {unit}";
            }
        }

        private EntryAction DescribeAction(Event ev, EventAction action)
        {
            var payload = ParseObject(action.Payload);
            var target = action.TargetPersonId ?? RouteFromRule(ev.AudienceRule);
            var targetName = target is int t ? First(t) : null;
            return action.ActionType switch
            {
                EventActionType.RelayMessage => new EntryAction(
                    "Pass on a message",
                    targetName is not null
                        ? $"Tell {targetName}: “{Str(payload, "MessageText") ?? ev.Body}”"
                        : $"Pass on: “{Str(payload, "MessageText") ?? ev.Body}”",
                    targetName is not null ? $"Tell {targetName}" : "Tell them",
                    targetName),
                EventActionType.NotifyPerson => new EntryAction(
                    "Tell a person",
                    targetName is not null ? $"Tell {targetName} about this entry, in these words" : "Tell the route person about this entry",
                    targetName is not null ? $"Tell {targetName}" : "Tell them",
                    targetName),
                EventActionType.CreateReminder => new EntryAction(
                    "Set a reminder",
                    Str(payload, "RemindOnText") is { } on
                        ? $"Remind on {on}: {Str(payload, "TaskText") ?? ev.Body}"
                        : $"Remind: {Str(payload, "TaskText") ?? ev.Body}",
                    "Set reminder",
                    null),
                EventActionType.MediaPlay => new EntryAction(
                    "Play something",
                    $"Put on {Str(payload, "WhatText") ?? "what was asked for"}",
                    "Put it on",
                    null),
                _ => new EntryAction(action.ActionType.ToString(), ev.Body, "Confirm", targetName)
            };
        }

        // What happened to the entry after the house wrote it, in the
        // order it happened, read from the audit trail, the deliveries,
        // and the sealed outbox.
        private void FillOutcome(TraceEntry entry, Event ev, EventAction? action, bool seeded)
        {
            var steps = new List<TraceStep>();
            foreach (var audit in _audits[ev.Id].Where(a => a.ObjectKind == "Event"))
            {
                var actor = audit.ActorPersonId is int a ? First(a) : null;
                var viaAssistant = audit.Channel == "federated" && actor is not null;
                switch (audit.FromState, audit.ToState)
                {
                    case (null, "Confirmed"):
                        steps.Add(new TraceStep("person", actor ?? "Someone",
                            "said it, so it stands on the record as said", null, audit.AtUtc));
                        break;
                    case (ConfirmationState.Proposed, "Confirmed"):
                        steps.Add(viaAssistant
                            ? new TraceStep("assistant", $"{actor}'s assistant", $"confirmed it for {actor}", null, audit.AtUtc)
                            : new TraceStep("person", actor ?? "Someone", "confirmed it", null, audit.AtUtc));
                        break;
                    case (ConfirmationState.Proposed, "NotConfirmed") when audit.ActorPersonId is null:
                        entry.Lapsed = true;
                        steps.Add(new TraceStep("system", "Nobody",
                            "answered within 24 hours, so it lapsed. Nothing was sent.",
                            null, audit.AtUtc, "stopped"));
                        break;
                    case (ConfirmationState.Proposed, "NotConfirmed"):
                        steps.Add(new TraceStep(viaAssistant ? "assistant" : "person",
                            viaAssistant ? $"{actor}'s assistant" : actor ?? "Someone",
                            action is null
                                ? "marked it as not right. It stays on the record, unconfirmed."
                                : "chose not to send it. It stays on the record, marked Not sent.",
                            null, audit.AtUtc, "stopped"));
                        break;
                    case (ConfirmationState.Confirmed, "Confirmed"):
                        entry.Owed = true;
                        steps.Add(new TraceStep("system", "The house",
                            action?.ActionType switch
                            {
                                EventActionType.CreateReminder => "kept the reminder on the record. This build has no reminder engine to ring it yet.",
                                EventActionType.MediaPlay => "kept it on the record as owed. This build has no media player to start it.",
                                _ => "found nobody it could be delivered to, so it is kept on the record as owed."
                            },
                            null, audit.AtUtc, "failed"));
                        break;
                    case (ConfirmationState.Confirmed, "Done"):
                        if (action?.ActionType == EventActionType.MediaPlay)
                        {
                            steps.Add(new TraceStep("system", "The house", "put it on", null, audit.AtUtc));
                        }
                        break;
                }
            }
            foreach (var delivery in _deliveries[ev.Id])
            {
                var to = First(delivery.ToPersonId);
                entry.SentTo.Add(to);
                steps.Add(new TraceStep("delivery", "Delivered",
                    $"to {to}'s inbox{(delivery.IsBackupHop ? ", as the backup person" : "")}",
                    null, delivery.DeliveredAtUtc));
                if (delivery.AcknowledgedAtUtc is DateTime seen)
                {
                    steps.Add(new TraceStep("person", to, "saw it", null, seen));
                }
            }
            // A sealed packet for a member's assistant, told by its own
            // status: queued (sealed here, not yet carried), sent (the
            // relay carried it), acknowledged (the assistant said so).
            foreach (var packet in _outbox[ev.Id])
            {
                var to = First(packet.ToPersonId);
                var what = packet.SourceKind == "Proposal" ? "the proposal" : "it";
                var purpose = packet.SourceKind == "Proposal" ? " to decide" : "";
                if (packet.SourceKind != "Proposal" && !entry.SentTo.Contains(to))
                {
                    entry.SentTo.Add(to);
                }
                if (packet.Status == "Queued")
                {
                    steps.Add(new TraceStep("delivery", "The house",
                        $"sealed {what} for {to}'s assistant{purpose}, so the relay cannot read it",
                        "Waiting for the relay to carry it", packet.CreatedAtUtc, "waiting"));
                    continue;
                }
                steps.Add(new TraceStep("delivery", "Sealed relay",
                    $"carried {what} to {to}'s assistant{purpose}, unreadable to the relay",
                    packet.Status == "Sent" ? "Waiting for the assistant to acknowledge it" : null,
                    packet.SentAtUtc ?? packet.CreatedAtUtc, packet.Status == "Acknowledged" ? "done" : "waiting"));
                if (packet.AcknowledgedAtUtc is DateTime acked)
                {
                    steps.Add(new TraceStep("assistant", $"{to}'s assistant", "acknowledged it", null, acked));
                }
            }
            var closed = _promises.FirstOrDefault(p => p.SourceEventId == ev.Id && p.State != PromiseState.Made);
            if (entry.Claim is { ClosedWords: not null } claim && claim.ClosedAtUtc is DateTime closedAt)
            {
                steps.Add(new TraceStep("person", closed?.ClosedByPersonId is int closer ? First(closer) : "Someone",
                    $"closed the claim as {claim.State}: \u201c{claim.ClosedWords}\u201d", null, closedAt));
            }
            entry.Outcome = steps.OrderBy(s => s.AtUtc ?? DateTime.MaxValue).ToList();
        }

        // ------------------------------------------------------------
        // Audience, read from the stored rule through the one visibility
        // check every surface uses.

        private string AudienceLabel(Event ev)
        {
            var who = _people.Where(p => BriefService.IsVisibleTo(ev, p.Id))
                .Select(p => NameWording.First(p.DisplayName))
                .ToList();
            if (who.Count == 0)
            {
                return "Nobody yet";
            }
            return who.Count == _people.Count ? "Everyone in the household" : "Only " + JoinNames(who);
        }

        private static string AudienceBasis(Event ev) => ev.SensitivityClass switch
        {
            SensitivityClass.Restricted => "Health rule: the person it is about and the person it goes to",
            SensitivityClass.Personal => ev.Type == EventType.Journal
                ? "Journal entries are personal"
                : "Kept narrow by the sharing rules",
            SensitivityClass.Named => ev.Type switch
            {
                EventType.Commitment => "A claim is between its maker and the person it is for",
                EventType.Task => "A task is shared with the people it names",
                _ => "Shared with the people it names"
            },
            _ => "A household entry"
        };

        private static int? RouteFromRule(string? rule)
        {
            var obj = ParseObject(rule);
            return Int(obj, "RoutePersonId");
        }

        private string? ContextLine(string? summaryJson, string speakerName)
        {
            if (string.IsNullOrWhiteSpace(summaryJson))
            {
                return null;
            }
            try
            {
                var counts = JsonSerializer.Deserialize<Dictionary<string, int>>(summaryJson) ?? new();
                string Count(string key, string one, string many)
                {
                    var n = counts.GetValueOrDefault(key);
                    return $"{n} {(n == 1 ? one : many)}";
                }
                var parts = new List<string>
                {
                    Count("People", "person", "people"),
                    Count("Supplies", "tracked supply", "tracked supplies"),
                    Count("Schedules", "schedule", "schedules"),
                    Count("RecentEntries", "recent entry", "recent entries")
                        + (speakerName.Length > 0 ? $" {speakerName} may see" : " anyone may see")
                };
                return "Read against " + JoinNames(parts);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string? DueText(JsonObject details)
        {
            var text = Str(details, "DueText");
            var at = Str(details, "DueAtLocal");
            if (at is not null && DateTime.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.None, out var due))
            {
                var formatted = due.ToString("ddd MMM d, h:mm tt", CultureInfo.InvariantCulture);
                return text is not null ? $"{text} ({formatted})" : formatted;
            }
            return text;
        }

        private static Dictionary<int, List<StatedMark>> StatedMarksByIndex(string? outputJson)
        {
            var result = new Dictionary<int, List<StatedMark>>();
            var root = ParseObject(outputJson);
            if (root["Events"] is not JsonArray events)
            {
                return result;
            }
            for (var i = 0; i < events.Count; i++)
            {
                if (events[i] is JsonObject ev && ev["StatedMarks"] is JsonArray marks)
                {
                    foreach (var mark in marks.OfType<JsonObject>())
                    {
                        var clause = Str(mark, "Clause");
                        if (!string.IsNullOrWhiteSpace(clause))
                        {
                            if (!result.TryGetValue(i, out var list))
                            {
                                result[i] = list = new List<StatedMark>();
                            }
                            list.Add(new StatedMark(Str(mark, "Kind") ?? "", clause));
                        }
                    }
                }
            }
            return result;
        }
    }

    // ------------------------------------------------------------------
    // Small readers.

    internal static JsonObject ParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new JsonObject();
        }
        try
        {
            return JsonNode.Parse(json) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    private static string? Str(JsonObject obj, string key) =>
        obj.TryGetPropertyValue(key, out var node) && node is JsonValue value
        && value.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)
            ? s
            : null;

    private static int? Int(JsonObject obj, string key) =>
        obj.TryGetPropertyValue(key, out var node) && node is JsonValue value
        && value.TryGetValue<int>(out var n)
            ? n
            : null;

    private static bool? Bool(JsonObject obj, string key) =>
        obj.TryGetPropertyValue(key, out var node) && node is JsonValue value
        && value.TryGetValue<bool>(out var b)
            ? b
            : null;

    private static string Capitalize(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static string Unquote(string text) => text.Trim().Trim('"', '“', '”');

    internal static string JoinNames(IReadOnlyList<string> names) => names.Count switch
    {
        0 => "",
        1 => names[0],
        2 => $"{names[0]} and {names[1]}",
        _ => string.Join(", ", names.Take(names.Count - 1)) + ", and " + names[^1]
    };

    private static string? Pretty(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
        }
        catch (JsonException)
        {
            return json;
        }
    }

    // Pretty form of a raw output, for callers holding one outside a
    // reading row (the reply to a question on Home).
    public static string? PrettyJson(string? json) => Pretty(json);
}

// ----------------------------------------------------------------------
// The shapes the pages render.

public sealed record EntryField(string Label, string Value);

public sealed record EntryAction(string TypeLabel, string Summary, string ConfirmLabel, string? TargetName);

public sealed record StatedMark(string Kind, string Clause);

public sealed record ClaimView(string Text, string State, string? ClosedWords, DateTime? ClosedAtUtc);

// One step in a trace. Actor decides the step's color and icon:
// model (the engine's reading), house (the house's own rules),
// person, assistant, delivery, seed, or system.
public sealed record TraceStep(string Actor, string Who, string Text, string? Detail = null,
    DateTime? AtUtc = null, string State = "done");

public sealed class TraceEngine
{
    // live, mock, rule, other, seed, page, or none.
    public required string Kind { get; init; }
    public required string Label { get; init; }
    public string? Model { get; init; }
    public int? ElapsedMs { get; init; }
    public int? PromptTokens { get; init; }
    public int? CompletionTokens { get; init; }
    public string? ContextLine { get; init; }
    public string? OutputJson { get; init; }
    public string? Reply { get; init; }

    // Kept private (the house's own reader, no model): why.
    public string? PrivateBy { get; init; }

    // Heard by voice before it was read: Muse Voice Transcribe, how
    // long it took, and whether the person changed the words first.
    public string? HeardModel { get; init; }
    public int? HeardMs { get; init; }
    public bool HeardEdited { get; init; }

    // Drafted by Muse Wonder from a review finding: the review, the
    // entries the finding cited, and whether the person changed it.
    public int? DraftCallId { get; init; }
    public List<int> DraftCites { get; init; } = new();
    public bool DraftEdited { get; init; }

    public bool CalledModel => Kind == ModelReadingEngine.Live;
    public bool HasReading => Kind is ModelReadingEngine.Live or ModelReadingEngine.Mock
        or ModelReadingEngine.Rule or ModelReadingEngine.Private or "other";
    // A model (or its local stand-in) read it: not the turn rule, not
    // the house's private reader.
    public bool ModelRead => Kind is ModelReadingEngine.Live or ModelReadingEngine.Mock or "other";
    public bool KeptPrivate => Kind == ModelReadingEngine.Private;
}

public sealed class TraceEntry
{
    public required Event Event { get; init; }
    public required string KindLabel { get; init; }
    public required string KindKey { get; init; }
    public required string Title { get; set; }
    public List<EntryField> Fields { get; } = new();
    public required string AudienceLabel { get; init; }
    public string? AudienceBasis { get; init; }
    public List<StatedMark> StatedMarks { get; init; } = new();
    public bool AskPending { get; init; }
    public EntryAction? Action { get; set; }
    public List<TraceStep> Outcome { get; set; } = new();
    public bool Waiting { get; init; }
    public bool RecordPending { get; init; }
    public bool Lapsed { get; set; }
    public bool Owed { get; set; }
    public List<string> SentTo { get; } = new();
    public ClaimView? Claim { get; set; }
    public string? SupplyLine { get; set; }
    public string? Note { get; set; }
}

public sealed class ActivityItem
{
    public required string Key { get; init; }
    public int SortId { get; init; }
    public required DateTime AtUtc { get; init; }
    public int? SpeakerId { get; init; }
    public required string SpeakerName { get; init; }
    public required string ChannelLabel { get; init; }
    public string? Words { get; init; }
    public int? ReadingId { get; init; }
    public required TraceEngine Engine { get; init; }
    public List<TraceEntry> Entries { get; init; } = new();
    public string StatusKey { get; set; } = "recorded";
    public string StatusLabel { get; set; } = "On the record";
    public bool WasSent { get; set; }
    public bool NeedsDecision => Entries.Any(e => e.Waiting || e.RecordPending);
}

public sealed class ActivityFeed
{
    public List<ActivityItem> Items { get; init; } = new();
    public TimeZoneInfo Zone { get; init; } = HouseholdTime.ZoneFor(null);
}

public sealed class EngineStat
{
    public required string Engine { get; init; }
    public required string Label { get; init; }
    public int Count { get; init; }
    public int MedianMs { get; init; }
    public int PromptTokens { get; init; }
    public int CompletionTokens { get; init; }
}

public sealed class ReadingStats
{
    public int Total { get; set; }
    public List<EngineStat> Engines { get; set; } = new();
    public Dictionary<string, int> Outcomes { get; set; } = new();
    public int VisibleMessages { get; set; }
    public Dictionary<string, int> EntriesByKind { get; } = new();
    public int ActionsProposed { get; set; }
    public int ActionsConfirmed { get; set; }
    public int ActionsNotSent { get; set; }
    public int ActionsWaiting { get; set; }
    public List<ActivityItem> Recent { get; set; } = new();
}
