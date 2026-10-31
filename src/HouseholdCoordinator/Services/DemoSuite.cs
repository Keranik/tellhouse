using System.Text.Json;
using HouseholdCoordinator.Data;
using HouseholdCoordinator.Services.Extraction;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HouseholdCoordinator.Services;

// The local demonstration and test suite, run with:
//   dotnet run -- --demo-suite
// It seeds a scratch database with the standard extraction context
// slice (Margaret, Daniel, Lisa, Timmy and Mia the children, June
// the helper; the slice supply items with their aliases; Daniel's
// two schedules), then runs a fixed corpus of 19 worked examples
// through the real pipeline: context builder, mock extraction
// client, validator, confirmation dispatcher, record. Expectations
// are fixed with the corpus: event types, states, person links,
// details, the exact template sentences, and the alert decisions.
// Three further cases exercise the Commitment type and the promise
// it births, a Pickup task, and the five-event cap with its fold,
// and a final block exercises the plan assembly, promise closing,
// audience-filtered brief, and fenced whereabouts lookup. A closing
// block exercises the stored audience where the record is read (the
// front door, timeline, promises, and plans page models, the briefs
// per person) and
// where a confirmed telling is dispatched. Nothing
// here touches the network or the demo day's database.
public static class DemoSuite
{
    private static int _passed;
    private static int _failed;
    private static readonly List<string> _failures = new();

    public static async Task<int> RunAsync()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"tellhouse-demo-{Guid.NewGuid():N}.db");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));
        services.AddScoped<ConfirmationService>();
        services.AddSingleton<IExtractionClient>(new MockExtractionClient());
        services.AddScoped<ExtractionContextBuilder>();
        services.AddScoped<ExtractionService>();
        services.AddScoped<PlanAssemblyService>();
        services.AddScoped<DinnerProvenanceService>();
        services.AddScoped<MemberDeliveryService>();
        services.AddScoped<BriefService>();
        services.AddScoped<LookupService>();

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        await db.Database.EnsureCreatedAsync();
        var householdId = SeedSlice(db);
        Console.WriteLine($"Scratch slice seeded in {dbPath} (household {householdId}).");

        var svc = sp.GetRequiredService<ExtractionService>();
        var confirmations = sp.GetRequiredService<ConfirmationService>();
        var builder = sp.GetRequiredService<ExtractionContextBuilder>();

        ExtractionInput Input(string transcript, int? speakerId, string time, ExtractionContext? ctxOverride = null)
        {
            var ctx = ctxOverride ?? builder.BuildAsync(householdId).GetAwaiter().GetResult();
            return new ExtractionInput
            {
                UtteranceId = $"demo-{Guid.NewGuid():N}"[..16],
                Transcript = transcript,
                SourceType = "voice",
                OccurredAtLocal = DateTimeOffset.Parse($"2026-10-27T{time}:00-05:00"),
                Speaker = speakerId is int id
                    ? ctx.Persons.Where(p => p.PersonId == id).Select(p => new ExtractionPersonRef
                    {
                        PersonId = p.PersonId,
                        DisplayName = p.DisplayName,
                        Role = p.Role
                    }).First()
                    : null,
                Context = ctx
            };
        }

        // Example 4 runs first so Example 14's lookup has its whereabouts
        // in the record, in the order the corpus fixes.
        var ex4 = await svc.ProcessAsync(Input("I'm at Domino's, heading home after the store.", 2, "14:03"));
        Check("Ex4 whereabouts written Confirmed", ex4.WrittenEvents.Count == 1
            && ex4.WrittenEvents[0].Type == EventType.Whereabouts
            && ex4.WrittenEvents[0].ConfirmationState == ConfirmationState.Confirmed);
        Check("Ex4 spoken is T-RECORDED", ex4.HouseReply == "Written down.");
        Check("Ex4 details carry place, motion, next", ex4.WrittenEvents.Count == 1
            && GetStr(ex4.WrittenEvents[0], "PlaceText") == "Domino's"
            && GetStr(ex4.WrittenEvents[0], "Motion") == "HeadingHome"
            && GetStr(ex4.WrittenEvents[0], "NextText") == "the store");

        // Seed Daniel's Tuesday note directly (a stored statement, the
        // way the record would hold it) for Example 3's lookup.
        var danielNote = new Event
        {
            HouseholdId = householdId,
            Type = EventType.HouseholdNote,
            Body = "I'm taking Mom to the center Tuesday; the nurse comes at two.",
            SubjectPersonId = 1,
            AuthorPersonId = 2,
            CreatedAtUtc = new DateTime(2026, 10, 27, 11, 40, 0, DateTimeKind.Utc),
            SensitivityClass = SensitivityClass.Household,
            AudienceRule = """{"Class":"Household"}"""
        };
        await confirmations.WriteStatementAsync(danielNote, "voice");

        var ex3 = await svc.ProcessAsync(Input("What did Daniel say about Tuesday?", 1, "11:45"));
        Check("Ex3 lookup writes nothing", ex3.WrittenEvents.Count == 0);
        Check("Ex3 answer names Daniel and carries the fact", ex3.HouseReply is not null
            && ex3.HouseReply.StartsWith("Daniel said") && ex3.HouseReply.Contains("nurse comes at two"));

        var ex1 = await svc.ProcessAsync(Input("Could you ask Lisa to print two copies of the bus pass?", 1, "06:58"));
        Check("Ex1 one relay Request, Proposed", ex1.WrittenEvents.Count == 1
            && ex1.WrittenEvents[0].Type == EventType.Request
            && ex1.WrittenEvents[0].ConfirmationState == ConfirmationState.Proposed
            && GetStr(ex1.WrittenEvents[0], "RequestKind") == "Relay");
        Check("Ex1 prompt is the exact relay confirm", ex1.HouseReply ==
            "Do you want me to tell Lisa: Could you ask Lisa to print two copies of the bus pass??");
        Check("Ex1 no alert from a relay", ex1.Output.AlertDecision.Fire == false
            && ex1.Output.AlertDecision.Reason == "RelayIsAnActionNotAnAlert");

        var ex2 = await svc.ProcessAsync(Input("Put my program on.", 1, "07:20"));
        Check("Ex2 media Request Proposed with MediaPlay", ex2.WrittenEvents.Count == 1
            && ex2.Output.ProposedAction?.ActionType == "MediaPlay"
            && ex2.HouseReply == "Shall I put my program on?");

        var ex5 = await svc.ProcessAsync(Input("We're low on wipes today; there are only a few left.", 3, "08:05"));
        Check("Ex5 supply resolves to Wipes", ex5.WrittenEvents.Count == 1
            && ex5.WrittenEvents[0].Type == EventType.Supply
            && ex5.WrittenEvents[0].RelatedSupplyItemId == 10
            && ex5.WrittenEvents[0].SubjectPersonId is null);
        Check("Ex5 spoken is the exact forecast line", ex5.HouseReply ==
            "Noted. Wipes marked low. At the usual rate, it runs out around Thursday.");
        Check("Ex5 supply goes to the brief, never fires", ex5.Output.AlertDecision.LevelId == "brief"
            && ex5.Output.AlertDecision.Fire == false);

        var ctx6 = await builder.BuildAsync(householdId);
        ctx6.OpenPrompt = new ExtractionOpenPrompt { Kind = "Journal", PromptId = "jp-03", Text = "What are you thankful for today?" };
        var ex6 = await svc.ProcessAsync(Input("I'm thankful Lisa calls every Sunday.", 1, "08:12", ctx6));
        Check("Ex6 journal prompt answer", ex6.WrittenEvents.Count == 1
            && ex6.WrittenEvents[0].Type == EventType.Journal
            && GetStr(ex6.WrittenEvents[0], "EntryKind") == "PromptAnswer"
            && GetStr(ex6.WrittenEvents[0], "PromptId") == "jp-03"
            && ex6.HouseReply == "Written down.");

        var ex7 = await svc.ProcessAsync(Input("My ankle is swollen and it hurts when I stand.", 1, "07:50"));
        Check("Ex7 exactly one HealthMention, Proposed", ex7.WrittenEvents.Count == 1
            && ex7.WrittenEvents[0].Type == EventType.HealthMention
            && ex7.WrittenEvents[0].ConfirmationState == ConfirmationState.Proposed
            && GetStr(ex7.WrittenEvents[0], "Form") == "Statement");
        Check("Ex7 spoken is the exact T-ROUTE sentence", ex7.HouseReply ==
            "I've written that down. Do you want me to tell Daniel?");
        Check("Ex7 alert fires on confirmation only, now level, Daniel route, Lisa backup",
            ex7.Output.AlertDecision is { Fire: false, FiresOn: "Confirmation", LevelId: "now", RouteToPersonId: 2, BackupPersonId: 3 });
        Check("Ex7 mention is Restricted with the route on its audience rule",
            ex7.WrittenEvents[0].SensitivityClass == SensitivityClass.Restricted
            && (ex7.WrittenEvents[0].AudienceRule ?? "").Contains("\"RoutePersonId\":2"));
        Check("Ex7 no delivery exists before anyone confirms",
            await db.AlertDeliveries.CountAsync() == 0);
        await confirmations.ConfirmAsync(ex7.WrittenEvents[0].Id, 1, "tap");
        var delivery = await db.AlertDeliveries.SingleAsync();
        Check("Ex7 confirming delivers the body verbatim to Daniel, once",
            delivery.ToPersonId == 2 && delivery.BodyText == "My ankle is swollen and it hurts when I stand.");
        var ex7After = await db.Events.FirstAsync(e => e.Id == ex7.WrittenEvents[0].Id);
        Check("Ex7 event completes through the dispatcher", ex7After.ConfirmationState == ConfirmationState.Done);

        var ex8 = await svc.ProcessAsync(Input("Daniel set my evening ones for seven, after supper.", 1, "08:31"));
        Check("Ex8 schedule statement copies the slice ScheduleRef", ex8.WrittenEvents.Count == 1
            && ex8.WrittenEvents[0].Type == EventType.HouseholdNote
            && GetStr(ex8.WrittenEvents[0], "MedMention") == "ScheduleStatement"
            && (ex8.WrittenEvents[0].Details ?? "").Contains("\"ScheduleId\":101")
            && (ex8.WrittenEvents[0].Details ?? "").Contains("\"TimeLocal\":\"19:00\"")
            && (ex8.WrittenEvents[0].Details ?? "").Contains("\"SetByPersonId\":2"));

        var ex9 = await svc.ProcessAsync(Input("My pills are in the blue box on the counter, next to the wipes.", 1, "09:02"));
        Check("Ex9 casual med mention is a plain note", ex9.WrittenEvents.Count == 1
            && ex9.WrittenEvents[0].Type == EventType.HouseholdNote
            && GetStr(ex9.WrittenEvents[0], "MedMention") == "Casual");

        var ex10 = await svc.ProcessAsync(Input("Should I take an extra one tonight?", 1, "18:52"));
        Check("Ex10 health question routed with T-ROUTE-Q verbatim", ex10.WrittenEvents.Count == 1
            && ex10.WrittenEvents[0].Type == EventType.HealthMention
            && GetStr(ex10.WrittenEvents[0], "Form") == "Question"
            && ex10.HouseReply == "I've written that down exactly as you said it. Do you want me to tell Daniel?");

        var ex11 = await svc.ProcessAsync(Input("Is this swelling bad?", 1, "19:05"));
        Check("Ex11 worry question is a HealthMention question, never answered", ex11.WrittenEvents.Count == 1
            && ex11.WrittenEvents[0].Type == EventType.HealthMention
            && GetStr(ex11.WrittenEvents[0], "Form") == "Question"
            && ex11.HouseReply is not null && ex11.HouseReply.Contains("Do you want me to tell Daniel?"));

        var ex12 = await svc.ProcessAsync(Input("I missed my morning ones today.", 1, "09:14"));
        Check("Ex12 missed-dose self-report is a note, no ScheduleRef, no action", ex12.WrittenEvents.Count == 1
            && ex12.WrittenEvents[0].Type == EventType.HouseholdNote
            && GetStr(ex12.WrittenEvents[0], "MedMention") == "SelfReport"
            && !(ex12.WrittenEvents[0].Details ?? "").Contains("ScheduleRef")
            && ex12.Output.ProposedAction is null
            && ex12.HouseReply == "Written down.");

        var ex13 = await svc.ProcessAsync(Input("Tell Daniel the gauze is nearly gone, and I liked the soup at lunch, and my program comes on at four.", 1, "12:40"));
        Check("Ex13 three events in order", ex13.WrittenEvents.Count == 3
            && ex13.WrittenEvents[0].Type == EventType.Supply
            && ex13.WrittenEvents[1].Type == EventType.HouseholdNote
            && ex13.WrittenEvents[2].Type == EventType.Request);
        Check("Ex13 gauze resolves with quantity in her words", ex13.WrittenEvents.Count == 3
            && ex13.WrittenEvents[0].RelatedSupplyItemId == 11
            && GetStr(ex13.WrittenEvents[0], "QuantityText") == "nearly gone"
            && ex13.WrittenEvents[0].SubjectPersonId == 1);
        Check("Ex13 media at four, prompt exact", ex13.Output.ProposedAction?.ActionType == "MediaPlay"
            && ex13.HouseReply == "Shall I put my program on?"
            && (ex13.WrittenEvents[2].Details ?? "") is not null);

        var ex14 = await svc.ProcessAsync(Input("Where's Daniel?", 1, "14:10"));
        Check("Ex14 whereabouts answer carries place and heading from the record", ex14.WrittenEvents.Count == 0
            && ex14.HouseReply is not null
            && ex14.HouseReply.StartsWith("Daniel said") && ex14.HouseReply.Contains("Domino's")
            && ex14.HouseReply.Contains("heading home"));

        var ex15 = await svc.ProcessAsync(Input("The center called, the Tuesday ride moves to three o'clock.", 1, "10:22"));
        Check("Ex15 facility notice, no subject, change in their words", ex15.WrittenEvents.Count == 1
            && ex15.WrittenEvents[0].Type == EventType.FacilityNotice
            && ex15.WrittenEvents[0].SubjectPersonId is null
            && GetStr(ex15.WrittenEvents[0], "SourceName") == "the center"
            && GetStr(ex15.WrittenEvents[0], "ChangeText") == "the Tuesday ride moves to three o'clock");

        var ex16 = await svc.ProcessAsync(Input("Remind me on Tuesday to order the evening ones, like Daniel put on the list.", 1, "16:44"));
        Check("Ex16 reminder request with the exact confirm prompt", ex16.WrittenEvents.Count == 1
            && ex16.Output.ProposedAction?.ActionType == "CreateReminder"
            && ex16.HouseReply == "Shall I remind you on Tuesday to order the evening ones, like Daniel put on the list?");

        var ex17 = await svc.ProcessAsync(Input("Only a few of the evening pills left in the bottle.", 1, "19:41"));
        Check("Ex17 alias resolves to Evening pills, T-SUPPLY spoken (no forecast: no seeded rate)", ex17.WrittenEvents.Count == 1
            && ex17.WrittenEvents[0].RelatedSupplyItemId == 14
            && GetStr(ex17.WrittenEvents[0], "QuantityText") == "only a few left"
            && ex17.HouseReply == "Noted. Evening pills (bottle) marked low. The morning brief will carry it.");

        var ex18 = await svc.ProcessAsync(Input("Tell her the thing about Thursday.", 1, "13:02"));
        Check("Ex18 unclear relay asks, writes nothing", ex18.Output.ClarificationNeeded
            && ex18.WrittenEvents.Count == 0
            && ex18.HouseReply == "I want to get that right. Who should I tell, and what about Thursday?");

        var eventsBefore19 = await db.Events.CountAsync();
        var ex19 = await svc.ProcessAsync(Input("Toilet paper is low.", null, "18:30"));
        Check("Ex19 speakerless supply: Proposed, unresolved, no person invented", ex19.WrittenEvents.Count == 1
            && ex19.WrittenEvents[0].Type == EventType.Supply
            && ex19.WrittenEvents[0].ConfirmationState == ConfirmationState.Proposed
            && ex19.WrittenEvents[0].SubjectPersonId is null && ex19.WrittenEvents[0].AuthorPersonId is null
            && GetStr(ex19.WrittenEvents[0], "UnresolvedItem") == "toilet paper"
            && ex19.WrittenEvents[0].RecordPending);
        Check("Ex19 spoken is T-RECORDED", ex19.HouseReply == "Written down.");
        await confirmations.CloseRecordPendingAsync(ex19.WrittenEvents[0].Id, 3, "tap");
        var ex19After = await db.Events.FirstAsync(e => e.Id == ex19.WrittenEvents[0].Id);
        var closingAudit = await db.EventAudits
            .Where(a => a.EventId == ex19.WrittenEvents[0].Id && a.ToState == "Confirmed")
            .FirstAsync();
        Check("Ex19 a circle member closes it and the audit names her", ex19After.ConfirmationState == ConfirmationState.Confirmed
            && closingAudit.ActorPersonId == 3);
        Check("Ex19 wrote exactly one event", await db.Events.CountAsync() == eventsBefore19 + 1);

        // Commitment, task, and fold cases (beyond the worked examples).
        var commit = await svc.ProcessAsync(Input("I'll be home by three.", 2, "09:00"));
        Check("Commitment written Confirmed by its maker", commit.WrittenEvents.Count == 1
            && commit.WrittenEvents[0].Type == EventType.Commitment
            && commit.WrittenEvents[0].AuthorPersonId == 2
            && GetStr(commit.WrittenEvents[0], "CommitmentText") == "I'll be home by three"
            && GetStr(commit.WrittenEvents[0], "DueText") == "three");
        Check("A claim is born with the Commitment, due 3:00 PM resolved", commit.PromisesBorn.Count == 1
            && commit.PromisesBorn[0].PromisorPersonId == 2
            && commit.PromisesBorn[0].State == PromiseState.Made
            && commit.PromisesBorn[0].DueAtLocal == new DateTime(2026, 10, 27, 15, 0, 0)
            && commit.PromisesBorn[0].SourceEventId == commit.WrittenEvents[0].Id);

        var task = await svc.ProcessAsync(Input("Daniel needs picking up from the depot at five.", 3, "13:20"));
        Check("Pickup is a Task, not a whereabouts or a request", task.WrittenEvents.Count == 1
            && task.WrittenEvents[0].Type == EventType.Task
            && GetStr(task.WrittenEvents[0], "TaskKind") == "Pickup"
            && GetInt(task.WrittenEvents[0], "CollectPersonId") == 2
            && GetStr(task.WrittenEvents[0], "PlaceText") == "the depot"
            && GetStr(task.WrittenEvents[0], "DueText") == "five");

        var fold = await svc.ProcessAsync(Input(
            "The porch light is on, and the soup is on the stove, and Lisa called about supper, and the mail came, and the garden gate is shut, and Daniel is at the store.",
            1, "17:05"));
        Check("Fold: five events plus the fold note, remainder verbatim in the fifth body",
            fold.WrittenEvents.Count == 6
            && fold.WrittenEvents[4].Body.Contains("Daniel is at the store")
            && fold.WrittenEvents[5].Type == EventType.HouseholdNote
            && fold.WrittenEvents[5].Body == "Daniel is at the store.");

        // The afternoon ramble, whole. Timmy (4) and Mia (5) are
        // first-class persons in the slice, and Lisa's one breath must
        // land as four events: a pickup, an errand, a homework task,
        // and the open dinner decision.
        var ramble = await svc.ProcessAsync(Input(
            "Timmy needs picking up from soccer at five thirty. Grab groceries on the way back. Mia has math homework tonight and she is stuck. And what are we even doing for dinner?",
            3, "14:03"));
        Check("Ramble: four events from one telling", ramble.WrittenEvents.Count == 4);
        Check("Ramble: pickup is a Task for Timmy, from soccer, due five thirty (5:30 PM resolved)",
            ramble.WrittenEvents.Count == 4
            && ramble.WrittenEvents[0].Type == EventType.Task
            && GetStr(ramble.WrittenEvents[0], "TaskKind") == "Pickup"
            && ramble.WrittenEvents[0].SubjectPersonId == 4
            && GetInt(ramble.WrittenEvents[0], "CollectPersonId") == 4
            && GetStr(ramble.WrittenEvents[0], "PlaceText") == "soccer"
            && GetStr(ramble.WrittenEvents[0], "DueText") == "five thirty"
            && (ramble.WrittenEvents[0].Details ?? "").Contains("\"DueAtLocal\":\"2026-10-27T17:30:00-05:00\"")
            && ramble.WrittenEvents[0].Body == "Timmy needs picking up from soccer at five thirty.");
        Check("Ramble: groceries are an Errand task, nobody's subject, on the way back",
            ramble.WrittenEvents.Count == 4
            && ramble.WrittenEvents[1].Type == EventType.Task
            && GetStr(ramble.WrittenEvents[1], "TaskKind") == "Errand"
            && GetStr(ramble.WrittenEvents[1], "TaskText") == "groceries"
            && GetStr(ramble.WrittenEvents[1], "WhenText") == "on the way back"
            && ramble.WrittenEvents[1].SubjectPersonId is null
            && ramble.WrittenEvents[1].AuthorPersonId == 3);
        Check("Ramble: homework is a General task with Mia as subject and HelpFlag set",
            ramble.WrittenEvents.Count == 4
            && ramble.WrittenEvents[2].Type == EventType.Task
            && GetStr(ramble.WrittenEvents[2], "TaskKind") == "General"
            && GetStr(ramble.WrittenEvents[2], "TaskText") == "math homework"
            && ramble.WrittenEvents[2].SubjectPersonId == 5
            && (ramble.WrittenEvents[2].Details ?? "").Contains("\"HelpFlag\":true")
            && GetStr(ramble.WrittenEvents[2], "WhenText") == "tonight");
        Check("Ramble: dinner lands as an undecided decision on a HouseholdNote, no subject",
            ramble.WrittenEvents.Count == 4
            && ramble.WrittenEvents[3].Type == EventType.HouseholdNote
            && GetStr(ramble.WrittenEvents[3], "DecisionTopic") == "dinner"
            && GetStr(ramble.WrittenEvents[3], "DecisionState") == "Undecided"
            && ramble.WrittenEvents[3].SubjectPersonId is null
            && ramble.WrittenEvents[3].Body == "And what are we even doing for dinner?");
        Check("Ramble: spoken is T-RECORDED, no action proposed, no alert",
            ramble.HouseReply == "Written down."
            && ramble.Output.ProposedAction is null
            && ramble.Output.AlertDecision.Fire == false);

        // The two-line fallback. The same afternoon spoken as two
        // shorter tellings must land as the same four events, two per
        // telling; this is the fallback form if the one-breath version
        // ever fails in use.
        var rambleA = await svc.ProcessAsync(Input(
            "Timmy needs picking up from soccer at five thirty. Grab groceries on the way back.",
            3, "14:03"));
        var rambleB = await svc.ProcessAsync(Input(
            "Mia has math homework tonight and she is stuck. And what are we even doing for dinner?",
            3, "14:04"));
        Check("Fallback line one: pickup and errand, two events",
            rambleA.WrittenEvents.Count == 2
            && rambleA.WrittenEvents[0].Type == EventType.Task
            && GetStr(rambleA.WrittenEvents[0], "TaskKind") == "Pickup"
            && rambleA.WrittenEvents[0].SubjectPersonId == 4
            && rambleA.WrittenEvents[1].Type == EventType.Task
            && GetStr(rambleA.WrittenEvents[1], "TaskKind") == "Errand");
        Check("Fallback line two: homework and the dinner decision, two events",
            rambleB.WrittenEvents.Count == 2
            && rambleB.WrittenEvents[0].Type == EventType.Task
            && GetStr(rambleB.WrittenEvents[0], "TaskText") == "math homework"
            && rambleB.WrittenEvents[0].SubjectPersonId == 5
            && rambleB.WrittenEvents[1].Type == EventType.HouseholdNote
            && GetStr(rambleB.WrittenEvents[1], "DecisionTopic") == "dinner"
            && GetStr(rambleB.WrittenEvents[1], "DecisionState") == "Undecided");

        // Cross-corpus invariants.
        Check("Corpus: exactly one alert delivery exists across all of it (Ex7, confirmed)",
            await db.AlertDeliveries.CountAsync() == 1);
        Check("Corpus: every pipeline output passed the invariant checks",
            new[] { ex1, ex2, ex3, ex4, ex5, ex6, ex7, ex8, ex9, ex10, ex11, ex12, ex13, ex14, ex15, ex16, ex17, ex18, ex19, commit, task, fold, ramble, rambleA, rambleB }
                .All(r => r.Validation.IsValid));

        // Plan assembly.
        var plans = sp.GetRequiredService<PlanAssemblyService>();
        var plan = await plans.CreatePlanAsync(householdId, "Tonight", new DateTime(2026, 10, 27, 17, 30, 0));
        var dinnerNote = new Event
        {
            HouseholdId = householdId,
            Type = EventType.HouseholdNote,
            Body = "Dinner is still up in the air.",
            SubjectPersonId = 3,
            AuthorPersonId = 3,
            CreatedAtUtc = DateTime.UtcNow,
            SensitivityClass = SensitivityClass.Household,
            AudienceRule = """{"Class":"Household"}""",
            Details = """{"MedMention":"None","DecisionTopic":"dinner","DecisionState":"Undecided"}"""
        };
        await confirmations.WriteStatementAsync(dinnerNote, "tap");
        var dinner = await plans.AddElementAsync(plan.Id, PlanElementKind.MealProposal,
            "Soup and sandwiches", new[] { 2, 3 }, sourceEventIds: new[] { dinnerNote.Id });
        await plans.RecordDisposalAsync(dinner.Id, 2, PlanDisposal.Agreed);
        var afterOne = await db.PlanElements.FirstAsync(e => e.Id == dinner.Id);
        Check("Dinner does not settle on one partner's agreement", afterOne.State == PlanElementState.Open);
        await plans.RecordDisposalAsync(dinner.Id, 3, PlanDisposal.Agreed);
        var afterBoth = await db.PlanElements.FirstAsync(e => e.Id == dinner.Id);
        var agreementRows = await db.PlanAgreements.CountAsync(a => a.PlanElementId == dinner.Id && a.Round == 1);
        Check("Dinner settles when both partners agreed, two rows on the record",
            afterBoth.State == PlanElementState.Settled && agreementRows == 2);
        var leaveBy = await plans.AddLeaveByAsync(plan.Id, new DateTime(2026, 10, 27, 17, 30, 0),
            new List<(string, int)> { ("drive to the store", 25), ("shop and load", 15) });
        Check("Leave-by is arithmetic on stated durations only", leaveBy is not null
            && leaveBy.BodyText.StartsWith("Leave by 4:50 PM"));
        var noDurations = await plans.AddLeaveByAsync(plan.Id, new DateTime(2026, 10, 27, 17, 30, 0),
            new List<(string, int)>());
        Check("No stated durations, no leave-by element", noDurations is null);
        await plans.AcknowledgeAsync(plan.Id, 2);
        var ackedPlan = await db.Plans.FirstAsync(p => p.Id == plan.Id);
        Check("Runner acknowledgement activates the plan", ackedPlan.State == PlanState.Acknowledged
            && ackedPlan.RunnerPersonId == 2);

        var plan2 = await plans.CreatePlanAsync(householdId, "Countered plan", new DateTime(2026, 10, 27, 18, 0, 0));
        var el2 = await plans.AddElementAsync(plan2.Id, PlanElementKind.MealProposal, "Pizza", new[] { 2, 3 });
        await plans.RecordDisposalAsync(el2.Id, 2, PlanDisposal.Agreed);
        await plans.RecordDisposalAsync(el2.Id, 3, PlanDisposal.Countered);
        await plans.RecordDisposalAsync(el2.Id, 2, PlanDisposal.Agreed);
        await plans.RecordDisposalAsync(el2.Id, 3, PlanDisposal.Agreed);
        var el2After = await db.PlanElements.Include(e => e.Agreements).FirstAsync(e => e.Id == el2.Id);
        Check("A counter restarts the round; settlement needs both in the new round",
            el2After.State == PlanElementState.Settled
            && el2After.Agreements.Count(a => a.Round == 2) == 2);

        // Promise closing flow, on the promise born above.
        var bornPromise = commit.PromisesBorn[0];
        var closing = new Event
        {
            HouseholdId = householdId,
            Type = EventType.HouseholdNote,
            Body = "Home, like I said this morning.",
            SubjectPersonId = 2,
            AuthorPersonId = 2,
            CreatedAtUtc = DateTime.UtcNow,
            SensitivityClass = SensitivityClass.Household,
            AudienceRule = """{"Class":"Household"}"""
        };
        await confirmations.WriteStatementAsync(closing, "voice");
        await confirmations.ClosePromiseAsync(bornPromise.Id, closing.Id, 2, nameof(PromiseState.Kept), "voice");
        var closedPromise = await db.Promises.FirstAsync(p => p.Id == bornPromise.Id);
        Check("A claim settles only by a person's linked closing telling",
            closedPromise.State == PromiseState.Kept
            && closedPromise.ClosedByEventId == closing.Id && closedPromise.ClosedByPersonId == 2);

        // Audience-filtered brief. The ankle mention (Restricted,
        // Daniel route) appears for Margaret and Daniel, not for Lisa
        // (backup only) or June.
        var briefs = sp.GetRequiredService<BriefService>();
        var today = new DateOnly(2026, 10, 27);
        var margaretBrief = await briefs.BuildBriefAsync(1, today);
        var danielBrief = await briefs.BuildBriefAsync(2, today);
        var lisaBrief = await briefs.BuildBriefAsync(3, today);
        var juneBrief = await briefs.BuildBriefAsync(6, today);
        bool HasAnkle(List<BriefLine> lines) => lines.Any(l => l.Text.Contains("ankle is swollen"));
        Check("Brief: restricted mention reaches subject and route person",
            HasAnkle(margaretBrief) && HasAnkle(danielBrief));
        Check("Brief: restricted mention does not reach backup or helper",
            !HasAnkle(lisaBrief) && !HasAnkle(juneBrief));
        Check("Brief: the supply forecast line reaches the person the item is assigned to",
            danielBrief.Any(l => l.Section == "Supplies" && l.Text.Contains("Wipes")));
        Check("Brief: a supply line does not reach a person it is not assigned to",
            !margaretBrief.Any(l => l.Section == "Supplies" && l.Text.Contains("Wipes")));

        // Fenced whereabouts lookup.
        var lookup = sp.GetRequiredService<LookupService>();
        var danielWhere = await lookup.AnswerWhereaboutsAsync(1, 2);
        Check("Lookup answers with the stated time named", danielWhere.StartsWith("At 2:03 PM, Daniel said:")
            && danielWhere.Contains("Domino's"));
        var nobodyWhere = await lookup.AnswerWhereaboutsAsync(1, 3);
        Check("Lookup with nothing on the record says so", nobodyWhere == "I don't have that written down.");

        // The in-demo delivery path (member views on the house layer;
        // the product's cross-channel delivery stays unbuilt, by
        // design). Each landing is checked for the right person, for
        // nobody else, and for not landing early.
        var memberViews = sp.GetRequiredService<MemberDeliveryService>();

        // Delivery of the Ex7 alert: delivered by the dispatcher on
        // confirmation, it sits verbatim in Daniel's member view and
        // in nobody else's. (That no delivery existed before
        // confirmation is the Ex7 check above.)
        var danielReceived = await memberViews.ReceivedForAsync(2);
        var lisaReceived = await memberViews.ReceivedForAsync(3);
        var margaretReceived = await memberViews.ReceivedForAsync(1);
        Check("Delivery: Daniel's view holds the confirmed alert verbatim",
            danielReceived.Any(i => i.Source == "Alert"
                && i.Text == "My ankle is swollen and it hurts when I stand."));
        Check("Delivery: the alert landed with Daniel only",
            !lisaReceived.Any(i => i.Source == "Alert")
            && !margaretReceived.Any(i => i.Source == "Alert"));

        // Delivery of the afternoon plan, assembled the way the
        // ramble's events shape it: it lands in the runner's (Daniel's)
        // view only when he acknowledges it, leave-by included.
        var afternoon = await plans.CreatePlanAsync(householdId, "This afternoon", new DateTime(2026, 10, 27, 17, 30, 0));
        await plans.AddElementAsync(afternoon.Id, PlanElementKind.Pickup,
            "Pick Timmy up from soccer at five thirty", Array.Empty<int>(), assigneePersonId: 2);
        await plans.AddElementAsync(afternoon.Id, PlanElementKind.Errand,
            "Groceries on the way back", Array.Empty<int>(), assigneePersonId: 2);
        await plans.AddLeaveByAsync(afternoon.Id, new DateTime(2026, 10, 27, 17, 30, 0),
            new List<(string, int)> { ("drive to soccer", 20), ("the pickup itself", 10) });
        var danielBeforeAck = await memberViews.ReceivedForAsync(2);
        Check("Delivery: nothing of the plan lands with Daniel before he acknowledges it",
            !danielBeforeAck.Any(i => i.Text.Contains("Pick Timmy up"))
            && !danielBeforeAck.Any(i => i.Text.Contains("Leave by 5:00 PM")));
        await plans.AcknowledgeAsync(afternoon.Id, 2);
        var danielAfterAck = await memberViews.ReceivedForAsync(2);
        Check("Delivery: the acknowledged plan lands in Daniel's view, leave-by included",
            danielAfterAck.Any(i => i.Text.Contains("Pick Timmy up from soccer"))
            && danielAfterAck.Any(i => i.Text.Contains("Groceries on the way back"))
            && danielAfterAck.Any(i => i.Text.Contains("Leave by 5:00 PM")));
        var lisaAfterAck = await memberViews.ReceivedForAsync(3);
        Check("Delivery: Daniel's plan does not land in Lisa's view",
            !lisaAfterAck.Any(i => i.Text.Contains("Pick Timmy up"))
            && !lisaAfterAck.Any(i => i.Text.Contains("Leave by 5:00 PM")));

        // Delivery of the dinner outcome: it reaches both partners'
        // views only after both Agreement rows are in, never on one,
        // and reaches nobody outside the two disposals.
        var dinnerPlan = await plans.CreatePlanAsync(householdId, "Dinner tonight", null);
        var tacos = await plans.AddElementAsync(dinnerPlan.Id, PlanElementKind.MealProposal,
            "Tacos", new[] { 2, 3 });
        await plans.RecordDisposalAsync(tacos.Id, 3, PlanDisposal.Agreed);
        var danielMidDinner = await memberViews.ReceivedForAsync(2);
        var lisaMidDinner = await memberViews.ReceivedForAsync(3);
        Check("Delivery: no dinner outcome lands on one partner's agreement",
            !danielMidDinner.Any(i => i.Text == "Tacos")
            && !lisaMidDinner.Any(i => i.Text == "Tacos"));
        await plans.RecordDisposalAsync(tacos.Id, 2, PlanDisposal.Agreed);
        var danielEndDinner = await memberViews.ReceivedForAsync(2);
        var lisaEndDinner = await memberViews.ReceivedForAsync(3);
        var margaretEndDinner = await memberViews.ReceivedForAsync(1);
        Check("Delivery: the settled dinner lands in both partners' views",
            danielEndDinner.Any(i => i.Text == "Tacos")
            && lisaEndDinner.Any(i => i.Text == "Tacos"));
        Check("Delivery: the dinner outcome lands with the partners only",
            !margaretEndDinner.Any(i => i.Text == "Tacos"));

        // The verbatim counter: a counter's words are stored on the
        // Agreement row exactly as said, they become the proposal
        // the new round starts from, and they are carried to the
        // other disposer word for word, never back to the counterer
        // and never to anyone the element does not name.
        var counterPlan = await plans.CreatePlanAsync(householdId, "Counter carry", null);
        var counterDinner = await plans.AddElementAsync(counterPlan.Id, PlanElementKind.MealProposal,
            "Tacos", new[] { 2, 3 });
        await plans.RecordDisposalAsync(counterDinner.Id, 3, PlanDisposal.Agreed);
        const string counterWords = "Taco salad instead, we have greens and Lisa's dressing.";
        await plans.RecordDisposalAsync(counterDinner.Id, 2, PlanDisposal.Countered, counterWords);
        var counterRow = await db.PlanAgreements.FirstAsync(a =>
            a.PlanElementId == counterDinner.Id && a.PersonId == 2 && a.Disposal == PlanDisposal.Countered);
        Check("Counter: the counter's words are stored verbatim on the Agreement row",
            counterRow.CounterText == counterWords && counterRow.Round == 1);
        var counterElement = await db.PlanElements.FirstAsync(e => e.Id == counterDinner.Id);
        Check("Counter: the new round starts from the counter's words",
            counterElement.BodyText == counterWords && counterElement.State == PlanElementState.Open);
        var lisaCounterView = await memberViews.ReceivedForAsync(3);
        var danielCounterView = await memberViews.ReceivedForAsync(2);
        var margaretCounterView = await memberViews.ReceivedForAsync(1);
        Check("Counter: the words land with the other disposer, as a counter carry",
            lisaCounterView.Any(i => i.Source == "Counter" && i.Text == counterWords));
        Check("Counter: the words do not land back with the counterer or anyone else",
            !danielCounterView.Any(i => i.Text == counterWords)
            && !margaretCounterView.Any(i => i.Text == counterWords));
        await plans.RecordDisposalAsync(counterDinner.Id, 3, PlanDisposal.Agreed);
        await plans.RecordDisposalAsync(counterDinner.Id, 2, PlanDisposal.Agreed);
        var counterSettled = await db.PlanElements.FirstAsync(e => e.Id == counterDinner.Id);
        var danielCounterSettled = await memberViews.ReceivedForAsync(2);
        var lisaCounterSettled = await memberViews.ReceivedForAsync(3);
        Check("Counter: round two settles on the counter's words, landing with both partners",
            counterSettled.State == PlanElementState.Settled
            && danielCounterSettled.Any(i => i.Text == counterWords)
            && lisaCounterSettled.Count(i => i.Text == counterWords) == 1);

        // A wordless counter restarts the round and carries nothing:
        // no stored words, no revised proposal, no landing.
        var wordlessPlan = await plans.CreatePlanAsync(householdId, "Wordless counter", null);
        var wordless = await plans.AddElementAsync(wordlessPlan.Id, PlanElementKind.MealProposal,
            "Pizza", new[] { 2, 3 });
        await plans.RecordDisposalAsync(wordless.Id, 2, PlanDisposal.Countered);
        var wordlessRow = await db.PlanAgreements.FirstAsync(a => a.PlanElementId == wordless.Id);
        var wordlessElement = await db.PlanElements.FirstAsync(e => e.Id == wordless.Id);
        var lisaWordlessView = await memberViews.ReceivedForAsync(3);
        Check("Counter: a wordless counter stores no words and carries nothing",
            wordlessRow.CounterText is null
            && wordlessElement.BodyText == "Pizza"
            && !lisaWordlessView.Any(i => i.Source == "Counter" && i.Text == "Pizza"));

        // Delivery of a re-time: the re-timed dessert entry is routed
        // to the person it affects (Margaret, the element's assignee)
        // at the new time, and to nobody else; before the change it
        // had not landed with her at all.
        var evening = await plans.CreatePlanAsync(householdId, "Tonight at home", new DateTime(2026, 10, 27, 19, 0, 0));
        var dessert = await plans.AddElementAsync(evening.Id, PlanElementKind.Task,
            "The neighbors come for dessert at seven", Array.Empty<int>(), assigneePersonId: 1);
        var margaretBeforeRetime = await memberViews.ReceivedForAsync(1);
        Check("Delivery: the dessert entry has not landed with Margaret before the change",
            !margaretBeforeRetime.Any(i => i.Text.Contains("dessert")));
        await plans.RetimeElementAsync(dessert.Id, "The neighbors come for dessert at eight thirty", 3);
        var margaretAfterRetime = await memberViews.ReceivedForAsync(1);
        Check("Delivery: the re-timed entry lands in the affected person's view at the new time",
            margaretAfterRetime.Any(i => i.Source == "Change" && i.Text.Contains("eight thirty")));
        var danielAfterRetime = await memberViews.ReceivedForAsync(2);
        Check("Delivery: the re-timed entry lands with the affected person only",
            !danielAfterRetime.Any(i => i.Text.Contains("eight thirty")));

        // The re-time through the Plans page handler itself: the
        // service checks above prove the machinery, this proves the
        // surface a person actually meets performs the act, lands
        // the new wording with the affected person only, and treats
        // blank words as no change at all.
        var pagePlan = await plans.CreatePlanAsync(householdId, "Through the page", new DateTime(2026, 10, 27, 19, 0, 0));
        var pageDessert = await plans.AddElementAsync(pagePlan.Id, PlanElementKind.Task,
            "June brings dessert over at seven", Array.Empty<int>(), assigneePersonId: 1);
        var plansPage = new HouseholdCoordinator.Pages.PlansModel(db, plans)
        {
            TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(
                new Microsoft.AspNetCore.Http.DefaultHttpContext(),
                new PassthroughTempDataProvider())
        };
        await plansPage.OnPostRetimeAsync(pageDessert.Id, "June brings dessert over at eight thirty", 3);
        var pageDessertAfter = await db.PlanElements.FirstAsync(e => e.Id == pageDessert.Id);
        var margaretPageView = await memberViews.ReceivedForAsync(1);
        var danielPageView = await memberViews.ReceivedForAsync(2);
        Check("Page: the Plans retime handler re-times the entry in the new words",
            pageDessertAfter.BodyText == "June brings dessert over at eight thirty");
        Check("Page: the handler's re-time lands with the affected person only",
            margaretPageView.Any(i => i.Source == "Change" && i.Text.Contains("June brings dessert over at eight thirty"))
            && !danielPageView.Any(i => i.Text.Contains("June brings dessert")));
        await plansPage.OnPostRetimeAsync(pageDessert.Id, "   ", 3);
        var pageDessertBlank = await db.PlanElements.FirstAsync(e => e.Id == pageDessert.Id);
        Check("Page: blank re-time words change nothing",
            pageDessertBlank.BodyText == "June brings dessert over at eight thirty");

        // The disposal form's counter words travel the page handler
        // into the Agreement row, verbatim.
        var pageCounterPlan = await plans.CreatePlanAsync(householdId, "Counter through the page", null);
        var pageCounter = await plans.AddElementAsync(pageCounterPlan.Id, PlanElementKind.MealProposal,
            "Soup", new[] { 2, 3 });
        await plansPage.OnPostDisposeAsync(pageCounter.Id, 2, "Countered", "Soup, but with the barley we already have.");
        var pageCounterRow = await db.PlanAgreements.FirstAsync(a => a.PlanElementId == pageCounter.Id);
        Check("Page: the disposal form's counter words land on the Agreement row verbatim",
            pageCounterRow.CounterText == "Soup, but with the barley we already have.");

        // The filmable beats. Beat 4's two small builds, Beat
        // 3's provenance, Beat 5's re-time audit row, and provenance
        // rendering for federated events.
        var dinnerProv = sp.GetRequiredService<DinnerProvenanceService>();

        // Beat 4, the stated turn: Lisa states Daniel's trash turn,
        // and the telling lands as a Proposed Task carrying a
        // proposed NotifyPerson for Daniel, the assigned person, and
        // for nobody else. Nothing is delivered before a person
        // confirms it; confirmation delivers exactly once, to
        // Daniel; and Daniel's closing tap on his own receiving
        // surface closes the delivery with an audit row naming him.
        var deliveriesBeforeTurn = await db.AlertDeliveries.CountAsync();
        var turn = await svc.ProcessAsync(Input(
            "Tonight is Daniel's turn to take the trash out, the truck comes at seven.", 3, "19:02"));
        Check("Beat 4: a stated turn lands as one Proposed task naming the assigned person",
            turn.WrittenEvents.Count == 1
            && turn.WrittenEvents[0].Type == EventType.Task
            && turn.WrittenEvents[0].ConfirmationState == ConfirmationState.Proposed
            && turn.WrittenEvents[0].SubjectPersonId == 2
            && GetInt(turn.WrittenEvents[0], "TurnPersonId") == 2
            && GetStr(turn.WrittenEvents[0], "TaskText") == "take the trash out");
        Check("Beat 4: the turn proposes a NotifyPerson for the assigned person only",
            turn.Output.ProposedAction?.ActionType == "NotifyPerson"
            && turn.Output.ProposedAction?.TargetPersonId == 2
            && turn.HouseReply == "Do you want me to tell Daniel?"
            && turn.Validation.IsValid);
        Check("Beat 4: the turn's stored audience routes to the assigned person",
            (turn.WrittenEvents[0].AudienceRule ?? "").Contains("\"RoutePersonId\":2")
            && turn.WrittenEvents[0].SensitivityClass == SensitivityClass.Named);
        Check("Beat 4: no delivery exists before anyone confirms the turn",
            await db.AlertDeliveries.CountAsync() == deliveriesBeforeTurn);
        await confirmations.ConfirmAsync(turn.WrittenEvents[0].Id, 3, "tap");
        var turnDelivery = await db.AlertDeliveries.FirstAsync(d => d.EventId == turn.WrittenEvents[0].Id);
        var turnAfter = await db.Events.FirstAsync(e => e.Id == turn.WrittenEvents[0].Id);
        Check("Beat 4: confirmation delivers the turn verbatim to Daniel, once, and to nobody else",
            turnDelivery.ToPersonId == 2
            && turnDelivery.BodyText == "Tonight is Daniel's turn to take the trash out, the truck comes at seven."
            && await db.AlertDeliveries.CountAsync(d => d.EventId == turn.WrittenEvents[0].Id) == 1
            && turnAfter.ConfirmationState == ConfirmationState.Done);
        var lisaTurnView = await memberViews.ReceivedForAsync(3);
        Check("Beat 4: the turn reminder does not land in anyone else's view",
            !lisaTurnView.Any(i => i.Text.Contains("turn to take the trash")));
        var danielTurnView = await memberViews.ReceivedForAsync(2);
        Check("Beat 4: Daniel's view holds the turn as an open alert he can close",
            danielTurnView.Any(i => i.AlertDeliveryId == turnDelivery.Id && i.ClosedAtUtc is null));

        // A closing tap by anyone but the addressee is refused and
        // writes nothing (the Ex7 delivery is still open here).
        var ex7Delivery = await db.AlertDeliveries.FirstAsync(d => d.BodyText.Contains("ankle is swollen"));
        var auditsBeforeWrongTap = await db.EventAudits.CountAsync(a => a.EventId == ex7Delivery.EventId);
        var wrongTapThrew = false;
        try { await confirmations.CloseDeliveryAsync(ex7Delivery.Id, 3, "tap"); }
        catch (InvalidOperationException) { wrongTapThrew = true; }
        Check("Beat 4: a closing tap by a non-addressee is refused and writes no audit row",
            wrongTapThrew
            && await db.EventAudits.CountAsync(a => a.EventId == ex7Delivery.EventId) == auditsBeforeWrongTap
            && (await db.AlertDeliveries.FirstAsync(d => d.Id == ex7Delivery.Id)).AcknowledgedAtUtc is null);

        await confirmations.CloseDeliveryAsync(turnDelivery.Id, 2, "tap");
        var turnDeliveryClosed = await db.AlertDeliveries.FirstAsync(d => d.Id == turnDelivery.Id);
        Check("Beat 4: the addressee's closing tap stamps the delivery and writes an audit row naming him",
            turnDeliveryClosed.AcknowledgedAtUtc is not null
            && await db.EventAudits.CountAsync(a => a.EventId == turn.WrittenEvents[0].Id
                && a.ToState == "Acknowledged" && a.ActorPersonId == 2 && a.Channel == "tap") == 1);
        var secondTapThrew = false;
        try { await confirmations.CloseDeliveryAsync(turnDelivery.Id, 2, "tap"); }
        catch (InvalidOperationException) { secondTapThrew = true; }
        Check("Beat 4: a second closing tap changes nothing and writes no second row",
            secondTapThrew
            && await db.EventAudits.CountAsync(a => a.EventId == turn.WrittenEvents[0].Id && a.ToState == "Acknowledged") == 1);

        // The closing tap through the Member page handler itself, on
        // a second turn (the bins shape, stated by Lisa for Daniel).
        var turn2 = await svc.ProcessAsync(Input("Daniel takes the bins out tonight.", 3, "19:20"));
        await confirmations.ConfirmAsync(turn2.WrittenEvents[0].Id, 3, "tap");
        var turn2Delivery = await db.AlertDeliveries.FirstAsync(d => d.EventId == turn2.WrittenEvents[0].Id);
        var memberPage = new HouseholdCoordinator.Pages.MemberModel(db, memberViews, confirmations)
        {
            TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(
                new Microsoft.AspNetCore.Http.DefaultHttpContext(),
                new PassthroughTempDataProvider())
        };
        await memberPage.OnPostCloseDeliveryAsync(turn2Delivery.Id, 2);
        Check("Beat 4: the Member page's closing tap closes the addressee's delivery with his audit row",
            turn2.WrittenEvents.Count == 1 && turn2Delivery.ToPersonId == 2
            && (await db.AlertDeliveries.FirstAsync(d => d.Id == turn2Delivery.Id)).AcknowledgedAtUtc is not null
            && await db.EventAudits.CountAsync(a => a.EventId == turn2.WrittenEvents[0].Id
                && a.ToState == "Acknowledged" && a.ActorPersonId == 2) == 1);
        var danielClosedView = await memberViews.ReceivedForAsync(2);
        Check("Beat 4: after the tap, Daniel's view shows the turn as closed",
            danielClosedView.Any(i => i.AlertDeliveryId == turn2Delivery.Id && i.ClosedAtUtc is not null));

        // Beat 3 provenance: usual meals and fridge contents exist
        // only as tellings, each with an author and a day, and a
        // dinner proposal is built from those tellings alone.
        async Task<Event> ProvenanceTelling(int authorId, string body, string kind, string itemName, DateTime atUtc)
        {
            var ev = new Event
            {
                HouseholdId = householdId,
                Type = EventType.HouseholdNote,
                Body = body,
                AuthorPersonId = authorId,
                CreatedAtUtc = atUtc,
                SensitivityClass = SensitivityClass.Household,
                AudienceRule = """{"Class":"Household"}""",
                Details = "{\"MedMention\":\"None\",\"MealProvenance\":\"" + kind + "\",\"ItemName\":\"" + itemName + "\"}"
            };
            return await confirmations.WriteStatementAsync(ev, "voice");
        }
        var tacosTelling = await ProvenanceTelling(3, "Tacos are one of our usual dinners.",
            "UsualMeal", "Tacos", new DateTime(2026, 10, 25, 18, 5, 0, DateTimeKind.Utc));
        var soupTelling = await ProvenanceTelling(2, "Soup and sandwiches is one of our usual dinners.",
            "UsualMeal", "Soup and sandwiches", new DateTime(2026, 10, 24, 19, 12, 0, DateTimeKind.Utc));
        var eggsTelling = await ProvenanceTelling(3, "There are eggs in the fridge.",
            "Fridge", "eggs", new DateTime(2026, 10, 26, 9, 20, 0, DateTimeKind.Utc));
        var greensTelling = await ProvenanceTelling(2, "There are greens in the fridge.",
            "Fridge", "greens", new DateTime(2026, 10, 26, 17, 40, 0, DateTimeKind.Utc));
        var provItems = await dinnerProv.GetItemsAsync(householdId);
        Check("Beat 3: each provenance item carries its telling's author and day",
            provItems.Any(i => i.ItemName == "Tacos" && i.Kind == "UsualMeal"
                && i.AuthorName == "Lisa Ellis" && i.ToldOn == new DateOnly(2026, 10, 25)
                && i.SourceEventId == tacosTelling.Id)
            && provItems.Any(i => i.ItemName == "Soup and sandwiches" && i.Kind == "UsualMeal"
                && i.AuthorName == "Daniel Ellis" && i.ToldOn == new DateOnly(2026, 10, 24))
            && provItems.Any(i => i.ItemName == "greens" && i.Kind == "Fridge"
                && i.AuthorName == "Daniel Ellis" && i.ToldOn == new DateOnly(2026, 10, 26)));
        var provPlan = await plans.CreatePlanAsync(householdId, "Dinner from tellings", null);
        var provElement = await dinnerProv.ProposeDinnerAsync(provPlan.Id,
            new[] { tacosTelling.Id, eggsTelling.Id, greensTelling.Id }, new[] { 2, 3 });
        Check("Beat 3: a proposal built from tellings names the meal and the fridge contents it draws on",
            provElement.BodyText == "Tacos (from the fridge: eggs, greens)"
            && (provElement.SourceEventIds ?? "").Contains(tacosTelling.Id.ToString())
            && (provElement.SourceEventIds ?? "").Contains(eggsTelling.Id.ToString())
            && (provElement.SourceEventIds ?? "").Contains(greensTelling.Id.ToString()));
        var elementsBeforeRefusals = await db.PlanElements.CountAsync(e => e.PlanId == provPlan.Id);
        var refusedNoTelling = false;
        try { await dinnerProv.ProposeDinnerAsync(provPlan.Id, new[] { danielNote.Id }, new[] { 2, 3 }); }
        catch (InvalidOperationException) { refusedNoTelling = true; }
        var refusedFridgeOnly = false;
        try { await dinnerProv.ProposeDinnerAsync(provPlan.Id, new[] { eggsTelling.Id }, new[] { 2, 3 }); }
        catch (InvalidOperationException) { refusedFridgeOnly = true; }
        var refusedEmpty = false;
        try { await dinnerProv.ProposeDinnerAsync(provPlan.Id, Array.Empty<int>(), new[] { 2, 3 }); }
        catch (InvalidOperationException) { refusedEmpty = true; }
        Check("Beat 3: a proposal with no telling, no usual meal, or no items at all is refused and writes nothing",
            refusedNoTelling && refusedFridgeOnly && refusedEmpty
            && await db.PlanElements.CountAsync(e => e.PlanId == provPlan.Id) == elementsBeforeRefusals);
        await plans.RecordDisposalAsync(provElement.Id, 3, PlanDisposal.Agreed);
        var danielProvMid = await memberViews.ReceivedForAsync(2);
        Check("Beat 3: the provenance dinner does not land on one partner's agreement",
            !danielProvMid.Any(i => i.Text.StartsWith("Tacos (from the fridge")));
        await plans.RecordDisposalAsync(provElement.Id, 2, PlanDisposal.Agreed);
        var danielProvEnd = await memberViews.ReceivedForAsync(2);
        var lisaProvEnd = await memberViews.ReceivedForAsync(3);
        var margaretProvEnd = await memberViews.ReceivedForAsync(1);
        Check("Beat 3: the settled provenance dinner lands in both partners' views and nobody else's",
            danielProvEnd.Any(i => i.Text == "Tacos (from the fridge: eggs, greens)")
            && lisaProvEnd.Any(i => i.Text == "Tacos (from the fridge: eggs, greens)")
            && !margaretProvEnd.Any(i => i.Text.StartsWith("Tacos (from the fridge")));
        var provPlansPage = new HouseholdCoordinator.Pages.PlansModel(db, plans)
        {
            TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(
                new Microsoft.AspNetCore.Http.DefaultHttpContext(),
                new PassthroughTempDataProvider())
        };
        await provPlansPage.OnGetAsync();
        Check("Beat 3: the Plans surface resolves the proposal's tellings with author and day",
            provPlansPage.ElementProvenance.TryGetValue(provElement.Id, out var resolved)
            && resolved.Count == 3
            && resolved.Any(i => i.ItemName == "Tacos" && i.AuthorName == "Lisa Ellis" && i.ToldOn == new DateOnly(2026, 10, 25))
            && provPlansPage.ProvenanceItems.Any(i => i.ItemName == "Soup and sandwiches"));
        var elementsBeforePageRefusal = await db.PlanElements.CountAsync(e => e.PlanId == provPlan.Id);
        await provPlansPage.OnPostAddDinnerAsync(provPlan.Id, null, "Lobster thermidor", new[] { 2, 3 });
        Check("Beat 3: the Plans dinner form refuses words with no telling behind them",
            await db.PlanElements.CountAsync(e => e.PlanId == provPlan.Id) == elementsBeforePageRefusal);
        await provPlansPage.OnPostAddDinnerAsync(provPlan.Id, null, "Soup and sandwiches", new[] { 2, 3 });
        var pageMeal = await db.PlanElements
            .Where(e => e.PlanId == provPlan.Id && e.BodyText == "Soup and sandwiches")
            .FirstOrDefaultAsync();
        Check("Beat 3: the Plans dinner form proposes a stated usual meal with its telling as the source",
            pageMeal is not null && (pageMeal.SourceEventIds ?? "").Contains(soupTelling.Id.ToString()));

        // Beat 5 audit: the re-time performed earlier in this suite
        // (the dessert entry, re-timed by Lisa) now carries an audit
        // row naming the actor with the words before and after.
        Check("Beat 5: a re-time writes an audit row naming the actor, with the words before and after",
            await db.PlanChangeAudits.CountAsync(a => a.PlanElementId == dessert.Id
                && a.ActorPersonId == 3
                && a.FromText.Contains("dessert at seven")
                && a.ToText.Contains("eight thirty")) == 1);

        // Provenance rendering for federated events: an event the
        // node took in from a member's own channel carries its
        // origin, connection, and packet, rendered the same way on
        // the front door, the timeline, and the member views, and
        // filtered by the stored audience exactly as those surfaces
        // already filter. A plain lane event renders no provenance.
        var fedEvent = new Event
        {
            HouseholdId = householdId,
            Type = EventType.HouseholdNote,
            Body = "Federated check: a telling that arrived through a member's own channel.",
            SubjectPersonId = 2,
            AuthorPersonId = 3,
            CreatedAtUtc = DateTime.UtcNow,
            SensitivityClass = SensitivityClass.Household,
            AudienceRule = """{"Class":"Household"}""",
            ProvenanceOrigin = "Lisa's assistant",
            ProvenanceConnection = "conn-lisa-1",
            ProvenancePacketId = "pkt-provenance-1"
        };
        await confirmations.WriteStatementAsync(fedEvent, "tap");
        Check("Provenance: a federated event renders origin, connection, and packet in one sentence",
            ProvenanceRendering.Describe(fedEvent)
                == "Came via Lisa's assistant, connection conn-lisa-1, packet pkt-provenance-1.");
        Check("Provenance: a plain lane event renders no provenance line",
            ProvenanceRendering.Describe(danielNote) is null);
        var fedRestricted = new Event
        {
            HouseholdId = householdId,
            Type = EventType.HealthMention,
            Body = "Federated check: a restricted telling that arrived through a channel.",
            SubjectPersonId = 1,
            AuthorPersonId = 1,
            CreatedAtUtc = DateTime.UtcNow,
            SensitivityClass = SensitivityClass.Restricted,
            AudienceRule = """{"Class":"Restricted","RoutePersonId":2,"BackupPersonId":3}""",
            ProvenanceOrigin = "Margaret's assistant",
            ProvenanceConnection = "conn-margaret-1",
            ProvenancePacketId = "pkt-provenance-9"
        };
        await confirmations.WriteStatementAsync(fedRestricted, "tap");
        var frontDoorDanielFed = await FrontDoorAs(2);
        var frontDoorTimmyFed = await FrontDoorAs(4);
        Check("Provenance: the front door shows a household federated event, provenance included in its data",
            FrontDoorHas(frontDoorDanielFed, "arrived through a member's own channel")
            && FrontDoorHas(frontDoorTimmyFed, "arrived through a member's own channel"));
        Check("Provenance: the front door hides a restricted federated event, and its provenance, from a reader it does not name",
            FrontDoorHas(frontDoorDanielFed, "a restricted telling that arrived")
            && !FrontDoorHas(frontDoorTimmyFed, "a restricted telling that arrived"));
        var timelineDanielFed = await TimelineAs(2);
        var timelineTimmyFed = await TimelineAs(4);
        Check("Provenance: the timeline filters a restricted federated event exactly as any other entry",
            TimelineHas(timelineDanielFed, "a restricted telling that arrived")
            && TimelineHas(timelineDanielFed, "arrived through a member's own channel")
            && !TimelineHas(timelineTimmyFed, "a restricted telling that arrived")
            && TimelineHas(timelineTimmyFed, "arrived through a member's own channel"));
        var fedAlertEvent = new Event
        {
            HouseholdId = householdId,
            Type = EventType.HouseholdNote,
            Body = "Federated check: the bins moved to the side gate.",
            SubjectPersonId = 2,
            AuthorPersonId = 3,
            CreatedAtUtc = DateTime.UtcNow,
            SensitivityClass = SensitivityClass.Named,
            AudienceRule = """{"Class":"Named","RoutePersonId":2}""",
            ProvenanceOrigin = "Lisa's assistant",
            ProvenanceConnection = "conn-lisa-1",
            ProvenancePacketId = "pkt-provenance-2"
        };
        var fedAction = new EventAction
        {
            ActionType = EventActionType.NotifyPerson,
            TargetPersonId = 2,
            CreatedAtUtc = DateTime.UtcNow
        };
        await confirmations.ProposeAsync(fedAlertEvent, fedAction, "tap");
        await confirmations.ConfirmAsync(fedAlertEvent.Id, 3, "tap");
        var danielFedView = await memberViews.ReceivedForAsync(2);
        var margaretFedView = await memberViews.ReceivedForAsync(1);
        Check("Provenance: a federated alert in the member view carries its origin, connection, and packet",
            danielFedView.Any(i => i.Source == "Alert" && i.Text.Contains("bins moved to the side gate")
                && i.Provenance == "Came via Lisa's assistant, connection conn-lisa-1, packet pkt-provenance-2."));
        Check("Provenance: the federated alert, and its provenance, land with its addressee only",
            !margaretFedView.Any(i => i.Text.Contains("bins moved to the side gate")));

        // The 24-hour lapse, exercised through the same entry point
        // the LapseService timer calls in the running app. Times are
        // fixed offsets from one sweep instant so the checks are
        // deterministic: a proposal exactly at its 24-hour mark
        // lapses, a fresh one does not, a record-pending fact never
        // lapses however old, and a second sweep changes nothing
        // (a lapsed action is never re-proposed and the audit gains
        // no second row). These checks run last: a sweep at a future
        // instant may also lapse the corpus proposals above, which
        // nothing after this point depends on.
        var sweepNow = DateTime.UtcNow;
        async Task<Event> ProposalAt(DateTime createdAtUtc, string body)
        {
            var ev = new Event
            {
                HouseholdId = householdId,
                Type = EventType.Request,
                Body = body,
                SubjectPersonId = 1,
                AuthorPersonId = 1,
                CreatedAtUtc = createdAtUtc,
                SensitivityClass = SensitivityClass.Household,
                AudienceRule = """{"Class":"Household"}"""
            };
            var action = new EventAction
            {
                ActionType = EventActionType.NotifyPerson,
                TargetPersonId = 2,
                CreatedAtUtc = createdAtUtc
            };
            return await confirmations.ProposeAsync(ev, action, "voice");
        }

        var staleProposal = await ProposalAt(sweepNow.AddHours(-24), "Lapse check: exactly at the mark.");
        var freshProposal = await ProposalAt(sweepNow.AddHours(25), "Lapse check: a minute old at sweep time.");
        var pendingFact = new Event
        {
            HouseholdId = householdId,
            Type = EventType.Supply,
            Body = "Lapse check: a person-free fact, three days old.",
            CreatedAtUtc = sweepNow.AddHours(-72),
            SensitivityClass = SensitivityClass.Household,
            AudienceRule = """{"Class":"Household"}"""
        };
        await confirmations.WriteStatementAsync(pendingFact, "tap");

        await confirmations.LapseExpiredAsync(sweepNow);
        var staleAfter = await db.Events.FirstAsync(e => e.Id == staleProposal.Id);
        var freshAfterFirst = await db.Events.FirstAsync(e => e.Id == freshProposal.Id);
        var pendingAfter = await db.Events.FirstAsync(e => e.Id == pendingFact.Id);
        Check("Lapse: a proposal at its 24-hour mark lapses to NotConfirmed",
            staleAfter.ConfirmationState == ConfirmationState.NotConfirmed);
        Check("Lapse: the audit row names the system, channel lapse, no person",
            await db.EventAudits.CountAsync(a => a.EventId == staleProposal.Id
                && a.Channel == "lapse" && a.ActorPersonId == null
                && a.ToState == nameof(ConfirmationState.NotConfirmed)) == 1);
        Check("Lapse: a record-pending fact is never discarded, however old",
            pendingAfter.ConfirmationState == ConfirmationState.Proposed && pendingAfter.RecordPending);
        await confirmations.LapseExpiredAsync(sweepNow.AddHours(25).AddMinutes(1));
        var freshAfterSecond = await db.Events.FirstAsync(e => e.Id == freshProposal.Id);
        Check("Lapse: a fresh proposal is untouched by both sweeps",
            freshAfterFirst.ConfirmationState == ConfirmationState.Proposed
            && freshAfterSecond.ConfirmationState == ConfirmationState.Proposed);
        await confirmations.LapseExpiredAsync(sweepNow.AddHours(49));
        Check("Lapse: a later sweep adds no second audit row and never re-proposes",
            await db.EventAudits.CountAsync(a => a.EventId == staleProposal.Id && a.Channel == "lapse") == 1
            && (await db.Events.FirstAsync(e => e.Id == staleProposal.Id)).ConfirmationState
                == ConfirmationState.NotConfirmed);

        // The stored audience, enforced where the record is read and
        // where a confirmed telling is dispatched. Three entries are
        // written directly to the record: a Restricted entry whose
        // rule names Daniel as route and Lisa as backup, a Personal
        // journal entry of Margaret's, and a Restricted entry with
        // no stored audience at all, which must render for nobody.
        // The page models are exercised directly, the way the
        // re-time checks exercise the Plans page: the surface a
        // person meets, not only the services under it.
        var restrictedMarker = new Event
        {
            HouseholdId = householdId,
            Type = EventType.HealthMention,
            Body = "Audience check: a restricted entry naming its route.",
            SubjectPersonId = 1,
            AuthorPersonId = 1,
            CreatedAtUtc = DateTime.UtcNow,
            SensitivityClass = SensitivityClass.Restricted,
            AudienceRule = """{"Class":"Restricted","RoutePersonId":2,"BackupPersonId":3}"""
        };
        await confirmations.WriteStatementAsync(restrictedMarker, "tap");
        var journalMarker = new Event
        {
            HouseholdId = householdId,
            Type = EventType.Journal,
            Body = "Audience check: a personal journal entry.",
            SubjectPersonId = 1,
            AuthorPersonId = 1,
            CreatedAtUtc = DateTime.UtcNow,
            SensitivityClass = SensitivityClass.Personal,
            AudienceRule = """{"Class":"Personal"}"""
        };
        await confirmations.WriteStatementAsync(journalMarker, "tap");
        var rulelessMarker = new Event
        {
            HouseholdId = householdId,
            Type = EventType.HealthMention,
            Body = "Audience check: a restricted entry with no stored audience.",
            CreatedAtUtc = DateTime.UtcNow,
            SensitivityClass = SensitivityClass.Restricted,
            AudienceRule = null
        };
        await confirmations.WriteStatementAsync(rulelessMarker, "tap");

        async Task<HouseholdCoordinator.Pages.TimelineModel> TimelineAs(int actorId)
        {
            var page = new HouseholdCoordinator.Pages.TimelineModel(db, confirmations) { ActorId = actorId };
            await page.OnGetAsync();
            return page;
        }
        var timelineMargaret = await TimelineAs(1);
        var timelineDaniel = await TimelineAs(2);
        var timelineLisa = await TimelineAs(3);
        var timelineTimmy = await TimelineAs(4);
        bool TimelineHas(HouseholdCoordinator.Pages.TimelineModel page, string text)
            => page.Events.Any(e => e.Body.Contains(text));
        Check("Audience: the timeline shows a restricted entry to its subject and its route person",
            TimelineHas(timelineMargaret, "a restricted entry naming its route")
            && TimelineHas(timelineDaniel, "a restricted entry naming its route"));
        Check("Audience: the timeline hides a restricted entry from the backup and from a child",
            !TimelineHas(timelineLisa, "a restricted entry naming its route")
            && !TimelineHas(timelineTimmy, "a restricted entry naming its route"));
        Check("Audience: the timeline shows a personal entry to its subject only",
            TimelineHas(timelineMargaret, "a personal journal entry")
            && !TimelineHas(timelineDaniel, "a personal journal entry"));
        Check("Audience: a delivery shows on the timeline only to the person it was delivered to",
            timelineDaniel.Deliveries.Any(d => d.BodyText == "My ankle is swollen and it hurts when I stand.")
            && !timelineMargaret.Deliveries.Any(d => d.BodyText == "My ankle is swollen and it hurts when I stand."));
        Check("Audience: an entry with no stored audience renders for nobody on the timeline",
            !TimelineHas(timelineMargaret, "no stored audience")
            && !TimelineHas(timelineDaniel, "no stored audience"));

        async Task<HouseholdCoordinator.Pages.FrontDoorModel> FrontDoorAs(int? viewerId)
        {
            var page = new HouseholdCoordinator.Pages.FrontDoorModel(db, builder, svc, confirmations) { ViewerId = viewerId };
            await page.OnGetAsync();
            return page;
        }
        var frontDoorOpen = await FrontDoorAs(null);
        var frontDoorDaniel = await FrontDoorAs(2);
        var frontDoorTimmy = await FrontDoorAs(4);
        bool FrontDoorHas(HouseholdCoordinator.Pages.FrontDoorModel page, string text)
            => page.RecentEvents.Any(e => e.Body.Contains(text));
        Check("Audience: the front door with no reader named shows neither restricted nor personal entries",
            !FrontDoorHas(frontDoorOpen, "a restricted entry naming its route")
            && !FrontDoorHas(frontDoorOpen, "a personal journal entry")
            && !FrontDoorHas(frontDoorOpen, "no stored audience"));
        Check("Audience: the front door reading as the route person shows the restricted entry, not the journal",
            FrontDoorHas(frontDoorDaniel, "a restricted entry naming its route")
            && !FrontDoorHas(frontDoorDaniel, "a personal journal entry"));
        Check("Audience: the front door reading as a child shows neither restricted nor personal entries",
            !FrontDoorHas(frontDoorTimmy, "a restricted entry naming its route")
            && !FrontDoorHas(frontDoorTimmy, "a personal journal entry"));

        // The medication order line in the briefs, per person: the
        // heart pills item is assigned to Lisa, so her brief carries
        // its order line and nobody else's does, the children's
        // included.
        var timmyBrief = await briefs.BuildBriefAsync(4, today);
        var miaBrief = await briefs.BuildBriefAsync(5, today);
        bool HasMedLine(List<BriefLine> lines) => lines.Any(l => l.Section == "Supplies" && l.Text.Contains("Heart pills"));
        Check("Brief: the medication order line reaches the person it is assigned to",
            HasMedLine(lisaBrief));
        Check("Brief: the medication order line reaches nobody else, children included",
            !HasMedLine(danielBrief) && !HasMedLine(margaretBrief) && !HasMedLine(juneBrief)
            && !HasMedLine(timmyBrief) && !HasMedLine(miaBrief));

        // The dispatcher takes its recipient from the event's stored
        // audience, never from the household's current routing. The
        // household's routing is changed first (the current level
        // now points at June, and the household's level key names a
        // level with no route at all); a confirmation whose stored
        // rule names Daniel must still deliver to Daniel.
        var nowLevel = await db.AlertLevels.FirstAsync(l => l.HouseholdId == householdId && l.LevelKey == "now");
        nowLevel.RouteToPersonId = 6;
        nowLevel.BackupPersonId = 6;
        var householdRow = await db.Households.FirstAsync(h => h.Id == householdId);
        householdRow.HealthMentionLevelKey = "here";
        await db.SaveChangesAsync();

        async Task<Event> DispatchProposal(string body, string rule)
        {
            var ev = new Event
            {
                HouseholdId = householdId,
                Type = EventType.HealthMention,
                Body = body,
                SubjectPersonId = 1,
                AuthorPersonId = 1,
                CreatedAtUtc = DateTime.UtcNow,
                SensitivityClass = SensitivityClass.Restricted,
                AudienceRule = rule
            };
            var action = new EventAction
            {
                ActionType = EventActionType.NotifyPerson,
                TargetPersonId = 2,
                CreatedAtUtc = DateTime.UtcNow
            };
            return await confirmations.ProposeAsync(ev, action, "voice");
        }

        var storedRoute = await DispatchProposal("Dispatch check: the stored route decides.",
            """{"Class":"Restricted","RoutePersonId":2,"BackupPersonId":3}""");
        await confirmations.ConfirmAsync(storedRoute.Id, 1, "tap");
        var storedDelivery = await db.AlertDeliveries.FirstAsync(d => d.EventId == storedRoute.Id);
        var storedAfter = await db.Events.FirstAsync(e => e.Id == storedRoute.Id);
        Check("Dispatch: the delivery follows the stored route after the household's routing changed",
            storedDelivery.ToPersonId == 2 && !storedDelivery.IsBackupHop
            && storedAfter.ConfirmationState == ConfirmationState.Done);

        var backupHop = await DispatchProposal("Dispatch check: the backup is the fallback only.",
            """{"Class":"Restricted","RoutePersonId":999,"BackupPersonId":3}""");
        await confirmations.ConfirmAsync(backupHop.Id, 1, "tap");
        var backupDelivery = await db.AlertDeliveries.FirstAsync(d => d.EventId == backupHop.Id);
        Check("Dispatch: the stored backup receives only when the stored route names nobody reachable",
            backupDelivery.ToPersonId == 3 && backupDelivery.IsBackupHop);

        var noRoute = await DispatchProposal("Dispatch check: nobody named, nobody told.",
            """{"Class":"Restricted"}""");
        await confirmations.ConfirmAsync(noRoute.Id, 1, "tap");
        var noRouteAfter = await db.Events.FirstAsync(e => e.Id == noRoute.Id);
        Check("Dispatch: a stored rule naming nobody reachable writes no delivery and leaves the event owed",
            await db.AlertDeliveries.CountAsync(d => d.EventId == noRoute.Id) == 0
            && noRouteAfter.ConfirmationState == ConfirmationState.Confirmed
            && await db.EventAudits.CountAsync(a => a.EventId == noRoute.Id && a.Channel == "executor") == 1);

        nowLevel.RouteToPersonId = 2;
        nowLevel.BackupPersonId = 3;
        householdRow.HealthMentionLevelKey = "now";
        await db.SaveChangesAsync();

        // The monolith's /Promises
        // and /Plans read surfaces fence by the stored audience,
        // as the node's equivalents and this suite's brief and
        // timeline checks already do. A commitment stored Named to
        // its maker alone, a personal undecided note, and an
        // element built from a personal telling are exercised
        // through the page models themselves, per reader.
        var fenceCommitment = new Event
        {
            HouseholdId = householdId,
            Type = EventType.Commitment,
            Body = "Fence check: a commitment for its maker only.",
            SubjectPersonId = 2,
            AuthorPersonId = 2,
            CreatedAtUtc = DateTime.UtcNow,
            ConfirmationState = ConfirmationState.Confirmed,
            SensitivityClass = SensitivityClass.Named,
            AudienceRule = """{"Class":"Named","MakerPersonId":2}"""
        };
        await confirmations.WriteStatementAsync(fenceCommitment, "tap");
        db.Promises.Add(new Promise
        {
            HouseholdId = householdId,
            PromisorPersonId = 2,
            PromiseePersonId = null,
            CommitmentText = "Fence check: a commitment for its maker only.",
            SourceEventId = fenceCommitment.Id,
            State = PromiseState.Made
        });
        await db.SaveChangesAsync();

        async Task<HouseholdCoordinator.Pages.PromisesModel> PromisesAs(int? viewerId)
        {
            var page = new HouseholdCoordinator.Pages.PromisesModel(db, confirmations) { ViewerId = viewerId };
            await page.OnGetAsync();
            return page;
        }
        var promisesMaker = await PromisesAs(2);
        var promisesOther = await PromisesAs(3);
        var promisesNone = await PromisesAs(null);
        bool PromiseShown(HouseholdCoordinator.Pages.PromisesModel page, string text)
            => page.Promises.Any(p => p.CommitmentText.Contains(text));
        Check("Monolith fence: /Promises shows a maker-only commitment to its maker",
            PromiseShown(promisesMaker, "a commitment for its maker only"));
        Check("Monolith fence: /Promises hides a maker-only commitment from another reader and from no reader",
            !PromiseShown(promisesOther, "a commitment for its maker only")
            && !PromiseShown(promisesNone, "a commitment for its maker only"));

        var fenceNote = new Event
        {
            HouseholdId = householdId,
            Type = EventType.HouseholdNote,
            Body = "Fence check: a personal undecided note.",
            SubjectPersonId = 1,
            AuthorPersonId = 1,
            CreatedAtUtc = DateTime.UtcNow,
            ConfirmationState = ConfirmationState.Confirmed,
            SensitivityClass = SensitivityClass.Personal,
            AudienceRule = """{"Class":"Personal"}""",
            Details = """{"DecisionTopic":"dinner","DecisionState":"Undecided"}"""
        };
        await confirmations.WriteStatementAsync(fenceNote, "tap");
        var fenceTelling = new Event
        {
            HouseholdId = householdId,
            Type = EventType.HouseholdNote,
            Body = "Fence check: a personal usual telling.",
            SubjectPersonId = 1,
            AuthorPersonId = 1,
            CreatedAtUtc = DateTime.UtcNow,
            ConfirmationState = ConfirmationState.Confirmed,
            SensitivityClass = SensitivityClass.Personal,
            AudienceRule = """{"Class":"Personal"}""",
            Details = """{"MealProvenance":"UsualMeal","ItemName":"fence stew"}"""
        };
        await confirmations.WriteStatementAsync(fenceTelling, "tap");
        var fencePlan = await plans.CreatePlanAsync(householdId, "Fence check plan", null);
        var fenceElement = await plans.AddElementAsync(fencePlan.Id, PlanElementKind.MealProposal,
            "Fence check: an element from a personal telling.", new[] { 1, 2 },
            sourceEventIds: new[] { fenceTelling.Id });

        async Task<HouseholdCoordinator.Pages.PlansModel> FencePlansAs(int? viewerId)
        {
            var page = new HouseholdCoordinator.Pages.PlansModel(db, plans) { ViewerId = viewerId };
            await page.OnGetAsync();
            return page;
        }
        var fencePlansAuthor = await FencePlansAs(1);
        var fencePlansOther = await FencePlansAs(2);
        var fencePlansNone = await FencePlansAs(null);
        Check("Monolith fence: /Plans shows a personal undecided note as an open decision to its author only",
            fencePlansAuthor.OpenDecisions.Any(e => e.Body.Contains("a personal undecided note"))
            && !fencePlansOther.OpenDecisions.Any(e => e.Body.Contains("a personal undecided note"))
            && !fencePlansNone.OpenDecisions.Any(e => e.Body.Contains("a personal undecided note")));
        Check("Monolith fence: /Plans shows an element built from a personal telling to that telling's audience only",
            fencePlansAuthor.ElementVisibleToViewer(fenceElement)
            && !fencePlansOther.ElementVisibleToViewer(fenceElement)
            && !fencePlansNone.ElementVisibleToViewer(fenceElement));
        Check("Monolith fence: /Plans lists a personal provenance telling only for a reader inside its audience",
            fencePlansAuthor.ProvenanceItems.Any(i => i.SourceEventId == fenceTelling.Id)
            && !fencePlansOther.ProvenanceItems.Any(i => i.SourceEventId == fenceTelling.Id)
            && !fencePlansNone.ProvenanceItems.Any(i => i.SourceEventId == fenceTelling.Id));

        // The dinner form's posted source is rechecked against the
        // reader the form carries: another reader naming the
        // personal telling is refused whole; its author is not.
        HouseholdCoordinator.Pages.PlansModel FencePage()
            => new(db, plans)
            {
                TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(
                    new Microsoft.AspNetCore.Http.DefaultHttpContext(),
                    new PassthroughTempDataProvider())
            };
        var elementsBeforeRefusal = await db.PlanElements.CountAsync(e => e.PlanId == fencePlan.Id);
        await FencePage().OnPostAddDinnerAsync(fencePlan.Id, fenceTelling.Id, "fence stew", new[] { 1, 2 }, viewerId: 2);
        Check("Monolith fence: the dinner form refuses a posted source the named reader may not see",
            await db.PlanElements.CountAsync(e => e.PlanId == fencePlan.Id) == elementsBeforeRefusal);
        await FencePage().OnPostAddDinnerAsync(fencePlan.Id, fenceTelling.Id, "fence stew", new[] { 1, 2 }, viewerId: 1);
        Check("Monolith fence: the dinner form accepts the same source for a reader inside its audience",
            await db.PlanElements.CountAsync(e => e.PlanId == fencePlan.Id) == elementsBeforeRefusal + 1);

        Console.WriteLine();
        Console.WriteLine($"Demo suite: {_passed} passed, {_failed} failed.");
        foreach (var failure in _failures)
        {
            Console.WriteLine($"  FAILED: {failure}");
        }
        try { File.Delete(dbPath); } catch (IOException) { }
        return _failed == 0 ? 0 : 1;
    }

    // Lets a suite check construct a Razor page model outside the
    // web host: the page's TempData writes go nowhere, which is all
    // a handler check needs.
    private sealed class PassthroughTempDataProvider : Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(Microsoft.AspNetCore.Http.HttpContext context)
            => new Dictionary<string, object>();

        public void SaveTempData(Microsoft.AspNetCore.Http.HttpContext context, IDictionary<string, object> values)
        {
        }
    }

    private static void Check(string name, bool condition)
    {
        if (condition)
        {
            _passed++;
            Console.WriteLine($"  PASS  {name}");
        }
        else
        {
            _failed++;
            _failures.Add(name);
            Console.WriteLine($"  FAIL  {name}");
        }
    }

    private static string? GetStr(Event ev, string key) =>
        ExtractionDetailsReader.GetString(ev.Details, key);

    private static int? GetInt(Event ev, string key) =>
        ExtractionDetailsReader.GetInt(ev.Details, key);

    private static int SeedSlice(AppDbContext db)
    {
        var household = new Household
        {
            Name = "The Ellis Household",
            TimeZoneId = "America/Chicago",
            HealthMentionLevelKey = "now"
        };
        db.Households.Add(household);
        db.SaveChanges();

        var margaret = new Person { Id = 1, HouseholdId = household.Id, DisplayName = "Margaret Ellis", Role = PersonRole.Family };
        var daniel = new Person { Id = 2, HouseholdId = household.Id, DisplayName = "Daniel Ellis", Role = PersonRole.Family };
        var lisa = new Person { Id = 3, HouseholdId = household.Id, DisplayName = "Lisa Ellis", Role = PersonRole.Family };
        var timmy = new Person { Id = 4, HouseholdId = household.Id, DisplayName = "Timmy", Role = PersonRole.Family, MinorFlag = true };
        var mia = new Person { Id = 5, HouseholdId = household.Id, DisplayName = "Mia", Role = PersonRole.Family, MinorFlag = true };
        var june = new Person { Id = 6, HouseholdId = household.Id, DisplayName = "June Alvarez", Role = PersonRole.Helper };
        db.Persons.AddRange(margaret, daniel, lisa, timmy, mia, june);
        db.SaveChanges();

        household.DefaultRoutePersonId = 2;
        household.BackupRoutePersonId = 3;
        db.SaveChanges();

        db.AlertLevels.AddRange(
            new AlertLevel { HouseholdId = household.Id, LevelKey = "now", Label = "Tell them now", RouteToPersonId = 2, BackupPersonId = 3 },
            new AlertLevel { HouseholdId = household.Id, LevelKey = "brief", Label = "Put it in the morning brief" },
            new AlertLevel { HouseholdId = household.Id, LevelKey = "here", Label = "Just keep it here" });
        db.HouseholdSchedules.AddRange(
            new HouseholdSchedule { Id = 100, HouseholdId = household.Id, Label = "Morning ones", SetByPersonId = 2, TimeLocal = "08:00" },
            new HouseholdSchedule { Id = 101, HouseholdId = household.Id, Label = "Evening ones", SetByPersonId = 2, TimeLocal = "19:00" });
        db.GlossaryTerms.Add(new GlossaryTerm
        {
            HouseholdId = household.Id,
            Term = "my program",
            Meaning = "the afternoon show Margaret watches at four"
        });
        db.SupplyItems.AddRange(
            new SupplyItem { Id = 10, HouseholdId = household.Id, Name = "Wipes", Unit = "wipe", QuantityOnHand = 12m, SeededDailyUsageRate = 5m, ReorderLeadDays = 2, AssignedPersonId = 2, Aliases = """["wipes","the wipes"]""" },
            new SupplyItem { Id = 11, HouseholdId = household.Id, Name = "Gauze", Unit = "dressing", QuantityOnHand = 1m, SeededDailyUsageRate = 1m, ReorderLeadDays = 2, AssignedPersonId = 2, Aliases = """["gauze"]""" },
            new SupplyItem { Id = 12, HouseholdId = household.Id, Name = "Pads", Unit = "pad", QuantityOnHand = 18m, SeededDailyUsageRate = 4m, ReorderLeadDays = 3, AssignedPersonId = 3, Aliases = """["pads","the big pads"]""" },
            new SupplyItem { Id = 13, HouseholdId = household.Id, Name = "Heart pills (morning)", Unit = "pill", QuantityOnHand = 7m, SeededDailyUsageRate = 1m, ReorderLeadDays = 7, AssignedPersonId = 3, Aliases = """["heart pills"]""" },
            new SupplyItem { Id = 14, HouseholdId = household.Id, Name = "Evening pills (bottle)", Unit = "bottle", QuantityOnHand = 9m, SeededDailyUsageRate = 0m, ReorderLeadDays = 7, AssignedPersonId = 1, Aliases = """["evening ones","evening pills"]""" });
        db.SaveChanges();
        return household.Id;
    }
}
