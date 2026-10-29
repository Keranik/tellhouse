namespace HouseholdCoordinator.Data;

// Seed content for the demo household, mirroring the files in this
// repository's seed folder (personas.md, scenario.md, supplies.json,
// task-notes.json, journal-prompts.json). All people are fictional. The
// day seeded is the scenario's Tuesday, Oct 27, 2026; event times carry
// the scenario's clock times and are stored as UTC (a deliberate
// simplification: no timezone conversion is applied to seed times).
//
// Seeding runs once, on an empty database. To reseed, delete the database
// file and start the app again.
public static class DbSeeder
{
    private static DateTime DemoDay(int hour, int minute) =>
        new(2026, 10, 27, hour, minute, 0, DateTimeKind.Utc);

    public static void Seed(AppDbContext db)
    {
        if (db.Households.Any())
        {
            return;
        }

        var household = new Household { Name = "The Ellis Household", TimeZoneId = "America/Chicago", HealthMentionLevelKey = "now" };
        db.Households.Add(household);
        db.SaveChanges();

        // The household circle: Daniel and Lisa are partners and Timmy
        // and Mia's parents; Margaret is Daniel's mother, in the circle,
        // living in the house's second unit; June is the Tuesday helper.
        // One household, one property, one record. Insertion order fixes
        // the demo ids the extraction context carries: Margaret 1,
        // Daniel 2, Lisa 3, Timmy 4, Mia 5, June 6.
        var margaret = new Person { HouseholdId = household.Id, DisplayName = "Margaret Ellis", Role = PersonRole.Family };
        var daniel = new Person { HouseholdId = household.Id, DisplayName = "Daniel", Role = PersonRole.Family };
        var lisa = new Person { HouseholdId = household.Id, DisplayName = "Lisa", Role = PersonRole.Family };
        var timmy = new Person { HouseholdId = household.Id, DisplayName = "Timmy", Role = PersonRole.Family, MinorFlag = true };
        var mia = new Person { HouseholdId = household.Id, DisplayName = "Mia", Role = PersonRole.Family, MinorFlag = true };
        var june = new Person { HouseholdId = household.Id, DisplayName = "June", Role = PersonRole.Helper };
        db.Persons.AddRange(margaret, daniel, lisa, timmy, mia, june);
        db.SaveChanges();

        // Routing defaults: Daniel is the
        // default route person, Lisa the backup. Set on the household once
        // the persons exist.
        household.DefaultRoutePersonId = daniel.Id;
        household.BackupRoutePersonId = lisa.Id;
        db.SaveChanges();

        // The context slice's household rows: alert levels in the
        // person's own words, the schedules a person set, and the
        // household glossary.
        db.AlertLevels.AddRange(
            new AlertLevel { HouseholdId = household.Id, LevelKey = "now", Label = "Tell Daniel now", RouteToPersonId = daniel.Id, BackupPersonId = lisa.Id },
            new AlertLevel { HouseholdId = household.Id, LevelKey = "brief", Label = "Put it in the brief" },
            new AlertLevel { HouseholdId = household.Id, LevelKey = "here", Label = "Just keep it here" }
        );
        db.HouseholdSchedules.AddRange(
            new HouseholdSchedule { HouseholdId = household.Id, Label = "Morning ones", SetByPersonId = daniel.Id, TimeLocal = "08:00" },
            new HouseholdSchedule { HouseholdId = household.Id, Label = "Evening ones", SetByPersonId = daniel.Id, TimeLocal = "19:00" }
        );
        db.GlossaryTerms.Add(new GlossaryTerm { HouseholdId = household.Id, Term = "my program", Meaning = "the afternoon show Margaret watches at four" });
        db.SaveChanges();

        // Consumables, with the rates and lead days from supplies.json.
        // The heart pills entry records only a schedule a person set and a
        // refill date; it carries nothing about taking them.
        var pads = new SupplyItem { HouseholdId = household.Id, Name = "pads", Unit = "pad", QuantityOnHand = 18m, SeededDailyUsageRate = 4m, ReorderLeadDays = 3, AssignedPersonId = lisa.Id, Aliases = """["pads","the big pads"]""" };
        var wipes = new SupplyItem { HouseholdId = household.Id, Name = "wipes", Unit = "wipe", QuantityOnHand = 12m, SeededDailyUsageRate = 5m, ReorderLeadDays = 2, AssignedPersonId = daniel.Id, Aliases = """["wipes","the wipes"]""" };
        var gauze = new SupplyItem { HouseholdId = household.Id, Name = "gauze", Unit = "dressing", QuantityOnHand = 1m, SeededDailyUsageRate = 1m, ReorderLeadDays = 2, AssignedPersonId = daniel.Id, Aliases = """["gauze"]""" };
        var heartPills = new SupplyItem { HouseholdId = household.Id, Name = "heart pills (morning)", Unit = "pill", QuantityOnHand = 7m, SeededDailyUsageRate = 1m, ReorderLeadDays = 7, AssignedPersonId = lisa.Id, Aliases = """["heart pills","morning ones"]""" };
        db.SupplyItems.AddRange(pads, wipes, gauze, heartPills);
        db.SaveChanges();

        // Household notes that answer by task, in the family's own words,
        // from task-notes.json. The coordinator hands these back exactly as
        // written; it does not write or change them.
        (string Task, string Note)[] taskNotes =
        {
            ("getting dressed", "The blue cardigan, from the chair. The green one has the broken button."),
            ("getting dressed", "Shoes go on sitting on the bed, not standing."),
            ("getting dressed", "Tea after, one sugar. She will say two. It is one."),
            ("making lunch", "Tea first, always. The kettle goes on before anything else, and her cup is the one with the flowers on it."),
            ("making lunch", "She likes her sandwich cut corner to corner, crusts on. If there is soup, it goes in a mug, not a bowl, so she can hold it steady."),
            ("making lunch", "Sit with her while she eats if you have the minutes. Lunch is when she tells the good stories, and she eats better with company."),
            ("getting ready for bed", "Her program ends at eight, and that is her signal to wind down. Anything she says then goes on the timeline in her words, the same as the rest of the day."),
            ("getting ready for bed", "The porch light stays on all night. Her water glass goes on the left side of the bed table, where she can reach it without sitting up."),
            ("getting ready for bed", "Say goodnight out loud, even if you are leaving by the side door. She answers back, and the house settles once she has heard it."),
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

        // Journaling prompts Margaret set herself, from journal-prompts.json.
        string[] prompts =
        {
            "What is one thing on the porch you can see from your chair?",
            "Who were the birds in the garden arguing with this morning?",
            "What song do you remember from when Daniel was little?",
            "What did the kitchen smell like when you first came down today?",
            "Tell about one photograph on the shelf, the day it was taken.",
            "What is something you heard on the radio this week that stayed with you?",
            "If June could bring you anything from the shops, what would make you laugh?",
            "What are you looking forward to on Sunday?",
        };
        foreach (var text in prompts)
        {
            db.JournalPrompts.Add(new JournalPrompt { HouseholdId = household.Id, Text = text, Enabled = true });
        }
        db.SaveChanges();

        // One Tuesday on the shared record, from scenario.md. Statement-only
        // events are written already Confirmed, their author the confirmer
        // of record. The 7:15 AM lookup leaves no event: lookups are read
        // only and create nothing.
        Event[] events =
        {
            // 5:40 AM. Daniel leaves his one line at the door.
            new()
            {
                HouseholdId = household.Id, SubjectPersonId = daniel.Id, AuthorPersonId = daniel.Id,
                Type = EventType.Whereabouts, CreatedAtUtc = DemoDay(5, 40),
                ConfirmationState = ConfirmationState.Confirmed,
                Body = "Depot by nine, hardware store after, home by three.",
                Details = """{"PlaceText":"depot","Motion":"HeadingTo","NextText":"hardware store after, home by three"}"""
            },
            // 7:20 AM. Margaret asks for her program; she confirms with a
            // spoken yes and the program starts, so the day ends with this
            // Done. Her lookup in the same breath creates no event.
            new()
            {
                HouseholdId = household.Id, SubjectPersonId = margaret.Id, AuthorPersonId = margaret.Id,
                Type = EventType.Request, CreatedAtUtc = DemoDay(7, 20),
                ConfirmationState = ConfirmationState.Done,
                Body = "Put my program on.",
                Details = """{"RequestKind":"Media"}"""
            },
            // 7:50 AM. A health mention, recorded in her words and handed to
            // Daniel at the level she chose in advance. Done: the hand off
            // was delivered. The record holds her words and nothing else.
            new()
            {
                HouseholdId = household.Id, SubjectPersonId = margaret.Id, AuthorPersonId = margaret.Id,
                Type = EventType.HealthMention, CreatedAtUtc = DemoDay(7, 50),
                ConfirmationState = ConfirmationState.Done,
                Body = "My ankle has swollen up again.",
                Details = """{"Form":"Statement"}"""
            },
            // 9:05 AM. Margaret states her Tuesday bridge club
            // arrangement in her own words: Daniel drives, June is
            // the backup. A statement of her plan, written already
            // Confirmed with Margaret as its confirmer of record,
            // the same shape as every statement-only entry on this
            // day. This is the record line Beat 6 shows among the
            // day's entries; it is record content, not a telling
            // produced live.
            new()
            {
                HouseholdId = household.Id, SubjectPersonId = margaret.Id, AuthorPersonId = margaret.Id,
                Type = EventType.HouseholdNote, CreatedAtUtc = DemoDay(9, 5),
                ConfirmationState = ConfirmationState.Confirmed,
                Body = "Daniel is driving me to bridge club on Tuesday, and June is the backup."
            },
            // 12:40 PM. Lisa uses the last of the gauze and says so the
            // moment she notices.
            new()
            {
                HouseholdId = household.Id, SubjectPersonId = margaret.Id, AuthorPersonId = lisa.Id,
                Type = EventType.Supply, CreatedAtUtc = DemoDay(12, 40),
                ConfirmationState = ConfirmationState.Confirmed,
                Body = "Last of the gauze going on now.",
                RelatedSupplyItemId = gauze.Id,
                Details = """{"Direction":"Used","QuantityText":"last of the gauze"}"""
            },
            // 1:15 PM. June arrives and opens the notes for getting dressed.
            new()
            {
                HouseholdId = household.Id, SubjectPersonId = margaret.Id, AuthorPersonId = june.Id,
                Type = EventType.HouseholdNote, CreatedAtUtc = DemoDay(13, 15),
                ConfirmationState = ConfirmationState.Confirmed,
                Body = "June arrived. Getting dressed notes opened."
            },
            // 2:55 PM. Daniel is home, five minutes ahead of the
            // morning's promise. A commitment on the timeline, kept on
            // the same timeline, in his words both times.
            new()
            {
                HouseholdId = household.Id, SubjectPersonId = daniel.Id, AuthorPersonId = daniel.Id,
                Type = EventType.Whereabouts, CreatedAtUtc = DemoDay(14, 55),
                ConfirmationState = ConfirmationState.Confirmed,
                Body = "Home, like I said this morning.",
                Details = """{"PlaceText":"home","Motion":"Arrived"}"""
            },
            // 3:30 PM. Margaret's voice is tired; she taps instead of
            // talking. She taps "Tell Daniel," confirms "Send" on the
            // card, and the note goes to Daniel.
            new()
            {
                HouseholdId = household.Id, SubjectPersonId = daniel.Id, AuthorPersonId = margaret.Id,
                Type = EventType.HouseholdNote, CreatedAtUtc = DemoDay(15, 30),
                ConfirmationState = ConfirmationState.Done,
                Body = "Thinking of you. All quiet here.",
                Details = """{"InputMode":"Tap","RelayTo":"Daniel"}"""
            },
            // 6:30 PM. A note to the house, from no one in particular:
            // the toilet paper is low. Events are told to the house, so
            // this one carries no subject and no author, and it is not
            // pinned to a tracked supply item. It sits Proposed until
            // anyone in the circle closes it. Attribution is recorded
            // when it is known and matters; here it is neither.
            new()
            {
                HouseholdId = household.Id, SubjectPersonId = null, AuthorPersonId = null,
                Type = EventType.Supply, CreatedAtUtc = DemoDay(18, 30),
                ConfirmationState = ConfirmationState.Proposed,
                Body = "Toilet paper is low.",
                Details = """{"Direction":"Low","UnresolvedItem":"toilet paper"}"""
            },
            // 8:45 PM. The day closes in her words.
            new()
            {
                HouseholdId = household.Id, SubjectPersonId = margaret.Id, AuthorPersonId = margaret.Id,
                Type = EventType.HouseholdNote, CreatedAtUtc = DemoDay(20, 45),
                ConfirmationState = ConfirmationState.Confirmed,
                Body = "Well. That is the whole day, written down. Goodnight.",
                Details = """{"MedMention":"None"}"""
            },
        };
        // Discretion, computed at write time:
        // the ankle mention is Restricted, routed to Daniel with Lisa
        // behind him; everything else on this ordinary day is Household.
        // The person-free toilet paper notice is record-pending: its fact
        // is on the record, unconfirmed, until a person closes it.
        events[2].SensitivityClass = SensitivityClass.Restricted;
        events[2].AudienceRule = "{\"Class\":\"Restricted\",\"RoutePersonId\":" + daniel.Id + ",\"BackupPersonId\":" + lisa.Id + "}";
        events[8].RecordPending = true;
        foreach (var ev in events)
        {
            ev.AudienceRule ??= "{\"Class\":\"" + ev.SensitivityClass + "\"}";
        }

        db.Events.AddRange(events);
        db.SaveChanges();

        // The day's actions, deliveries, and audit trail, so the seeded
        // record shows the whole machinery, not just its end states.
        var mediaAction = new EventAction { EventId = events[1].Id, ActionType = EventActionType.MediaPlay, Payload = """{"WhatText":"my program","AtLocal":null}""", CreatedAtUtc = DemoDay(7, 20) };
        var notifyAction = new EventAction { EventId = events[2].Id, ActionType = EventActionType.NotifyPerson, Payload = """{"AboutEventIndex":0}""", CreatedAtUtc = DemoDay(7, 50) };
        var relayAction = new EventAction { EventId = events[7].Id, ActionType = EventActionType.RelayMessage, TargetPersonId = daniel.Id, Payload = """{"MessageText":"Thinking of you. All quiet here."}""", CreatedAtUtc = DemoDay(15, 30) };
        db.EventActions.AddRange(mediaAction, notifyAction, relayAction);
        db.SaveChanges();

        // The ankle hand off, delivered to Daniel at the level Margaret
        // chose in advance: her words verbatim, and nothing else.
        db.AlertDeliveries.Add(new AlertDelivery
        {
            EventId = events[2].Id, EventActionId = notifyAction.Id, LevelKey = "now",
            ToPersonId = daniel.Id, BodyText = events[2].Body,
            DeliveredAtUtc = DemoDay(7, 50), IsBackupHop = false
        });

        // Lisa used the last of the gauze at 12:40; the count afterward
        // is zero, and the adjustment is the paper trail for it. The
        // item's own count moves to zero with it, the same way the
        // live pipeline moves a count when a supply telling lands
        // (ExtractionService.ApplySupplyAdjustmentAsync): a seed that
        // left the item at one would show one on hand on the same
        // timeline that records the last of it being used.
        db.SupplyAdjustments.Add(new SupplyAdjustment
        {
            SupplyItemId = gauze.Id, EventId = events[4].Id, ActorPersonId = lisa.Id,
            Direction = "Used", QuantityOnHandAfter = 0m, AtUtc = DemoDay(12, 40)
        });
        gauze.QuantityOnHand = 0m;

        // Audit rows: statement-only writes name their author as the
        // confirmer of record with the input's channel; the three
        // action events carry their full write, confirm, done chain.
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
            Write(events[0], "voice"),
            Proposed(events[1], margaret.Id, "voice", DemoDay(7, 20)),
            Step(events[1], ConfirmationState.Proposed, ConfirmationState.Confirmed, margaret.Id, "voice", DemoDay(7, 20)),
            Step(events[1], ConfirmationState.Confirmed, ConfirmationState.Done, null, "executor", DemoDay(7, 20)),
            Proposed(events[2], margaret.Id, "voice", DemoDay(7, 50)),
            Step(events[2], ConfirmationState.Proposed, ConfirmationState.Confirmed, margaret.Id, "voice", DemoDay(7, 50)),
            Step(events[2], ConfirmationState.Confirmed, ConfirmationState.Done, null, "executor", DemoDay(7, 50)),
            Write(events[3], "voice"),
            Write(events[4], "voice"),
            Write(events[5], "voice"),
            Write(events[6], "voice"),
            Proposed(events[7], margaret.Id, "tap", DemoDay(15, 30)),
            Step(events[7], ConfirmationState.Proposed, ConfirmationState.Confirmed, margaret.Id, "tap", DemoDay(15, 30)),
            Step(events[7], ConfirmationState.Confirmed, ConfirmationState.Done, null, "executor", DemoDay(15, 30)),
            Write(events[9], "voice")
        );

        // Dinner provenance, stated in the days before the demo day
        // (Beat 3's named deliverable): the household's usual meals
        // and its fridge contents exist only as tellings, each with
        // its author and its day, so a dinner proposal can carry the
        // telling behind every item it names. One item per telling,
        // so each item traces to exactly one author and one day.
        Event ProvenanceTelling(Person author, string body, string kind, string itemName, DateTime at) => new()
        {
            HouseholdId = household.Id, SubjectPersonId = null, AuthorPersonId = author.Id,
            Type = EventType.HouseholdNote, CreatedAtUtc = at,
            ConfirmationState = ConfirmationState.Confirmed,
            Body = body,
            SensitivityClass = SensitivityClass.Household,
            AudienceRule = "{\"Class\":\"Household\"}",
            Details = "{\"MedMention\":\"None\",\"MealProvenance\":\"" + kind + "\",\"ItemName\":\"" + itemName + "\"}"
        };
        var provenanceEvents = new[]
        {
            ProvenanceTelling(margaret, "Pasta bake is a usual dinner in this house.", "UsualMeal", "Pasta bake", new DateTime(2026, 10, 23, 18, 20, 0, DateTimeKind.Utc)),
            ProvenanceTelling(daniel, "Soup and sandwiches is one of our usual dinners.", "UsualMeal", "Soup and sandwiches", new DateTime(2026, 10, 24, 19, 12, 0, DateTimeKind.Utc)),
            ProvenanceTelling(lisa, "Tacos are one of our usual dinners.", "UsualMeal", "Tacos", new DateTime(2026, 10, 25, 18, 5, 0, DateTimeKind.Utc)),
            ProvenanceTelling(lisa, "There are eggs in the fridge.", "Fridge", "eggs", new DateTime(2026, 10, 26, 9, 20, 0, DateTimeKind.Utc)),
            ProvenanceTelling(daniel, "There are greens in the fridge.", "Fridge", "greens", new DateTime(2026, 10, 26, 17, 40, 0, DateTimeKind.Utc)),
            ProvenanceTelling(lisa, "There is cheese in the fridge.", "Fridge", "cheese", new DateTime(2026, 10, 27, 8, 15, 0, DateTimeKind.Utc)),
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

        // The day's promise, first class:
        // Daniel's 5:40 line is the source; his 2:55 arrival closes it,
        // confirmed by Daniel himself, and the closing link names both.
        var promise = new Promise
        {
            HouseholdId = household.Id,
            PromisorPersonId = daniel.Id,
            PromiseePersonId = null,
            CommitmentText = "home by three",
            DueText = "three",
            DueAtLocal = new DateTime(2026, 10, 27, 15, 0, 0),
            SourceEventId = events[0].Id,
            State = PromiseState.Kept,
            ClosedByEventId = events[6].Id,
            ClosedByPersonId = daniel.Id,
            ClosedAtUtc = DemoDay(14, 55)
        };
        db.Promises.Add(promise);
        db.SaveChanges();
        db.EventAudits.AddRange(
            new EventAudit { ObjectKind = "Promise", EventId = events[0].Id, FromState = null, ToState = "Made", ActorPersonId = daniel.Id, Channel = "voice", AtUtc = DemoDay(5, 40) },
            new EventAudit { ObjectKind = "Promise", EventId = events[6].Id, FromState = null, ToState = "Kept", ActorPersonId = daniel.Id, Channel = "voice", AtUtc = DemoDay(14, 55) }
        );
        db.SaveChanges();
    }
}
