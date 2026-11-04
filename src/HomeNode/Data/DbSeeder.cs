namespace HomeNode.Data;

// Seed content for the demo household, mirroring the files in this
// repository's seed folder (personas.md, scenario.md, supplies.json,
// task-notes.json, journal-prompts.json): one ordinary, busy family
// Tuesday. All people are fictional. The day seeded is the most recent
// Tuesday (the suites use Oct 27, 2026; see StoryDay); seed times are
// the scenario's America/Chicago wall clock times, converted to and
// stored as the true UTC instants of those local times, the same
// convention the pipeline uses (ExtractionService stores
// OccurredAtLocal.UtcDateTime). A renderer that converts a stored
// instant back to the household zone therefore shows the scenario's
// clock time. Fields that are local by construction (DueAtLocal and
// other deliberately local fields) stay local and are not converted.
//
// Seeding runs once, on an empty database. To reseed, delete the database
// file and start the app again.
public static class DbSeeder
{
    private static readonly TimeZoneInfo HouseholdZone =
        TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");

    // The true UTC instant of a wall clock time in the household zone.
    // The conversion goes through TimeZoneInfo, so the offset for the
    // date in question (CDT, UTC-5, for Oct 2026) is the zone's, not a
    // hardcoded number.
    private static DateTime ChicagoToUtc(int year, int month, int day, int hour, int minute) =>
        TimeZoneInfo.ConvertTimeToUtc(
            new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified),
            HouseholdZone);

    // The demo's ordinary Tuesday. The suites keep the scenario's own
    // date (Oct 27, 2026); the running node sets it before seeding a new
    // record to the most recent Tuesday before today, so a visitor meets
    // a day that just happened (see UseStoryDayFor).
    public static DateOnly StoryDay { get; set; } = new(2026, 10, 27);

    private static DateTime DemoDay(int hour, int minute) =>
        ChicagoToUtc(StoryDay.Year, StoryDay.Month, StoryDay.Day, hour, minute);

    // A time on a day before the story day (the household's usual
    // meals and the fridge, told in the days before).
    private static DateTime BeforeStoryDay(int daysBefore, int hour, int minute)
    {
        var day = StoryDay.AddDays(-daysBefore);
        return ChicagoToUtc(day.Year, day.Month, day.Day, hour, minute);
    }

    private const string StoryAnchorWords = "Depot by nine, hardware store after, home by three.";

    // Chooses the story day for this node: the day an existing record
    // was seeded with (its first entry's household date), or for a new
    // record the most recent Tuesday strictly before today.
    public static void UseStoryDayFor(AppDbContext db, DateTime utcNow)
    {
        // The scenario's first line of its Tuesday, Daniel at 5:40 AM,
        // dates an existing record.
        var anchor = db.Events.Where(e => e.Body == StoryAnchorWords)
            .OrderBy(e => e.CreatedAtUtc).Select(e => (DateTime?)e.CreatedAtUtc).FirstOrDefault();
        if (anchor is DateTime anchorUtc)
        {
            StoryDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(anchorUtc, HouseholdZone));
            return;
        }
        if (db.Events.Any())
        {
            return;
        }
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utcNow, HouseholdZone));
        var back = ((int)today.DayOfWeek - (int)DayOfWeek.Tuesday + 7) % 7;
        StoryDay = today.AddDays(back == 0 ? -7 : -back);
    }

    // The record time of the last entry this seeder writes (8:45 PM
    // on the scenario's Tuesday). Every seeded entry is at or before
    // it. The pages use it, together with the absence of any model
    // reading, to label the starting record as seeded rather than
    // read by a model; nothing about the seed itself depends on it.
    public static DateTime LastSeededEntryUtc => DemoDay(20, 45);

    public static void Seed(AppDbContext db)
    {
        if (db.Households.Any())
        {
            return;
        }

        var household = new Household { Name = "The Ellis Household", TimeZoneId = "America/Chicago", HealthMentionLevelKey = "now" };
        db.Households.Add(household);
        db.SaveChanges();

        // The household: Daniel and Lisa are partners and Timmy and Mia's
        // parents; Margaret is Daniel's mother, living in the house's
        // second unit and very much part of the day; June lives next door,
        // has a key, and does the Tuesday school pickup. One household,
        // one record. Insertion order fixes the demo ids the extraction
        // context carries: Margaret 1, Daniel 2, Lisa 3, Timmy 4, Mia 5,
        // June 6.
        var margaret = new Person { HouseholdId = household.Id, DisplayName = "Margaret Ellis", Role = PersonRole.Family };
        var daniel = new Person { HouseholdId = household.Id, DisplayName = "Daniel", Role = PersonRole.Family };
        var lisa = new Person { HouseholdId = household.Id, DisplayName = "Lisa", Role = PersonRole.Family };
        var timmy = new Person { HouseholdId = household.Id, DisplayName = "Timmy", Role = PersonRole.Family, MinorFlag = true };
        var mia = new Person { HouseholdId = household.Id, DisplayName = "Mia", Role = PersonRole.Family, MinorFlag = true };
        var june = new Person { HouseholdId = household.Id, DisplayName = "June", Role = PersonRole.Helper };
        db.Persons.AddRange(margaret, daniel, lisa, timmy, mia, june);
        db.SaveChanges();

        // Routing defaults: Daniel is the default route person, Lisa the
        // backup. Set on the household once the persons exist.
        household.DefaultRoutePersonId = daniel.Id;
        household.BackupRoutePersonId = lisa.Id;
        db.SaveChanges();

        // Audience defaults: Lisa set her Private default by telling, with
        // read-back, and the request row below is that telling and her
        // disposal. Mia's Ask was set at enrollment by Daniel, the adult
        // who enrolled her (clause 8), and is recorded as an enrollment
        // act. A change from here on goes through the same telling and
        // read-back path.
        var seedNow = DemoDay(6, 0);
        db.PersonAudienceDefaults.AddRange(
            new PersonAudienceDefault { HouseholdId = household.Id, HolderPersonId = lisa.Id, Value = AudienceDefaultValue.Private, SetAtUtc = seedNow },
            new PersonAudienceDefault { HouseholdId = household.Id, HolderPersonId = mia.Id, Value = AudienceDefaultValue.Ask, SetAtUtc = seedNow }
        );
        db.AudienceDefaultRequests.AddRange(
            new AudienceDefaultRequest
            {
                HouseholdId = household.Id, HolderPersonId = lisa.Id, RequestedByPersonId = lisa.Id,
                Value = AudienceDefaultValue.Private, State = AudienceDefaultRequestState.Applied,
                TellingText = "Set the audience default to Private.",
                ReadBackText = "From now on, messages where you don't name anyone will be seen by just you and anyone you name.",
                Channel = "tap", CreatedAtUtc = seedNow, DisposedByPersonId = lisa.Id, DisposedAtUtc = seedNow
            },
            new AudienceDefaultRequest
            {
                HouseholdId = household.Id, HolderPersonId = mia.Id, RequestedByPersonId = daniel.Id,
                Value = AudienceDefaultValue.Ask, State = AudienceDefaultRequestState.Applied,
                TellingText = "(set at enrollment)",
                Channel = "enrollment", CreatedAtUtc = seedNow, DisposedByPersonId = daniel.Id, DisposedAtUtc = seedNow
            }
        );
        db.SaveChanges();

        // The context slice's household rows: alert levels in the
        // household's own words, the times a person set, and the
        // household's own words for its people and things.
        db.AlertLevels.AddRange(
            new AlertLevel { HouseholdId = household.Id, LevelKey = "now", Label = "Right away", RouteToPersonId = daniel.Id, BackupPersonId = lisa.Id },
            new AlertLevel { HouseholdId = household.Id, LevelKey = "brief", Label = "Put it in the brief" },
            new AlertLevel { HouseholdId = household.Id, LevelKey = "here", Label = "Just keep it here" }
        );
        db.HouseholdSchedules.AddRange(
            new HouseholdSchedule { HouseholdId = household.Id, Label = "School pickup", SetByPersonId = lisa.Id, TimeLocal = "15:00" },
            new HouseholdSchedule { HouseholdId = household.Id, Label = "Recycling out", SetByPersonId = daniel.Id, TimeLocal = "20:00" }
        );
        db.GlossaryTerms.AddRange(
            new GlossaryTerm { HouseholdId = household.Id, Term = "Mom", Meaning = "Lisa, Timmy and Mia's mother" },
            new GlossaryTerm { HouseholdId = household.Id, Term = "Dad", Meaning = "Daniel, Timmy and Mia's father" },
            new GlossaryTerm { HouseholdId = household.Id, Term = "Grandma", Meaning = "Margaret, Daniel's mother, in the back unit" },
            new GlossaryTerm { HouseholdId = household.Id, Term = "the truck", Meaning = "the recycling truck, Wednesdays at seven in the morning" }
        );
        db.SaveChanges();

        // The household staples the house keeps count of, with the rates
        // and lead days from supplies.json.
        var coffee = new SupplyItem { HouseholdId = household.Id, Name = "coffee", Unit = "bag", QuantityOnHand = 1m, SeededDailyUsageRate = 0.25m, ReorderLeadDays = 2, AssignedPersonId = daniel.Id, Aliases = """["coffee","the coffee","coffee beans"]""" };
        var tablets = new SupplyItem { HouseholdId = household.Id, Name = "dishwasher tablets", Unit = "tablet", QuantityOnHand = 9m, SeededDailyUsageRate = 1m, ReorderLeadDays = 3, AssignedPersonId = lisa.Id, Aliases = """["dishwasher tablets","dishwasher pods","the tablets"]""" };
        var towels = new SupplyItem { HouseholdId = household.Id, Name = "paper towels", Unit = "roll", QuantityOnHand = 3m, SeededDailyUsageRate = 0.3m, ReorderLeadDays = 2, AssignedPersonId = daniel.Id, Aliases = """["paper towels","kitchen roll"]""" };
        var soap = new SupplyItem { HouseholdId = household.Id, Name = "dish soap", Unit = "bottle", QuantityOnHand = 1m, SeededDailyUsageRate = 0.1m, ReorderLeadDays = 3, AssignedPersonId = lisa.Id, Aliases = """["dish soap","washing up liquid"]""" };
        db.SupplyItems.AddRange(coffee, tablets, towels, soap);
        db.SaveChanges();

        // Household notes that answer by task, in the family's own words,
        // from task-notes.json. The house hands these back exactly as
        // written; it does not write or change them.
        (string Task, string Note)[] taskNotes =
        {
            ("school pickup", "Mia waits at the side gate, not the front."),
            ("school pickup", "Tuesdays Timmy has soccer. His boots are in the hall closet."),
            ("school pickup", "Snacks are in the blue tin. One each, then homework."),
            ("the bins", "Recycling is the blue bin, out Tuesday night. The lid has to close or they skip it."),
            ("the bins", "Bring them back in on Wednesday after work."),
            ("the dishwasher", "Pots go by hand. Tablets are under the sink."),
        };
        var sortByTask = new Dictionary<string, int>();
        foreach (var (task, note) in taskNotes)
        {
            sortByTask[task] = sortByTask.GetValueOrDefault(task) + 1;
            db.TaskNotes.Add(new TaskNote
            {
                HouseholdId = household.Id,
                TaskKey = task,
                NoteText = note,
                SortOrder = sortByTask[task]
            });
        }

        // Journal prompts the household set, from journal-prompts.json.
        string[] prompts =
        {
            "What was the best part of your day?",
            "What should the house know about tomorrow?",
            "Who helped you out today?",
            "What are you looking forward to this weekend?",
        };
        foreach (var text in prompts)
        {
            db.JournalPrompts.Add(new JournalPrompt { HouseholdId = household.Id, Text = text, Enabled = true });
        }
        db.SaveChanges();

        // One ordinary Tuesday on the shared record, from scenario.md: the
        // things households most often forget or argue about, each said
        // once, in the words of the person who said it. Statement-only
        // events are written already Confirmed, their author the confirmer
        // of record.
        Event Said(Person? author, Person? subject, EventType type, int hour, int minute, string body, string? details = null) => new()
        {
            HouseholdId = household.Id, SubjectPersonId = subject?.Id, AuthorPersonId = author?.Id,
            Type = type, CreatedAtUtc = DemoDay(hour, minute),
            ConfirmationState = ConfirmationState.Confirmed,
            Body = body, Details = details
        };
        // 5:40 AM. Daniel leaves his one line at the door. The last part
        // is a promise, and the record treats it as one.
        var leaving = Said(daniel, daniel, EventType.Whereabouts, 5, 40, StoryAnchorWords,
            """{"PlaceText":"depot","Motion":"HeadingTo","NextText":"hardware store after, home by three"}""");
        // 6:45 AM. Which bin, before the truck.
        var bins = Said(lisa, null, EventType.HouseholdNote, 6, 45, "Recycling goes out tonight, the truck comes at seven tomorrow.");
        // 7:05 AM. The last of a staple, said out loud for once.
        var lastCoffee = Said(lisa, null, EventType.Supply, 7, 5, "Used the last of the coffee.",
            """{"Direction":"Used","QuantityText":"the last of the coffee"}""");
        lastCoffee.RelatedSupplyItemId = coffee.Id;
        // 7:20 AM. The schedule moves.
        var soccerMoved = Said(lisa, timmy, EventType.HouseholdNote, 7, 20, "Timmy's soccer practice moved to 5:30 tonight.");
        // 9:05 AM. A shared lift, arranged in Margaret's own words.
        var bridge = Said(margaret, margaret, EventType.HouseholdNote, 9, 5, "Daniel is driving me to bridge club on Tuesday, and June is the backup.");
        // 12:40 PM. A bill, before it is late.
        var bill = Said(lisa, null, EventType.HouseholdNote, 12, 40, "Electric bill is due Friday.");
        // 1:15 PM. The school pickup, done.
        var pickup = Said(june, mia, EventType.HouseholdNote, 13, 15, "Got Mia from school, we're at the house.");
        // 2:55 PM. Daniel is home, five minutes ahead of the morning's
        // promise, in his words both times.
        var home = Said(daniel, daniel, EventType.Whereabouts, 14, 55, "Home, like I said this morning.",
            """{"PlaceText":"home","Motion":"Arrived"}""");
        // 3:30 PM. Margaret passes word to Lisa at work with one tap.
        var homework = Said(margaret, lisa, EventType.HouseholdNote, 15, 30, "Mia's homework is done and she's had a snack.",
            """{"InputMode":"Tap","RelayTo":"Lisa"}""");
        homework.ConfirmationState = ConfirmationState.Done;
        // 4:50 PM and 5:05 PM. The evening, double-booked: both parents
        // say they will take Timmy to soccer, neither checks. The house's
        // own rules and Muse Wonder both notice.
        var danielSoccer = Said(daniel, daniel, EventType.Commitment, 16, 50, "I'll take Timmy to soccer at 5:30.",
            """{"CommitmentText":"take Timmy to soccer at 5:30","DueText":"5:30"}""");
        var lisaSoccer = Said(lisa, lisa, EventType.Commitment, 17, 5, "I'll take Timmy to soccer at 5:30.",
            """{"CommitmentText":"take Timmy to soccer at 5:30","DueText":"5:30"}""");
        // 6:30 PM. A note to the house, from no one in particular: the
        // toilet paper is low. It carries no subject and no author and is
        // not pinned to a tracked item; it sits Proposed until anyone in
        // the household closes it.
        var paper = Said(null, null, EventType.Supply, 18, 30, "Toilet paper is low.",
            """{"Direction":"Low","UnresolvedItem":"toilet paper"}""");
        paper.ConfirmationState = ConfirmationState.Proposed;
        paper.RecordPending = true;
        // 8:45 PM. The day closes: the bins went out after all.
        var goodnight = Said(daniel, null, EventType.HouseholdNote, 20, 45, "Bins are out, dishwasher's running. Goodnight.");

        Event[] events = { leaving, bins, lastCoffee, soccerMoved, bridge, bill, pickup, home, homework, danielSoccer, lisaSoccer, paper, goodnight };
        // Everyone in the household may see everything on this ordinary
        // day: each entry's audience is written down with it.
        foreach (var ev in events)
        {
            ev.AudienceRule ??= "{\"Class\":\"" + ev.SensitivityClass + "\"}";
        }
        db.Events.AddRange(events);
        db.SaveChanges();

        // Margaret's word to Lisa, passed on and delivered: the action
        // and the delivery are on the record with it.
        var relayAction = new EventAction { EventId = homework.Id, ActionType = EventActionType.RelayMessage, TargetPersonId = lisa.Id, Payload = """{"MessageText":"Mia's homework is done and she's had a snack."}""", CreatedAtUtc = DemoDay(15, 30) };
        db.EventActions.Add(relayAction);
        db.SaveChanges();
        db.AlertDeliveries.Add(new AlertDelivery
        {
            EventId = homework.Id, EventActionId = relayAction.Id, LevelKey = "brief",
            ToPersonId = lisa.Id, BodyText = homework.Body,
            DeliveredAtUtc = DemoDay(15, 30), IsBackupHop = false
        });

        // Lisa used the last of the coffee at 7:05; the count afterward is
        // zero, and the adjustment is the paper trail for it, the same way
        // the live pipeline moves a count when a supply message lands.
        db.SupplyAdjustments.Add(new SupplyAdjustment
        {
            SupplyItemId = coffee.Id, EventId = lastCoffee.Id, ActorPersonId = lisa.Id,
            Direction = "Used", QuantityOnHandAfter = 0m, AtUtc = DemoDay(7, 5)
        });
        coffee.QuantityOnHand = 0m;

        // Audit rows: statement-only writes name their author as the
        // confirmer of record; the relayed note carries its full write,
        // confirm, done chain.
        EventAudit Write(Event ev, string channel) => new()
        {
            ObjectKind = "Event", EventId = ev.Id, FromState = null,
            ToState = ev.ConfirmationState.ToString(), ActorPersonId = ev.AuthorPersonId,
            Channel = channel, AtUtc = ev.CreatedAtUtc
        };
        EventAudit Step(Event ev, ConfirmationState from, ConfirmationState to, int? actor, string channel, DateTime at) => new()
        {
            ObjectKind = "Event", EventId = ev.Id, FromState = from,
            ToState = to.ToString(), ActorPersonId = actor, Channel = channel, AtUtc = at
        };
        EventAudit Proposed(Event ev, int? actor, string channel, DateTime at) => new()
        {
            ObjectKind = "Event", EventId = ev.Id, FromState = null,
            ToState = "Proposed", ActorPersonId = actor, Channel = channel, AtUtc = at
        };
        db.EventAudits.AddRange(
            Write(leaving, "voice"),
            Write(bins, "voice"),
            Write(lastCoffee, "voice"),
            Write(soccerMoved, "tap"),
            Write(bridge, "voice"),
            Write(bill, "voice"),
            Write(pickup, "voice"),
            Write(home, "voice"),
            Proposed(homework, margaret.Id, "tap", DemoDay(15, 30)),
            Step(homework, ConfirmationState.Proposed, ConfirmationState.Confirmed, margaret.Id, "tap", DemoDay(15, 30)),
            Step(homework, ConfirmationState.Confirmed, ConfirmationState.Done, null, "executor", DemoDay(15, 30)),
            Write(danielSoccer, "voice"),
            Write(lisaSoccer, "voice"),
            Write(goodnight, "voice")
        );

        // Dinner provenance, stated in the days before the demo day:
        // the household's usual meals and its fridge contents exist
        // only as tellings, each with its author and its day, so a
        // dinner proposal can carry the telling behind every item it
        // names. One item per telling, so each item traces to exactly
        // one author and one day.
        Event ProvenanceTelling(Person author, string body, string kind, string itemName, DateTime at) => new()
        {
            HouseholdId = household.Id, SubjectPersonId = null, AuthorPersonId = author.Id,
            Type = EventType.HouseholdNote, CreatedAtUtc = at,
            ConfirmationState = ConfirmationState.Confirmed,
            Body = body,
            SensitivityClass = SensitivityClass.Household,
            AudienceRule = "{\"Class\":\"Household\"}",
            Details = "{\"MealProvenance\":\"" + kind + "\",\"ItemName\":\"" + itemName + "\"}"
        };
        var provenanceEvents = new[]
        {
            ProvenanceTelling(margaret, "Pasta bake is a usual dinner in this house.", "UsualMeal", "Pasta bake", BeforeStoryDay(4, 18, 20)),
            ProvenanceTelling(daniel, "Soup and sandwiches is one of our usual dinners.", "UsualMeal", "Soup and sandwiches", BeforeStoryDay(3, 19, 12)),
            ProvenanceTelling(lisa, "Tacos are one of our usual dinners.", "UsualMeal", "Tacos", BeforeStoryDay(2, 18, 5)),
            ProvenanceTelling(lisa, "There are eggs in the fridge.", "Fridge", "eggs", BeforeStoryDay(1, 9, 20)),
            ProvenanceTelling(daniel, "There are greens in the fridge.", "Fridge", "greens", BeforeStoryDay(1, 17, 40)),
            ProvenanceTelling(lisa, "There is cheese in the fridge.", "Fridge", "cheese", BeforeStoryDay(0, 8, 15)),
        };
        db.Events.AddRange(provenanceEvents);
        db.SaveChanges();
        foreach (var ev in provenanceEvents)
        {
            db.EventAudits.Add(new EventAudit
            {
                ObjectKind = "Event", EventId = ev.Id, FromState = null,
                ToState = "Confirmed", ActorPersonId = ev.AuthorPersonId,
                Channel = "voice", AtUtc = ev.CreatedAtUtc
            });
        }
        db.SaveChanges();

        // The day's claims, first class. Daniel's 5:40 line is the source
        // of "home by three"; his 2:55 arrival closes it. The two soccer
        // claims stay open: the double-booked evening is on the record
        // until one of them closes it in their own words.
        Promise Claim(Person maker, string text, string dueText, TimeOnly due, Event source) => new()
        {
            HouseholdId = household.Id,
            PromisorPersonId = maker.Id,
            PromiseePersonId = null,
            CommitmentText = text,
            DueText = dueText,
            DueAtLocal = StoryDay.ToDateTime(due),
            SourceEventId = source.Id,
            State = PromiseState.Made
        };
        var homeByThree = Claim(daniel, "home by three", "three", new TimeOnly(15, 0), leaving);
        homeByThree.State = PromiseState.Kept;
        homeByThree.ClosedByEventId = home.Id;
        homeByThree.ClosedByPersonId = daniel.Id;
        homeByThree.ClosedAtUtc = DemoDay(14, 55);
        var danielClaim = Claim(daniel, "take Timmy to soccer at 5:30", "5:30", new TimeOnly(17, 30), danielSoccer);
        var lisaClaim = Claim(lisa, "take Timmy to soccer at 5:30", "5:30", new TimeOnly(17, 30), lisaSoccer);
        db.Promises.AddRange(homeByThree, danielClaim, lisaClaim);
        db.SaveChanges();
        db.EventAudits.AddRange(
            new EventAudit { ObjectKind = "Promise", EventId = leaving.Id, FromState = null, ToState = "Made", ActorPersonId = daniel.Id, Channel = "voice", AtUtc = DemoDay(5, 40) },
            new EventAudit { ObjectKind = "Promise", EventId = home.Id, FromState = null, ToState = "Kept", ActorPersonId = daniel.Id, Channel = "voice", AtUtc = DemoDay(14, 55) },
            new EventAudit { ObjectKind = "Promise", EventId = danielSoccer.Id, FromState = null, ToState = "Made", ActorPersonId = daniel.Id, Channel = "voice", AtUtc = DemoDay(16, 50) },
            new EventAudit { ObjectKind = "Promise", EventId = lisaSoccer.Id, FromState = null, ToState = "Made", ActorPersonId = lisa.Id, Channel = "voice", AtUtc = DemoDay(17, 5) }
        );
        db.SaveChanges();
    }
}
