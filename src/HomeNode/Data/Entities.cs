namespace HomeNode.Data;

// Household, then Person, then Event. Events are told to the house; the
// person links on an event are optional attribution, recorded when known
// and when they matter (see Event). The household circle is whoever the
// house has let in: family, helpers, and anyone else coordinating in.
//
// The recipient of every telling is the HOUSE. There is deliberately
// no recipient role on a person: people
// tell the house, the house holds it, and who gets told afterward is
// the house's decision under its discretion rules, never a role label
// pinned on a person. A person's support needs live in the record as
// facts and events, the same as anyone else's. Role says only how a
// person relates to the house: family, or a helper let into the circle.

public enum PersonRole
{
    Family,
    Helper
}

public enum EventType
{
    Request,
    HouseholdNote,
    Supply,
    Journal,
    FacilityNotice,
    Whereabouts,
    HealthMention,
    // The speaker's own future action, stated in their own words.
    // A Commitment is the birth record of a Promise object; its maker
    // is required, and a clause with no identifiable maker is never a
    // Commitment.
    Commitment,
    // Household work that needs doing and is not yet anyone's stated
    // promise. TaskKind in Details is General, Pickup, or
    // Errand. A pickup is a Task, never a Whereabouts and never a Request.
    Task
}

// An event that carries a proposed action moves through these states only
// when a person acts on it. A statement-only event has nothing to confirm,
// so it is written already Confirmed, with its author as the confirmer of
// record; there is deliberately no fifth state. Wording rule for any surface that shows these states: a confirmed
// state reads as confirmed by a person.
public enum ConfirmationState
{
    Proposed,
    Confirmed,
    Done,
    NotConfirmed
}

public class Household
{
    public int Id { get; set; }
    public string Name { get; set; } = "";

    // The house's own clock. Seeded event times are the scenario's wall
    // clock in this zone (the demo day is seeded in America/Chicago);
    // the field exists so time handling has a home instead of an assumption.
    public string TimeZoneId { get; set; } = "America/Chicago";

    // Routing defaults carried in the extraction context slice: who a
    // health mention is routed to first, and who stands behind
    // them. Routing lives on the household, never on a person's role.
    public int? DefaultRoutePersonId { get; set; }
    public Person? DefaultRoutePerson { get; set; }
    public int? BackupRoutePersonId { get; set; }
    public Person? BackupRoutePerson { get; set; }

    // The alert level that applies to health mentions, by its level key
    // (see AlertLevel). The person's own choice, set in advance.
    public string? HealthMentionLevelKey { get; set; }

    public List<Person> Persons { get; set; } = new();
    public List<Event> Events { get; set; } = new();
    public List<SupplyItem> SupplyItems { get; set; } = new();
    public List<TaskNote> TaskNotes { get; set; } = new();
    public List<JournalPrompt> JournalPrompts { get; set; } = new();
    public List<HouseholdSchedule> Schedules { get; set; } = new();
    public List<AlertLevel> AlertLevels { get; set; } = new();
    public List<GlossaryTerm> GlossaryTerms { get; set; } = new();
    public List<Promise> Promises { get; set; } = new();
    public List<Plan> Plans { get; set; } = new();
}

public class Person
{
    public int Id { get; set; }
    public int HouseholdId { get; set; }
    public Household? Household { get; set; }

    public string DisplayName { get; set; } = "";
    public PersonRole Role { get; set; }

    // True for the household's children: a child is a first-class
    // teller and subject, and the flag is how surfaces and rules know
    // a person is a minor without a role word ever saying so.
    public bool MinorFlag { get; set; }
}

public class Event
{
    public int Id { get; set; }
    public int HouseholdId { get; set; }
    public Household? Household { get; set; }

    // Who this event is about, and who added it. BOTH ARE OPTIONAL.
    // Events are told to the house, not to a person: a supply running
    // low, a house notice, an open ask to whoever can act. Attribution
    // is recorded when it is known and matters; its absence is a true
    // fact about the utterance, not missing data, and no person link
    // is ever invented to satisfy the schema. Routing comes from
    // supply assignment and roles, never from a forced subject.
    // House-generated entries (order-by lines from the supply
    // arithmetic) likewise have no human author.
    public int? SubjectPersonId { get; set; }
    public Person? SubjectPerson { get; set; }
    public int? AuthorPersonId { get; set; }
    public Person? AuthorPerson { get; set; }

    public EventType Type { get; set; }

    // The words as they were said or written, kept plain. When one spoken
    // turn yields several events, each event keeps the clause that produced
    // it, still in the speaker's own words.
    public string Body { get; set; } = "";

    // Type-specific structured data from extraction, stored as JSON text so
    // it rides on the event without flattening into Body. The shapes,
    // by type:
    //   Request: RequestKind (Relay, Media, Reminder, Bring, Other), and
    //     TargetPersonId when the kind is Relay.
    //   HouseholdNote: MedMention (None, Casual, ScheduleStatement,
    //     SelfReport) and ScheduleRef when MedMention is ScheduleStatement.
    //     DecisionTopic and DecisionState (Undecided, Decided, with
    //     DecidedText when decided) when the note carries an open household
    //     decision: an undecided dinner has structured existence for
    //     plan assembly.
    //   Supply: Direction (Low, Out, Used, Restocked), QuantityText in the
    //     speaker's words, UnresolvedItem when the item cannot be matched
    //     (an unresolved item stays a Supply event carrying UnresolvedItem;
    //     it never falls back to a HouseholdNote).
    //   Journal: EntryKind (PromptAnswer, FreeEntry) and PromptId.
    //   FacilityNotice: SourceName and ChangeText, as spoken.
    //   Whereabouts: PlaceText, Motion (At, HeadingTo, HeadingHome,
    //     Leaving, Arrived, Home; Arrived and Home are terminal), and
    //     NextText when spoken.
    //   HealthMention: Form (Statement, Question). Nothing further, ever.
    //   Commitment: CommitmentText (the clause verbatim, required),
    //     DueText and DueAtLocal when spoken and resolvable, and
    //     PromiseePersonId only when a promisee is named.
    //   Task: TaskKind (General, Pickup, Errand) and TaskText in the
    //     speaker's words; WhenText or DueText and DueAtLocal when spoken;
    //     CollectPersonId and PlaceText for Pickup; HelpFlag when the
    //     speaker says someone is stuck or help is wanted; NeedsAssignee
    //     when the telling names nobody to do it.
    public string? Details { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public ConfirmationState ConfirmationState { get; set; } = ConfirmationState.Proposed;

    // Provenance for an event that arrived through a member's own
    // channel (a federated event): where it came from, which
    // connection carried it, and which packet it arrived in. All
    // three are null for a telling made on the house layer itself,
    // and surfaces render provenance only when it is present, so a
    // plain local entry never borrows a federated origin. The values
    // are recorded by the node when the packet is opened, exactly as
    // reported by the ingress, and are part of the record the
    // audience rules filter like any other field of the event.
    // (The field names and semantics are kept exactly, so the
    // record is the one provenance source of truth; rendering
    // is ProvenanceRendering.Describe. A packet view is an
    // observability surface over traffic, not a second source.)
    public string? ProvenanceOrigin { get; set; }
    public string? ProvenanceConnection { get; set; }
    public string? ProvenancePacketId { get; set; }

    // Home node additions beside the provenance fields. A federated
    // act is recorded as reported, not observed, and the node keeps
    // told time distinct from recorded time: ToldAtUtc is when the
    // teller told it, RecordedAtUtc is when the node took it into
    // the record. The two differ exactly when a telling waited
    // queued, unrecorded, before the node opened it. Both are null
    // for house layer tellings, whose CreatedAtUtc already carries
    // their time.
    public bool ReportedNotObserved { get; set; }
    public DateTime? ToldAtUtc { get; set; }
    public DateTime? RecordedAtUtc { get; set; }

    // Discretion, stored at write time: how
    // widely this event may be seen, computed once when the event lands
    // and never widened afterward. HealthMention is Restricted, always;
    // Journal is Personal unless the speaker marks it shared; the rest
    // default to Household.
    public SensitivityClass SensitivityClass { get; set; } = SensitivityClass.Household;

    // The audience rule computed with the class, as JSON: the named
    // persons, the route person, and the backup, in record terms.
    public string? AudienceRule { get; set; }

    // True while a person-free event's fact is on the record but not yet
    // closed by a person: the fact counts tentatively
    // for forecasts and lists, the entry stays visible as unconfirmed,
    // and lapse never discards it.
    public bool RecordPending { get; set; }

    // True while an audience ask (audience decision clause 7) about this
    // event is unanswered or unpresented: the event is stored at the
    // narrowest audience its ceiling permits until the entitled
    // person answers, and an unanswered ask never widens it.
    public bool AudienceAskPending { get; set; }

    // Set when an event concerns a tracked household supply (for example a
    // note that the wipes are running low).
    public int? RelatedSupplyItemId { get; set; }
    public SupplyItem? RelatedSupplyItem { get; set; }

    // HealthMention events are record and hand off only: the words are kept
    // and passed to a family member. No commentary of any kind is generated
    // about them, and there is deliberately no field for any.
}

public class SupplyItem
{
    public int Id { get; set; }
    public int HouseholdId { get; set; }
    public Household? Household { get; set; }

    public string Name { get; set; } = "";
    public string Unit { get; set; } = "";

    // The other words the household uses for this item, as a JSON array of
    // strings ("the wipes", "evening ones"). Extraction resolves a spoken
    // item against the name and these aliases, from the context slice;
    // an item matched by neither stays unresolved on its event.
    public string? Aliases { get; set; }

    // Seeded by a person who knows the household's rhythm; the product does
    // not guess a rate. All reorder arithmetic runs off this seed.
    public decimal SeededDailyUsageRate { get; set; }
    public decimal QuantityOnHand { get; set; }

    // How many days ahead a person must order so the refill lands in time.
    public int ReorderLeadDays { get; set; }

    // The household member who picks this item up, when one is named.
    public int? AssignedPersonId { get; set; }
    public Person? AssignedPerson { get; set; }

    // The day the shelf runs empty at the seeded rate, counted the way a
    // person counts: whole days of supply on hand, added to today. Twelve
    // wipes at five a day from Tuesday morning run out on Thursday. This
    // arithmetic is plain deterministic code over the fields above, the
    // same every time it runs; extraction never produces these dates, it
    // only records the counts and rates a person states.
    public DateOnly GetRunOutDate(DateOnly today)
    {
        if (SeededDailyUsageRate <= 0)
        {
            return DateOnly.MaxValue;
        }
        var wholeDaysOnHand = (int)Math.Floor(QuantityOnHand / SeededDailyUsageRate);
        return today.AddDays(wholeDaysOnHand);
    }

    // The day to order by: run out day minus the lead a person needs.
    public DateOnly GetOrderByDate(DateOnly today)
    {
        var runOut = GetRunOutDate(today);
        return runOut == DateOnly.MaxValue ? runOut : runOut.AddDays(-ReorderLeadDays);
    }
}

// Household notes that answer by task: whoever is helping taps what they are
// doing and sees only the few notes that matter for that task.
public class TaskNote
{
    public int Id { get; set; }
    public int HouseholdId { get; set; }
    public Household? Household { get; set; }

    public string TaskKey { get; set; } = "";
    public string NoteText { get; set; } = "";
    public int SortOrder { get; set; }
}

// Journaling prompts a person in the house sets for herself. A prompt she
// turns off stays off; a day with no entry is simply a day with no entry.
// Dormant in this build: the table is seeded, but no surface offers a
// prompt and nothing reads the table; prompts are not a feature here.
public class JournalPrompt
{
    public int Id { get; set; }
    public int HouseholdId { get; set; }
    public Household? Household { get; set; }

    public string Text { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

// How widely an event may be seen. Computed
// once at write time and stored on the event; a later grant or valve may
// narrow or route within an audience, never add a person the class
// excludes.
public enum SensitivityClass
{
    Open,
    Household,
    Named,
    Restricted,
    Personal
}

// One append-only audit row per state change: who decided, through
// which channel, and when. ActorPersonId is
// null only for executor and lapse rows. FromState is null on the write
// row that brings an event (or a promise, via ObjectKind) into being.
public class EventAudit
{
    public int Id { get; set; }

    // The record object this row is about: "Event", "Promise", or
    // "AlertDelivery" (keyed by the delivery's record event).
    public string ObjectKind { get; set; } = "Event";
    public int EventId { get; set; }
    public Event? Event { get; set; }

    public ConfirmationState? FromState { get; set; }
    public string ToState { get; set; } = "";
    public int? ActorPersonId { get; set; }
    public Person? ActorPerson { get; set; }
    public DateTime AtUtc { get; set; }

    // One of: voice, tap, executor, lapse, federated, member. For a
    // statement-only write the channel is the input's source type
    // (voice or tap); "member" names the member surface, where a
    // delivery acknowledgement is stamped.
    public string Channel { get; set; } = "";
}

// A proposed action attached to an event.
// An action does nothing until a person confirms it; the confirmation
// service is the only code that moves one.
public enum EventActionType
{
    RelayMessage,
    MediaPlay,
    CreateReminder,
    NotifyPerson
}

public class EventAction
{
    public int Id { get; set; }
    public int EventId { get; set; }
    public Event? Event { get; set; }

    public EventActionType ActionType { get; set; }
    public int? TargetPersonId { get; set; }
    public Person? TargetPerson { get; set; }

    // The action payload, in its per-type shape, as JSON text.
    public string? Payload { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

// A delivered alert: the exception surface.
// Carries the event Body verbatim and nothing else. At most one alert per
// event, ever; a backup hop is the same alert re-addressed, not a new one.
public class AlertDelivery
{
    public int Id { get; set; }
    public int EventId { get; set; }
    public Event? Event { get; set; }
    public int? EventActionId { get; set; }
    public EventAction? EventAction { get; set; }

    public string LevelKey { get; set; } = "";
    public int ToPersonId { get; set; }
    public Person? ToPerson { get; set; }
    public string BodyText { get; set; } = "";
    public DateTime DeliveredAtUtc { get; set; }
    public DateTime? AcknowledgedAtUtc { get; set; }
    public bool IsBackupHop { get; set; }
}

// A schedule a person set: a label, a local
// time, and who set it. ScheduleRef on an event is copied from these rows,
// never from a model's own knowledge.
public class HouseholdSchedule
{
    public int Id { get; set; }
    public int HouseholdId { get; set; }
    public Household? Household { get; set; }

    public string Label { get; set; } = "";
    public int SetByPersonId { get; set; }
    public Person? SetByPerson { get; set; }

    // Local wall-clock time as spoken and set, "08:00".
    public string TimeLocal { get; set; } = "";
}

// An alert level in the person's own words: "now" pushes to the
// route person, "brief" rides the morning
// brief, "here" stays on the timeline. Levels are set in advance by a
// person, never by an utterance.
public class AlertLevel
{
    public int Id { get; set; }
    public int HouseholdId { get; set; }
    public Household? Household { get; set; }

    public string LevelKey { get; set; } = "";
    public string Label { get; set; } = "";
    public int? RouteToPersonId { get; set; }
    public Person? RouteToPerson { get; set; }
    public int? BackupPersonId { get; set; }
    public Person? BackupPerson { get; set; }
}

// A household glossary entry: what a
// household phrase means in this house ("my program"). Supplied to the
// pipeline as part of the context slice; also where the household's own
// affirmative words for a spoken "yes" live.
public class GlossaryTerm
{
    public int Id { get; set; }
    public int HouseholdId { get; set; }
    public Household? Household { get; set; }

    public string Term { get; set; } = "";
    public string Meaning { get; set; } = "";
}

// One recorded change to a supply count, made when a Supply event lands:
// which item, what direction, the count afterward, and who said so. The
// supply arithmetic stays deterministic; this is its paper trail.
public class SupplyAdjustment
{
    public int Id { get; set; }
    public int SupplyItemId { get; set; }
    public SupplyItem? SupplyItem { get; set; }
    public int? EventId { get; set; }
    public Event? Event { get; set; }
    public int? ActorPersonId { get; set; }
    public Person? ActorPerson { get; set; }

    public string Direction { get; set; } = "";
    public decimal? QuantityOnHandAfter { get; set; }
    public DateTime AtUtc { get; set; }
}

// A promise: a commitment on the timeline,
// first class. Born by statement: stating it to the house makes it. The
// closing states (Kept, Broken, Released) are set only by a person's
// confirmed closing link, never by the clock and never by inference; a
// past-due promise still Made surfaces in neutral words as still open.
public enum PromiseState
{
    Made,
    Kept,
    Broken,
    Released
}

public class Promise
{
    public int Id { get; set; }
    public int HouseholdId { get; set; }
    public Household? Household { get; set; }

    // A promise needs a maker, and a maker is never inferred: a
    // speakerless utterance can never create one.
    public int PromisorPersonId { get; set; }
    public Person? PromisorPerson { get; set; }

    // Null means promised to the house: spoken into the shared record.
    // A promisee is never invented to fill the field.
    public int? PromiseePersonId { get; set; }
    public Person? PromiseePerson { get; set; }

    // The clause in the maker's verbatim words; the full utterance stays
    // on the source event's Body.
    public string CommitmentText { get; set; } = "";
    public string? DueText { get; set; }
    public DateTime? DueAtLocal { get; set; }

    public int SourceEventId { get; set; }
    public Event? SourceEvent { get; set; }

    public PromiseState State { get; set; } = PromiseState.Made;

    // The closing link, written once at confirmation, never edited after.
    public int? ClosedByEventId { get; set; }
    public Event? ClosedByEvent { get; set; }
    public int? ClosedByPersonId { get; set; }
    public Person? ClosedByPerson { get; set; }
    public DateTime? ClosedAtUtc { get; set; }
}

// A plan: elements assembled from the record
// against one anchor deadline, where each element that needs agreement
// carries its own disposals and silence never agrees. Schema in this
// build; plan assembly is downstream work.
public enum PlanState
{
    Assembling,
    Proposed,
    Acknowledged,
    Completed,
    Superseded,
    Dropped
}

public enum PlanElementKind
{
    MealProposal,
    ShoppingList,
    LeaveBy,
    Pickup,
    Task,
    Errand
}

public enum PlanElementState
{
    Open,
    Settled,
    Dropped
}

public enum PlanDisposal
{
    Agreed,
    Declined,
    Countered
}

public class Plan
{
    public int Id { get; set; }
    public int HouseholdId { get; set; }
    public Household? Household { get; set; }

    public string Label { get; set; } = "";
    public int? AnchorEventId { get; set; }
    public Event? AnchorEvent { get; set; }
    public DateTime? AnchorAtLocal { get; set; }

    // The person the plan as a whole lands with, distinct from element
    // assignees. The runner's tap is an acknowledgement ("I have this
    // plan") that activates the plan; binding consents live per element.
    public int? RunnerPersonId { get; set; }
    public Person? RunnerPerson { get; set; }

    public PlanState State { get; set; } = PlanState.Assembling;
    public List<PlanElement> Elements { get; set; } = new();
}

public class PlanElement
{
    public int Id { get; set; }
    public int PlanId { get; set; }
    public Plan? Plan { get; set; }

    public PlanElementKind Kind { get; set; }

    // The record events this element is built from, as a JSON array of
    // event ids.
    public string? SourceEventIds { get; set; }

    // The persons whose disposal this element requires, as a JSON array
    // of person ids, set when the element is proposed. An element
    // settles only when every one of them has agreed in the current
    // round; silence never agrees.
    public string? RequiredDisposerIds { get; set; }

    // The element's content in record words.
    public string BodyText { get; set; } = "";
    public int? AssigneePersonId { get; set; }
    public Person? AssigneePerson { get; set; }
    public PlanElementState State { get; set; } = PlanElementState.Open;
    public List<PlanAgreement> Agreements { get; set; } = new();
}

// Why a plan element landed in a person's member view (the in-demo
// delivery path): the plan was acknowledged by its
// runner, the element settled with everyone's agreement, or the
// element was re-timed and the change was routed to the person it
// affects.
public enum PlanDeliveryReason
{
    PlanAcknowledged,
    ElementSettled,
    ElementRetimed,
    CounterCarried
}

// One landing of one plan element in one person's member view, inside
// this one-computer demo only. This is the demo delivery path, not the
// product's delivery mechanism: the product's design reaches a person
// through that person's own assistant, in their own channel, and that
// cross-channel delivery is not built here. The row
// carries the element's words as they stood at the moment of landing,
// so a later edit never rewrites what a person actually received. At
// most one landing per element, person, and wording: the same words
// never land twice, and changed words land as a new landing.
public class PlanDelivery
{
    public int Id { get; set; }
    public int PlanElementId { get; set; }
    public PlanElement? PlanElement { get; set; }
    public int ToPersonId { get; set; }
    public Person? ToPerson { get; set; }
    public PlanDeliveryReason Reason { get; set; }

    // Who made the change that caused this landing, when a person did
    // (a re-time). Null for landings caused by the plan machinery
    // itself (acknowledgement, settlement).
    public int? ActorPersonId { get; set; }
    public Person? ActorPerson { get; set; }

    // The element's words at the moment of landing, verbatim.
    public string BodyText { get; set; } = "";
    public DateTime DeliveredAtUtc { get; set; }
}

// The standing audience default a person may hold (audience decision clause
// 4): Private resolves the holder's unspecified tellings to Personal,
// Household leaves them at the shape's class, Ask asks the holder
// before the audience is set. No default can select Open; the widest
// audience a default may reach is the household. The narrowness rank
// is Private narrowest, then Ask, then Household widest; narrowing
// and widening in clause 8 are judged by that rank.
public enum AudienceDefaultValue
{
    Private,
    Ask,
    Household
}

public static class AudienceDefaultRank
{
    public static int Of(AudienceDefaultValue value) => value switch
    {
        AudienceDefaultValue.Private => 0,
        AudienceDefaultValue.Ask => 1,
        AudienceDefaultValue.Household => 2,
        _ => 2
    };
}

// One person's standing audience default, at most one row per holder.
// Set or changed only through AudienceDefaultRequest rows that carry
// the telling, the read-back, and the recorded disposal (clause 4).
// A change is forward-looking only: no stored event is ever touched
// when a default changes (clauses 1 and 4).
public class PersonAudienceDefault
{
    public int Id { get; set; }
    public int HouseholdId { get; set; }
    public Household? Household { get; set; }
    public int HolderPersonId { get; set; }
    public Person? HolderPerson { get; set; }
    public AudienceDefaultValue Value { get; set; }
    public DateTime SetAtUtc { get; set; }
}

// The state of one default-setting telling. PendingReadBack waits for
// the holder's disposal of the read-back. ProposalWaiting is an
// instruction about someone else's default (or an adult's proposal
// to widen a minor's): recorded as a proposal to the holder and never
// applied by the instruction itself; only the holder's own later act
// can apply it. Applied and Declined are terminal disposals.
public enum AudienceDefaultRequestState
{
    PendingReadBack,
    ProposalWaiting,
    Applied,
    Declined
}

// One telling that set, tried to set, or proposed a person's audience
// default: the record of the telling, the read-back the house gave,
// and the disposal a person recorded. This row, not a settings page,
// is how a default changes (clause 4).
public class AudienceDefaultRequest
{
    public int Id { get; set; }
    public int HouseholdId { get; set; }
    public Household? Household { get; set; }
    public int HolderPersonId { get; set; }
    public Person? HolderPerson { get; set; }
    public int RequestedByPersonId { get; set; }
    public Person? RequestedByPerson { get; set; }
    public AudienceDefaultValue Value { get; set; }
    public AudienceDefaultRequestState State { get; set; }
    public string TellingText { get; set; } = "";
    public string? ReadBackText { get; set; }
    public string Channel { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public int? DisposedByPersonId { get; set; }
    public Person? DisposedByPerson { get; set; }
    public DateTime? DisposedAtUtc { get; set; }
}

// The state of one ask (clause 7). Pending is created with the event;
// the lapse clock starts only when the ask is Presented to the person
// entitled to answer it. Answered and Lapsed are terminal; a lapsed
// ask changes nothing, because silence never widens.
public enum AudienceAskState
{
    Pending,
    Presented,
    Answered,
    Lapsed
}

// One ask step (clause 7): the house holds an event at the narrowest
// audience its ceiling permits and asks the one person entitled to
// answer (the speaker for their own telling, the subject for a
// telling about them). The row names no content. BaselineAudienceIds
// and InterimAudienceIds are JSON arrays of person ids recorded at
// write time, so an answer can restore the baseline but never widen
// past it, and a lapse can be checked against the interim set.
public class AudienceAsk
{
    public int Id { get; set; }
    public int EventId { get; set; }
    public Event? Event { get; set; }
    public int AskedPersonId { get; set; }
    public Person? AskedPerson { get; set; }
    public string Reason { get; set; } = "";
    public AudienceAskState State { get; set; } = AudienceAskState.Pending;
    public string BaselineAudienceIds { get; set; } = "[]";
    public string InterimAudienceIds { get; set; } = "[]";

    // The class and rule the event would have carried if the ask had
    // not engaged, recorded at write time. An answer of Household
    // restores exactly this; no answer restores anything wider.
    public string BaselineClass { get; set; } = "";
    public string? BaselineRule { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? PresentedAtUtc { get; set; }
    public string? Answer { get; set; }
    public int? AnsweredByPersonId { get; set; }
    public Person? AnsweredByPerson { get; set; }
    public DateTime? AnsweredAtUtc { get; set; }
}

// One person's disposal of one plan element in one proposal round. The
// current round must be unanimous for the element to settle.
public class PlanAgreement
{
    public int Id { get; set; }
    public int PlanElementId { get; set; }
    public PlanElement? PlanElement { get; set; }
    public int PersonId { get; set; }
    public Person? Person { get; set; }

    public PlanDisposal Disposal { get; set; }
    public int Round { get; set; } = 1;
    public DateTime AtUtc { get; set; }

    // The counter's own words, verbatim, when the disposal is
    // Countered; null otherwise and null for a wordless counter. A
    // counter is a new proposal round: these words are the revised
    // proposal the house carries to the other party, never a
    // paraphrase and never the house's own summary.
    public string? CounterText { get; set; }
}
