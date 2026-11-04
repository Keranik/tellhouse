using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HomeNode.Data;
using HomeNode.Services.Extraction;
using Microsoft.EntityFrameworkCore;

namespace HomeNode.Services.Models;

// "What needs attention": two readers look over the household record
// for one person, and neither of them decides anything.
//
//   - The house's own rules, always, on this node, no model: supplies
//     that will run out before they can be reordered, two people
//     claiming the same thing, a proposal close to lapsing, a plan
//     still waiting on someone. These can include private things
//     (a medicine running low), because nothing leaves the house.
//   - Muse Wonder, when the person asks, on a Live instance: Meta's
//     reasoning model reads the SHARED part of what this person may
//     see and reports what a person should look at, citing the
//     entries it relied on, with at most one short draft message per
//     finding. It never sees anything private or health-shaped: those
//     entries, claims and supplies are removed before the request is
//     built (see Slice), and any finding whose words come back
//     health-shaped is dropped.
//
// A finding is never acted on by the house. A person may take a draft
// into the message box on Home, change it, and send it; from there it
// is an ordinary message, read, fenced and confirmed like any other.
public sealed class ReviewService
{
    private readonly AppDbContext _db;
    private readonly MetaApi _api;

    public ReviewService(AppDbContext db, MetaApi api)
    {
        _db = db;
        _api = api;
    }

    public bool WonderAvailable => _api.Callable;

    // ---------- What the person may see, in two cuts ----------

    public sealed class Slice
    {
        public required Household Household { get; init; }
        public required TimeZoneInfo Zone { get; init; }
        public required DateOnly Today { get; init; }
        public required List<Person> People { get; init; }
        public required List<Event> Events { get; init; }
        public required List<Promise> Promises { get; init; }
        public required List<SupplyItem> Supplies { get; init; }
        public required List<Plan> Plans { get; init; }
        public required HashSet<int> PrivateEventIds { get; init; }

        // The cut Muse Wonder may be shown: nothing private, nothing
        // health-shaped, nothing narrower than a shared class unless
        // it is about the people in it.
        public bool ShareableEvent(Event ev) =>
            !PrivateEventIds.Contains(ev.Id)
            && ev.Type != EventType.HealthMention
            && ev.Type != EventType.Journal
            && ev.SensitivityClass is not (SensitivityClass.Restricted or SensitivityClass.Personal)
            && !MentionsMedicine(ev.Details)
            && !PrivacyRule.WordsArePrivate(ev.Body);

        public bool ShareablePromise(Promise p)
        {
            var source = Events.FirstOrDefault(e => e.Id == p.SourceEventId);
            return (source is null || ShareableEvent(source))
                && !PrivacyRule.WordsArePrivate(p.CommitmentText);
        }

        public bool ShareableSupply(SupplyItem s) => !PrivacyRule.WordsArePrivate(s.Name);

        private static bool MentionsMedicine(string? details)
        {
            var med = ExtractionDetailsReader.GetString(details, "MedMention");
            return med is not null && med != "None";
        }
    }

    // nowUtc: the household clock's "now" (the current time unless a
    // check pins it).
    public async Task<Slice?> SliceForAsync(int? viewerId, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var household = await _db.Households.FirstOrDefaultAsync();
        if (household is null)
        {
            return null;
        }
        var zone = HouseholdTime.ZoneFor(household.TimeZoneId);
        var today = DateOnly.FromDateTime(HouseholdTime.ToLocal(now, zone));
        var people = await _db.Persons.OrderBy(p => p.Id).ToListAsync();
        bool Visible(Event ev) => viewerId is int id ? BriefService.IsVisibleTo(ev, id) : BriefService.IsVisibleWithNoReader(ev);

        var since = now.AddDays(-4);
        var events = (await _db.Events
                .Where(e => e.CreatedAtUtc >= since || e.ConfirmationState == ConfirmationState.Proposed)
                .OrderByDescending(e => e.Id).Take(200).ToListAsync())
            .Where(Visible).ToList();
        var promises = await _db.Promises.Where(p => p.State == PromiseState.Made).ToListAsync();
        var sourceIds = promises.Select(p => p.SourceEventId).ToHashSet();
        var sources = await _db.Events.Where(e => sourceIds.Contains(e.Id)).ToListAsync();
        foreach (var s in sources.Where(s => events.All(e => e.Id != s.Id) && Visible(s)))
        {
            events.Add(s);
        }
        promises = promises.Where(p =>
        {
            var source = sources.FirstOrDefault(s => s.Id == p.SourceEventId);
            return viewerId is int id
                ? BriefService.PromiseVisibleTo(p, source, id)
                : BriefService.PromiseVisibleWithNoReader(p, source);
        }).ToList();
        var supplies = await _db.SupplyItems.OrderBy(s => s.Id).ToListAsync();
        var plans = await _db.Plans
            .Include(p => p.Elements).ThenInclude(e => e.Agreements)
            .Where(p => p.State != PlanState.Completed && p.State != PlanState.Dropped && p.State != PlanState.Superseded)
            .ToListAsync();

        var privateIds = new HashSet<int>();
        var privateReadings = await _db.ModelReadings
            .Where(r => r.Engine == ModelReadingEngine.Private && r.EventIds != null)
            .Select(r => r.EventIds!).ToListAsync();
        foreach (var json in privateReadings)
        {
            try
            {
                foreach (var id in JsonSerializer.Deserialize<List<int>>(json) ?? new())
                {
                    privateIds.Add(id);
                }
            }
            catch (JsonException)
            {
                // An unreadable id list protects nothing and hides
                // nothing; the event is still checked by its words.
            }
        }

        return new Slice
        {
            Household = household,
            Zone = zone,
            Today = today,
            People = people,
            Events = events,
            Promises = promises,
            Supplies = supplies,
            Plans = plans,
            PrivateEventIds = privateIds
        };
    }

    // ---------- The house's own rules (no model) ----------

    public static List<ReviewFinding> HouseFindings(Slice slice, int? viewerId)
    {
        var findings = new List<ReviewFinding>();
        string Name(int? id) => slice.People.FirstOrDefault(p => p.Id == id) is { } p ? FirstName(p) : "someone";

        // Supplies that run out before an order could arrive.
        foreach (var s in slice.Supplies)
        {
            if (s.SeededDailyUsageRate <= 0)
            {
                continue;
            }
            var orderBy = s.GetOrderByDate(slice.Today);
            if (orderBy > slice.Today.AddDays(1))
            {
                continue;
            }
            var runOut = s.GetRunOutDate(slice.Today);
            var days = Math.Max(0, runOut.DayNumber - slice.Today.DayNumber);
            var who = s.AssignedPersonId;
            var yours = who is int mine && mine == viewerId;
            findings.Add(new ReviewFinding
            {
                Kind = "running_low",
                Headline = days == 0
                    ? $"{Cap(s.Name)}: out, or about to be."
                    : $"{Cap(s.Name)}: about {days} day{(days == 1 ? "" : "s")} left.",
                Detail = $"An order takes {s.ReorderLeadDays} day{(s.ReorderLeadDays == 1 ? "" : "s")} to arrive"
                    + (who is null ? "." : yours ? $"; you look after {s.Name}." : $"; {Name(who)} looks after {s.Name}."),
                SupplyIds = new() { s.Id },
                PersonIds = who is int w ? new() { w } : new(),
                DraftTo = who is int t && t != viewerId ? t : null,
                DraftMessage = yours
                    ? $"I'll order more {s.Name} today."
                    : who is int t2
                        ? $"Ask {Name(t2)} to order more {s.Name} today."
                        : $"We need more {s.Name}."
            });
        }

        // The same thing claimed twice by different people.
        var open = slice.Promises.Where(p => p.State == PromiseState.Made).ToList();
        for (var i = 0; i < open.Count; i++)
        {
            for (var j = i + 1; j < open.Count; j++)
            {
                var a = open[i];
                var b = open[j];
                if (a.PromisorPersonId == b.PromisorPersonId || Overlap(a.CommitmentText, b.CommitmentText) < 0.5)
                {
                    continue;
                }
                findings.Add(new ReviewFinding
                {
                    Kind = "conflict",
                    Headline = $"{Name(a.PromisorPersonId)} and {Name(b.PromisorPersonId)} both claimed the same thing.",
                    Detail = $"“{a.CommitmentText}” and “{b.CommitmentText}”.",
                    EntryIds = new() { a.SourceEventId, b.SourceEventId },
                    PersonIds = new() { a.PromisorPersonId, b.PromisorPersonId }
                });
            }
        }

        // Proposals close to lapsing (24 hours with no answer).
        foreach (var ev in slice.Events.Where(e => e.ConfirmationState == ConfirmationState.Proposed))
        {
            var age = DateTime.UtcNow - ev.CreatedAtUtc;
            if (age < TimeSpan.FromHours(12) || age > TimeSpan.FromHours(24))
            {
                continue;
            }
            var left = TimeSpan.FromHours(24) - age;
            findings.Add(new ReviewFinding
            {
                Kind = "unanswered",
                Headline = ev.AuthorPersonId is int author
                    ? $"{Name(author)}'s message lapses in {Math.Max(1, (int)left.TotalHours)} hours."
                    : $"A note to the house lapses in {Math.Max(1, (int)left.TotalHours)} hours.",
                Detail = "Nobody has said yes or no. If nobody answers, it is not sent.",
                EntryIds = new() { ev.Id },
                PersonIds = ev.AuthorPersonId is int a ? new() { a } : new()
            });
        }

        // A plan still waiting on someone's answer.
        foreach (var plan in slice.Plans)
        {
            foreach (var element in plan.Elements.Where(e => e.State == PlanElementState.Open))
            {
                var required = PlanAssemblyService.RequiredDisposers(element);
                var answered = element.Agreements.Select(a => a.PersonId).ToHashSet();
                var waiting = required.Where(id => !answered.Contains(id)).ToList();
                if (waiting.Count == 0)
                {
                    continue;
                }
                findings.Add(new ReviewFinding
                {
                    Kind = "plan_gap",
                    Headline = $"“{plan.Label}” is waiting on {string.Join(" and ", waiting.Select(w => Name(w)))}.",
                    Detail = $"{element.BodyText}: no answer yet.",
                    PersonIds = waiting
                });
            }
        }
        return findings.Take(8).ToList();
    }

    // ---------- Muse Wonder ----------

    public async Task<ModelCall?> LatestWonderAsync(int? viewerId) =>
        await _db.ModelCalls
            .Where(c => c.Purpose == ModelCallPurpose.Review && c.PersonId == viewerId)
            .OrderByDescending(c => c.Id).FirstOrDefaultAsync();

    // Ask Muse Wonder for a review as this person. Returns the stored
    // call (done, empty, or failed); a recent review is returned as it
    // is rather than asked again.
    public async Task<ModelCall> AskWonderAsync(Slice slice, int? viewerId, CancellationToken ct = default)
    {
        var latest = await LatestWonderAsync(viewerId);
        if (latest is not null && latest.Outcome != ModelCallOutcome.Failed
            && DateTime.UtcNow - latest.AtUtc < TimeSpan.FromMinutes(_api.Models.ReviewMinutesBetween))
        {
            return latest;
        }
        var call = new ModelCall
        {
            HouseholdId = slice.Household.Id,
            Purpose = ModelCallPurpose.Review,
            Model = _api.Models.ReviewModel,
            Engine = "live",
            PersonId = viewerId,
            AtUtc = DateTime.UtcNow
        };
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            var shown = BuildRequestSlice(slice, viewerId);
            var raw = await CallWonderAsync(shown, ct);
            var result = Validate(raw, slice, shown, viewerId);
            call.Outcome = result.Findings.Count == 0 ? ModelCallOutcome.Empty : ModelCallOutcome.Done;
            call.OutputJson = JsonSerializer.Serialize(result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            call.Outcome = ModelCallOutcome.Failed;
            call.OutputJson = JsonSerializer.Serialize(new WonderResult
            {
                Problem = ex is HttpRequestException or TaskCanceledException
                    ? "The Meta Model API did not answer in time."
                    : "Muse Wonder's answer could not be used."
            });
        }
        call.ElapsedMs = (int)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _db.ModelCalls.Add(call);
        await _db.SaveChangesAsync(ct);
        return call;
    }

    // What Wonder is shown: the shareable cut only, as compact JSON
    // with ids, the household clock, and computed supply dates.
    public static JsonObject BuildRequestSlice(Slice slice, int? viewerId)
    {
        string Local(DateTime utc) => HouseholdTime.ToLocal(utc, slice.Zone).ToString("ddd yyyy-MM-dd HH:mm");
        var viewer = slice.People.FirstOrDefault(p => p.Id == viewerId);
        return new JsonObject
        {
            ["now"] = Local(DateTime.UtcNow),
            ["viewer"] = viewer is null ? "the household (no one in particular)" : $"{FirstName(viewer)} (person {viewer.Id})",
            ["people"] = new JsonArray(slice.People.Select(p => (JsonNode)new JsonObject
            {
                ["id"] = p.Id,
                ["name"] = FirstName(p),
                ["role"] = p.Role.ToString(),
                ["child"] = p.MinorFlag
            }).ToArray()),
            ["entries"] = new JsonArray(slice.Events.Where(slice.ShareableEvent)
                .OrderByDescending(e => e.Id).Take(60).Select(e => (JsonNode)new JsonObject
                {
                    ["id"] = e.Id,
                    ["type"] = e.Type.ToString(),
                    ["words"] = e.Body,
                    ["by"] = e.AuthorPersonId,
                    ["about"] = e.SubjectPersonId,
                    ["state"] = e.ConfirmationState.ToString(),
                    ["at"] = Local(e.CreatedAtUtc)
                }).ToArray()),
            ["claims"] = new JsonArray(slice.Promises.Where(slice.ShareablePromise).Select(p => (JsonNode)new JsonObject
            {
                ["entry_id"] = p.SourceEventId,
                ["by"] = p.PromisorPersonId,
                ["for"] = p.PromiseePersonId,
                ["words"] = p.CommitmentText,
                ["due"] = p.DueText
            }).ToArray()),
            ["supplies"] = new JsonArray(slice.Supplies.Where(slice.ShareableSupply).Select(s => (JsonNode)new JsonObject
            {
                ["id"] = s.Id,
                ["name"] = s.Name,
                ["on_hand"] = $"{s.QuantityOnHand:0.#} {s.Unit}",
                ["runs_out"] = s.GetRunOutDate(slice.Today) == DateOnly.MaxValue ? null : s.GetRunOutDate(slice.Today).ToString("ddd yyyy-MM-dd"),
                ["order_by"] = s.GetOrderByDate(slice.Today) == DateOnly.MaxValue ? null : s.GetOrderByDate(slice.Today).ToString("ddd yyyy-MM-dd"),
                ["looked_after_by"] = s.AssignedPersonId
            }).ToArray()),
            ["plans"] = new JsonArray(slice.Plans.Select(p => (JsonNode)new JsonObject
            {
                ["label"] = p.Label,
                ["at"] = p.AnchorAtLocal?.ToString("ddd HH:mm"),
                ["open_items"] = new JsonArray(p.Elements.Where(e => e.State == PlanElementState.Open).Select(e => (JsonNode)new JsonObject
                {
                    ["what"] = e.BodyText,
                    ["waiting_on"] = new JsonArray(PlanAssemblyService.RequiredDisposers(e)
                        .Where(id => e.Agreements.All(a => a.PersonId != id))
                        .Select(id => (JsonNode)JsonValue.Create(id)).ToArray())
                }).ToArray())
            }).ToArray())
        };
    }

    public const string WonderInstructions = """
        You review a household's shared coordination record for one person, the viewer.
        Find at most five things a person should look at now:
          conflict      two people have claimed the same task, or two entries cannot both be true;
          slipping      a claim or request that is due soon and shows no sign of being done;
          running_low   a shared supply whose order_by date is today or past;
          unanswered    a proposal still waiting for someone's yes or no;
          plan_gap      a plan item still waiting on someone's answer.
        Rules:
          - Use only the record given. Cite the entry ids (and supply ids) each finding relies on.
          - Never give medical, legal, or financial advice. Never guess at anyone's health or feelings.
          - Name people only by the names given. Write plainly, without blame.
          - A draft is one short message the viewer could send to one household member, in plain words,
            or null when nobody needs telling. Never draft to the viewer themself.
          - If nothing needs attention, return an empty findings list.
        """;

    private static readonly JsonObject WonderSchema = BuildSchema();

    private static JsonObject BuildSchema()
    {
        JsonObject Str() => new() { ["type"] = "string" };
        JsonObject Ints() => new() { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "integer" } };
        var draft = new JsonObject
        {
            ["type"] = new JsonArray("object", "null"),
            ["properties"] = new JsonObject
            {
                ["to_person_id"] = new JsonObject { ["type"] = "integer" },
                ["message"] = Str()
            },
            ["required"] = new JsonArray("to_person_id", "message"),
            ["additionalProperties"] = false
        };
        var finding = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("conflict", "slipping", "running_low", "unanswered", "plan_gap") },
                ["headline"] = Str(),
                ["detail"] = Str(),
                ["entry_ids"] = Ints(),
                ["supply_ids"] = Ints(),
                ["person_ids"] = Ints(),
                ["draft"] = draft
            },
            ["required"] = new JsonArray("kind", "headline", "detail", "entry_ids", "supply_ids", "person_ids", "draft"),
            ["additionalProperties"] = false
        };
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["summary"] = Str(),
                ["findings"] = new JsonObject { ["type"] = "array", ["items"] = finding }
            },
            ["required"] = new JsonArray("summary", "findings"),
            ["additionalProperties"] = false
        };
    }

    private async Task<string> CallWonderAsync(JsonObject shown, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = _api.Models.ReviewModel,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = WonderInstructions },
                new JsonObject { ["role"] = "user", ["content"] = shown.ToJsonString() }
            },
            ["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject
                {
                    ["name"] = "household_review",
                    ["strict"] = true,
                    ["schema"] = WonderSchema.DeepClone()
                }
            },
            ["reasoning_effort"] = _api.Models.ReviewEffort,
            // Reasoning tokens count toward the output cap.
            ["max_tokens"] = 8192
        };
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(180) };
        using var request = _api.Request(HttpMethod.Post, "/chat/completions");
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(MetaApi.Failure(response.StatusCode, text));
        }
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()
            ?? throw new InvalidOperationException("Muse Wonder returned no content.");
    }

    // The house's checks on Wonder's answer. A finding survives only
    // when every entry and supply it cites was in what Wonder was
    // shown, every person it names is in the household, its words are
    // not health-shaped, and its draft (if any) goes to a household
    // member other than the viewer. Nothing Wonder says is acted on.
    public static WonderResult Validate(string raw, Slice slice, JsonObject shown, int? viewerId)
    {
        var entryIds = (shown["entries"] as JsonArray ?? new()).Select(n => (int)n!["id"]!).ToHashSet();
        foreach (var claim in shown["claims"] as JsonArray ?? new())
        {
            entryIds.Add((int)claim!["entry_id"]!);
        }
        var supplyIds = (shown["supplies"] as JsonArray ?? new()).Select(n => (int)n!["id"]!).ToHashSet();
        var people = slice.People.Select(p => p.Id).ToHashSet();
        var result = new WonderResult();
        WonderAnswer? answer;
        try
        {
            answer = JsonSerializer.Deserialize<WonderAnswer>(raw, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            result.Problem = "Muse Wonder's answer was not in the agreed shape.";
            return result;
        }
        if (answer is null)
        {
            result.Problem = "Muse Wonder returned nothing.";
            return result;
        }
        result.Summary = Clip(answer.Summary, 200);
        if (PrivacyRule.WordsArePrivate(result.Summary))
        {
            result.Summary = null;
        }
        var dropped = 0;
        foreach (var f in answer.Findings ?? new())
        {
            var cites = (f.EntryIds ?? new()).Count + (f.SupplyIds ?? new()).Count;
            var ok = cites > 0
                && (f.EntryIds ?? new()).All(entryIds.Contains)
                && (f.SupplyIds ?? new()).All(supplyIds.Contains)
                && (f.PersonIds ?? new()).All(people.Contains)
                && !string.IsNullOrWhiteSpace(f.Headline)
                && !PrivacyRule.WordsArePrivate(f.Headline + " " + f.Detail + " " + f.Draft?.Message);
            if (ok && f.Draft is not null)
            {
                ok = people.Contains(f.Draft.ToPersonId)
                    && f.Draft.ToPersonId != viewerId
                    && !string.IsNullOrWhiteSpace(f.Draft.Message);
            }
            if (!ok)
            {
                dropped++;
                continue;
            }
            result.Findings.Add(new ReviewFinding
            {
                Kind = f.Kind ?? "conflict",
                Headline = Clip(f.Headline, 140)!,
                Detail = Clip(f.Detail, 280) ?? "",
                EntryIds = f.EntryIds ?? new(),
                SupplyIds = f.SupplyIds ?? new(),
                PersonIds = f.PersonIds ?? new(),
                DraftTo = f.Draft?.ToPersonId,
                DraftMessage = Clip(f.Draft?.Message, 220)
            });
            if (result.Findings.Count == 5)
            {
                break;
            }
        }
        result.Dropped = dropped;
        return result;
    }

    // Marks a Wonder draft as sent: the message it became, and
    // whether the person changed the words first. Read by the trace.
    public static async Task MarkDraftSentAsync(AppDbContext db, int callId, int index, int readingId, bool edited)
    {
        var call = await db.ModelCalls.FirstOrDefaultAsync(c => c.Id == callId && c.Purpose == ModelCallPurpose.Review);
        if (call?.OutputJson is null)
        {
            return;
        }
        var result = JsonSerializer.Deserialize<WonderResult>(call.OutputJson);
        if (result is null || index < 0 || index >= result.Findings.Count)
        {
            return;
        }
        result.Findings[index].SentReadingId = readingId;
        result.Findings[index].SentEdited = edited;
        call.OutputJson = JsonSerializer.Serialize(result);
        await db.SaveChangesAsync();
    }

    public static WonderResult? Read(ModelCall call)
    {
        if (call.OutputJson is null)
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<WonderResult>(call.OutputJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string FirstName(Person p) => p.DisplayName.Split(' ')[0];

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static string? Clip(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }
        s = s.Trim();
        return s.Length <= max ? s : s[..(max - 1)].TrimEnd() + "…";
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "i", "i'll", "ill", "will", "the", "a", "an", "to", "at", "on", "in", "for", "and", "it", "up", "get", "do", "be", "by", "of", "my", "me"
    };

    private static double Overlap(string a, string b)
    {
        static HashSet<string> Words(string s) => s.ToLowerInvariant()
            .Split(new[] { ' ', ',', '.', '!', '?', '’', '\'' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !StopWords.Contains(w)).ToHashSet();
        var wa = Words(a);
        var wb = Words(b);
        if (wa.Count == 0 || wb.Count == 0)
        {
            return 0;
        }
        return wa.Intersect(wb).Count() / (double)wa.Union(wb).Count();
    }
}

public sealed class ReviewFinding
{
    public string Kind { get; set; } = "";
    public string Headline { get; set; } = "";
    public string Detail { get; set; } = "";
    public List<int> EntryIds { get; set; } = new();
    public List<int> SupplyIds { get; set; } = new();
    public List<int> PersonIds { get; set; } = new();
    public int? DraftTo { get; set; }
    public string? DraftMessage { get; set; }
    public int? SentReadingId { get; set; }
    public bool SentEdited { get; set; }
}

public sealed class WonderResult
{
    public string? Summary { get; set; }
    public List<ReviewFinding> Findings { get; set; } = new();
    public int Dropped { get; set; }
    public string? Problem { get; set; }
}

internal sealed class WonderAnswer
{
    public string? Summary { get; set; }
    public List<WonderAnswerFinding>? Findings { get; set; }
}

internal sealed class WonderAnswerFinding
{
    public string? Kind { get; set; }
    public string? Headline { get; set; }
    public string? Detail { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("entry_ids")]
    public List<int>? EntryIds { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("supply_ids")]
    public List<int>? SupplyIds { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("person_ids")]
    public List<int>? PersonIds { get; set; }
    public WonderAnswerDraft? Draft { get; set; }
}

internal sealed class WonderAnswerDraft
{
    [System.Text.Json.Serialization.JsonPropertyName("to_person_id")]
    public int ToPersonId { get; set; }
    public string? Message { get; set; }
}
