using System.Text.Json;
using HomeNode.Data;
using HomeNode.Services.Extraction;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HomeNode.Services;

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
// front door and timeline page models, the briefs per person) and
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
        var keyDir = Path.Combine(Path.GetTempPath(), $"tellhouse-demo-keys-{Guid.NewGuid():N}");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));
        services.AddSingleton(new NodeKeyStore(keyDir));
        services.AddScoped<ConfirmationService>();
        services.AddScoped<AudienceDecisionService>();
        services.AddSingleton<IExtractionClient>(new MockExtractionClient());
        services.AddScoped<ExtractionContextBuilder>();
        services.AddScoped<ExtractionService>();
        services.AddScoped<PlanAssemblyService>();
        services.AddScoped<RunnerTaskService>();
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
        var audience = sp.GetRequiredService<AudienceDecisionService>();

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

        // The open prompt is set by hand here because no surface in
        // this build offers one: the JournalPrompt table is dormant
        // and journal prompts are not a feature of this build. This
        // check pins only how an answer is recorded if a prompt were
        // open; it does not show a prompt being offered.
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
        Check("A Promise is born with the Commitment, due 3:00 PM resolved", commit.PromisesBorn.Count == 1
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
        Check("Promise closes only by a person's linked closing telling",
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
        var plansPage = new HomeNode.Pages.PlansModel(db, plans)
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

        // The runner rule. The ramble's pickup task (author
        // Lisa, subject Timmy) is stored Named to author and
        // subject, so Daniel cannot see the telling; but the plan
        // names him runner, and a task's named runner sees the
        // task's own basic details in his brief and can complete
        // it, without the telling ever opening to him. The stored
        // audience is checked unchanged after every step.
        var runnerTasks = sp.GetRequiredService<RunnerTaskService>();
        async Task<bool> ThrowsQ38(Func<Task> act)
        {
            try { await act(); return false; }
            catch (InvalidOperationException) { return true; }
        }
        var runnerPickup = ramble.WrittenEvents[0];
        var runnerErrand = ramble.WrittenEvents[1];
        var runnerPickupRule = runnerPickup.AudienceRule;
        var runnerPickupClass = runnerPickup.SensitivityClass;
        var runnerPlan = await plans.CreatePlanAsync(householdId, "Runner rule: the afternoon",
            new DateTime(2026, 10, 27, 17, 30, 0));
        await plans.AddElementAsync(runnerPlan.Id, PlanElementKind.Pickup,
            "Pick Timmy up from soccer at five thirty", Array.Empty<int>(),
            sourceEventIds: new[] { runnerPickup.Id });
        await plans.AddElementAsync(runnerPlan.Id, PlanElementKind.Errand,
            "Groceries on the way back", Array.Empty<int>(),
            sourceEventIds: new[] { runnerErrand.Id });

        Check("Runner rule: a third party cannot bind the runner",
            await ThrowsQ38(() => plans.DesignateRunnerAsync(runnerPlan.Id, 2, 1)));
        Check("Runner rule: the runner cannot bind themselves",
            await ThrowsQ38(() => plans.DesignateRunnerAsync(runnerPlan.Id, 2, 2)));
        Check("Runner rule: the author cannot bind themselves as runner either",
            await ThrowsQ38(() => plans.DesignateRunnerAsync(runnerPlan.Id, 3, 3)));
        await plans.DesignateRunnerAsync(runnerPlan.Id, 2, 3);
        Check("Runner rule: the tasks' author binds the runner at plan confirmation",
            (await db.Plans.FirstAsync(p => p.Id == runnerPlan.Id)).RunnerPersonId == 2);
        Check("Runner rule: only the bound runner can acknowledge the plan",
            await ThrowsQ38(() => plans.AcknowledgeAsync(runnerPlan.Id, 3)));
        await plans.AcknowledgeAsync(runnerPlan.Id, 2);
        Check("Runner rule: the audit records the runner delivery for each task in the plan",
            await db.EventAudits.CountAsync(a => a.ObjectKind == "Event"
                && a.ToState == "RunnerDelivery" && a.ActorPersonId == 2 && a.Channel == "brief"
                && (a.EventId == runnerPickup.Id || a.EventId == runnerErrand.Id)) == 2);

        var runnerDanielBrief = await briefs.BuildBriefAsync(2, today);
        Check("Runner rule: the runner's brief carries the pickup's basic details (what, when, where, subject, author)",
            runnerDanielBrief.Any(l => l.Section == "Tasks to run"
                && l.Text.Contains("Timmy") && l.Text.Contains("five thirty")
                && l.Text.Contains("soccer") && l.Text.Contains("Lisa")));
        Check("Runner rule: the runner's brief never carries the telling itself",
            !runnerDanielBrief.Any(l => l.Text.Contains(runnerPickup.Body)));
        Check("Runner rule: the telling stays fenced from the runner on the record surfaces",
            !BriefService.IsVisibleTo(runnerPickup, 2));
        Check("Runner rule: the stored audience is exactly what it was before the binding",
            runnerPickup.AudienceRule == runnerPickupRule && runnerPickup.SensitivityClass == runnerPickupClass);
        var runnerLisaBrief = await briefs.BuildBriefAsync(3, today);
        Check("Runner rule: a person who is not the runner gets no runner projection",
            !runnerLisaBrief.Any(l => l.Section == "Tasks to run"));

        // The fenced question path with the projection live: the
        // runner's own questions still meet the stored audience,
        // so the projection is not a side channel into the record.
        var runnerDanielInput = Input("Where's Margaret?", 2, "21:30");
        await svc.ProcessAsync(runnerDanielInput);
        Check("Runner rule: with the projection live, the runner's question path holds none of the fenced telling",
            runnerDanielInput.Context.RecentEvents.All(e => BriefService.IsVisibleTo(e, 2))
            && !runnerDanielInput.Context.RecentEvents.Any(e => e.Body == runnerPickup.Body));

        Check("Runner rule: a person inside the audience who is not the runner cannot complete the task",
            await ThrowsQ38(() => runnerTasks.CompleteTaskAsync(runnerPickup.Id, 3)));
        Check("Runner rule: a person outside the audience who is not the runner cannot complete it either",
            await ThrowsQ38(() => runnerTasks.CompleteTaskAsync(runnerPickup.Id, 1)));
        await runnerTasks.CompleteTaskAsync(runnerPickup.Id, 2);
        var runnerPickupAfter = await db.Events.FirstAsync(e => e.Id == runnerPickup.Id);
        Check("Runner rule: the runner completes the task, and the audit names the runner as the actor",
            runnerPickupAfter.ConfirmationState == ConfirmationState.Done
            && await db.EventAudits.CountAsync(a => a.ObjectKind == "Event" && a.EventId == runnerPickup.Id
                && a.ToState == nameof(ConfirmationState.Done) && a.ActorPersonId == 2
                && a.Channel == "runner") == 1);
        Check("Runner rule: a completed task cannot be completed again",
            await ThrowsQ38(() => runnerTasks.CompleteTaskAsync(runnerPickup.Id, 2)));
        var runnerDanielBriefAfter = await briefs.BuildBriefAsync(2, today);
        Check("Runner rule: a completed task drops out of the runner's brief; the open one stays",
            !runnerDanielBriefAfter.Any(l => l.Section == "Tasks to run" && l.Text.Contains("Timmy"))
            && runnerDanielBriefAfter.Any(l => l.Section == "Tasks to run" && l.Text.Contains("groceries")));

        // Reassignment, by the author alone: the view and the
        // completion move to the new runner, and the audit records
        // the new runner's delivery.
        await plans.DesignateRunnerAsync(runnerPlan.Id, 6, 3);
        var runnerDanielBriefMoved = await briefs.BuildBriefAsync(2, today);
        var runnerJuneBrief = await briefs.BuildBriefAsync(6, today);
        Check("Runner rule: reassignment moves the view (the former runner loses it, the new runner gains it)",
            !runnerDanielBriefMoved.Any(l => l.Section == "Tasks to run")
            && runnerJuneBrief.Any(l => l.Section == "Tasks to run"
                && l.Text.Contains("groceries") && l.Text.Contains("on the way back")
                && l.Text.Contains("Lisa")));
        Check("Runner rule: the audit records the new runner's delivery on reassignment",
            await db.EventAudits.CountAsync(a => a.ObjectKind == "Event" && a.EventId == runnerErrand.Id
                && a.ToState == "RunnerDelivery" && a.ActorPersonId == 6) == 1);
        Check("Runner rule: after reassignment the former runner cannot complete the task",
            await ThrowsQ38(() => runnerTasks.CompleteTaskAsync(runnerErrand.Id, 2)));
        await runnerTasks.CompleteTaskAsync(runnerErrand.Id, 6);
        Check("Runner rule: after reassignment the new runner can complete it",
            (await db.Events.FirstAsync(e => e.Id == runnerErrand.Id)).ConfirmationState == ConfirmationState.Done);

        // A task whose underlying telling is Restricted: the
        // runner sees its basic details and can run it, and the
        // telling stays exactly as fenced as the ankle mention.
        var runnerRestrictedTask = new Event
        {
            HouseholdId = householdId,
            Type = EventType.Task,
            Body = "Margaret's private words about the folder that Daniel must never read.",
            SubjectPersonId = 1,
            AuthorPersonId = 1,
            CreatedAtUtc = DateTime.UtcNow,
            Details = """{"TaskKind":"General","TaskText":"bring the blue folder","WhenText":"tonight"}""",
            SensitivityClass = SensitivityClass.Restricted,
            AudienceRule = """{"Class":"Restricted","RoutePersonId":3,"BackupPersonId":2}"""
        };
        await confirmations.WriteStatementAsync(runnerRestrictedTask, "tap");
        var runnerPrivatePlan = await plans.CreatePlanAsync(householdId, "Runner rule: a private errand", null);
        await plans.AddElementAsync(runnerPrivatePlan.Id, PlanElementKind.Task,
            "Bring the blue folder tonight", Array.Empty<int>(),
            sourceEventIds: new[] { runnerRestrictedTask.Id });
        await plans.DesignateRunnerAsync(runnerPrivatePlan.Id, 2, 1);
        await plans.AcknowledgeAsync(runnerPrivatePlan.Id, 2);
        var runnerDanielBriefPrivate = await briefs.BuildBriefAsync(2, today);
        Check("Runner rule: a Restricted task projects its basic details to its runner",
            runnerDanielBriefPrivate.Any(l => l.Section == "Tasks to run"
                && l.Text.Contains("bring the blue folder") && l.Text.Contains("tonight")
                && l.Text.Contains("Margaret")));
        Check("Runner rule: the Restricted telling itself stays fenced from its runner, body and audience alike",
            !runnerDanielBriefPrivate.Any(l => l.Text.Contains(runnerRestrictedTask.Body))
            && !BriefService.IsVisibleTo(runnerRestrictedTask, 2)
            && runnerRestrictedTask.AudienceRule == """{"Class":"Restricted","RoutePersonId":3,"BackupPersonId":2}""");
        await runnerTasks.CompleteTaskAsync(runnerRestrictedTask.Id, 2);
        Check("Runner rule: the runner completes the Restricted task without ever opening its telling",
            (await db.Events.FirstAsync(e => e.Id == runnerRestrictedTask.Id)).ConfirmationState == ConfirmationState.Done);

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

        async Task<HomeNode.Pages.TimelineModel> TimelineAs(int actorId)
        {
            var page = new HomeNode.Pages.TimelineModel(db, confirmations) { ActorId = actorId };
            await page.OnGetAsync();
            return page;
        }
        var timelineMargaret = await TimelineAs(1);
        var timelineDaniel = await TimelineAs(2);
        var timelineLisa = await TimelineAs(3);
        var timelineTimmy = await TimelineAs(4);
        bool TimelineHas(HomeNode.Pages.TimelineModel page, string text)
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

        async Task<HomeNode.Pages.FrontDoorModel> FrontDoorAs(int? viewerId)
        {
            var page = new HomeNode.Pages.FrontDoorModel(db, builder, svc, confirmations, audience) { ViewerId = viewerId };
            await page.OnGetAsync();
            return page;
        }
        var frontDoorOpen = await FrontDoorAs(null);
        var frontDoorDaniel = await FrontDoorAs(2);
        var frontDoorTimmy = await FrontDoorAs(4);
        bool FrontDoorHas(HomeNode.Pages.FrontDoorModel page, string text)
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

        // Audience defaults, exercised end to end on this same
        // slice under the audience decision rules (clauses 1 to 10):
        // a seeded holder, a default set by telling with
        // read-back on the house's own pages (the front door page handlers),
        // and an Ask holding an event at its narrowest audience.
        // Visibility below is always BriefService.IsVisibleTo against
        // the stored rule, the read the record surfaces perform.
        bool Vis(Event ev, int personId) => BriefService.IsVisibleTo(ev, personId);

        Check("Audience defaults: the seeded holder's Private default is in force",
            await audience.GetEffectiveDefaultAsync(householdId, 6) == AudienceDefaultValue.Private
            && await audience.GetDefaultRowAsync(householdId, 6) is { Value: AudienceDefaultValue.Private });

        // Clause 7 rationing: no default set and no stated words
        // raising sharing, so the shape's class decides and the
        // house asks nothing. (Lisa holds no default yet at this
        // point in the block.)
        var plainNote = await svc.ProcessAsync(Input("The soup is on the stove.", 3, "18:00"));
        Check("Audience defaults: with no default set, the shape's class decides and no ask is generated",
            plainNote.WrittenEvents.Count == 1
            && plainNote.WrittenEvents[0].SensitivityClass == SensitivityClass.Household
            && plainNote.AsksCreated.Count == 0);

        // Clause 4 on the house's own pages: Daniel sets an Ask default by
        // telling, through the front door handlers. The read-back
        // comes first; nothing is applied until he disposes of it.
        var doorPage = new HomeNode.Pages.FrontDoorModel(db, builder, svc, confirmations, audience)
        {
            TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(
                new Microsoft.AspNetCore.Http.DefaultHttpContext(),
                new PassthroughTempDataProvider())
        };
        await doorPage.OnPostSetDefaultAsync(2, 2, "Ask");
        var pendingDaniel = await db.AudienceDefaultRequests.OrderBy(r => r.Id).LastAsync(r => r.HolderPersonId == 2);
        Check("Audience defaults: a default set by telling waits on its read-back, unapplied",
            pendingDaniel.State == AudienceDefaultRequestState.PendingReadBack
            && pendingDaniel.ReadBackText != null && pendingDaniel.ReadBackText.Contains("Ask")
            && await audience.GetDefaultRowAsync(householdId, 2) is null);
        await doorPage.OnPostConfirmDefaultAsync(pendingDaniel.Id, 2);
        var appliedDaniel = await db.AudienceDefaultRequests.FirstAsync(r => r.Id == pendingDaniel.Id);
        Check("Audience defaults: the holder's disposal of the read-back applies the default, recorded",
            appliedDaniel.State == AudienceDefaultRequestState.Applied
            && appliedDaniel.DisposedByPersonId == 2
            && await audience.GetEffectiveDefaultAsync(householdId, 2) == AudienceDefaultValue.Ask);
        await doorPage.OnGetAsync();
        Check("Audience defaults: the front door shows the holder's default after the read-back",
            doorPage.Defaults.Any(d => d.Person.Id == 2 && d.Label == "Ask"));

        // Clause 7, the Ask: Daniel's own telling is held at
        // its narrowest audience (Daniel alone) until he answers.
        var askTelling = await svc.ProcessAsync(Input("The porch light is on.", 2, "19:00"));
        var askEv = askTelling.WrittenEvents[0];
        var askRow = askTelling.AsksCreated[0];
        Check("Audience defaults: an Ask default holds the event at its narrowest audience",
            askTelling.AsksCreated.Count == 1
            && askEv.SensitivityClass == SensitivityClass.Personal
            && askEv.AudienceAskPending
            && Vis(askEv, 2) && !Vis(askEv, 3) && !Vis(askEv, 1));
        Check("Audience defaults: the ask is addressed to the speaker for his own telling",
            askRow.AskedPersonId == 2 && askRow.State == AudienceAskState.Pending);
        var lisaAnswerThrew = false;
        try { await audience.AnswerAskAsync(askRow.Id, 3, "Household", null, DateTime.UtcNow); }
        catch (InvalidOperationException) { lisaAnswerThrew = true; }
        Check("Audience defaults: only the person asked may answer the ask", lisaAnswerThrew);
        await audience.PresentAskAsync(askRow.Id, DateTime.UtcNow);
        await audience.AnswerAskAsync(askRow.Id, 2, "Household", null, DateTime.UtcNow);
        var askEvAfter = await db.Events.FirstAsync(e => e.Id == askEv.Id);
        Check("Audience defaults: answering Household restores exactly the baseline audience",
            askEvAfter.SensitivityClass == SensitivityClass.Household && Vis(askEvAfter, 3) && !askEvAfter.AudienceAskPending);

        // Clause 7: silence never widens. A second telling's ask is
        // presented, never answered, and lapses; the event stays at
        // its narrowest audience.
        var silentTelling = await svc.ProcessAsync(Input("The garden gate is shut.", 2, "19:05"));
        var silentEv = silentTelling.WrittenEvents[0];
        var silentAsk = silentTelling.AsksCreated[0];
        var presentedAt = DateTime.UtcNow;
        await audience.PresentAskAsync(silentAsk.Id, presentedAt);
        var lapsedCount = await audience.LapseAsksAsync(presentedAt.AddHours(25));
        var silentAfter = await db.Events.FirstAsync(e => e.Id == silentEv.Id);
        var silentAskAfter = await db.AudienceAsks.FirstAsync(a => a.Id == silentAsk.Id);
        Check("Audience defaults: an unanswered ask lapses and silence never widens",
            lapsedCount >= 1 && silentAskAfter.State == AudienceAskState.Lapsed
            && silentAfter.SensitivityClass == SensitivityClass.Personal
            && !Vis(silentAfter, 3) && !silentAfter.AudienceAskPending);

        // Clause 7 from the subject's side: a telling about Mia, a
        // minor whose effective default is Ask until set, asks Mia,
        // and the event is held to Mia and its author meanwhile.
        var miaTelling = await svc.ProcessAsync(Input("Mia is at the library.", 3, "19:10"));
        var miaEv = miaTelling.WrittenEvents[0];
        Check("Audience defaults: a telling about a person whose default says Ask asks that person, held narrow",
            miaTelling.AsksCreated.Count == 1
            && miaTelling.AsksCreated[0].AskedPersonId == 5
            && Vis(miaEv, 5) && Vis(miaEv, 3) && !Vis(miaEv, 2));
        // Clause 9's clock rule, applied to this ask: it has not
        // been presented to Mia yet, so no lapse sweep, however late,
        // can lapse it. The clock runs from presentation.
        await audience.LapseAsksAsync(DateTime.UtcNow.AddDays(7));
        var miaAskBeforePresentation = await db.AudienceAsks.FirstAsync(a => a.Id == miaTelling.AsksCreated[0].Id);
        Check("Audience defaults: an ask's lapse clock starts at its presentation, not at its writing",
            miaAskBeforePresentation.State == AudienceAskState.Pending);
        await audience.AnswerAskAsync(miaTelling.AsksCreated[0].Id, 5, "Private", null, DateTime.UtcNow);
        var miaAfter = await db.Events.FirstAsync(e => e.Id == miaEv.Id);
        Check("Audience defaults: the subject's own answer keeps the event at its narrowest set",
            !Vis(miaAfter, 2) && Vis(miaAfter, 5) && !miaAfter.AudienceAskPending);

        // Clause 5: where both defaults apply, the narrower governs.
        // Lisa sets Household; Daniel replaces Ask with Private.
        // Lisa's telling about Daniel then resolves by Daniel's
        // Private, not by Lisa's Household.
        var lisaReq = await audience.ProposeByTellingAsync(householdId, 3, 3, AudienceDefaultValue.Household, "Set the audience default to Household.", "tap", DateTime.UtcNow);
        await audience.ConfirmReadBackAsync(lisaReq.Id, 3, DateTime.UtcNow);
        var danielReq = await audience.ProposeByTellingAsync(householdId, 2, 2, AudienceDefaultValue.Private, "Set the audience default to Private.", "tap", DateTime.UtcNow);
        await audience.ConfirmReadBackAsync(danielReq.Id, 2, DateTime.UtcNow);
        var depotTelling = await svc.ProcessAsync(Input("Daniel is at the depot.", 3, "19:15"));
        var depotEv = depotTelling.WrittenEvents[0];
        var depotRuleStored = depotEv.AudienceRule;
        Check("Audience defaults: the subject's Private default governs over the speaker's Household default (narrower wins)",
            depotEv.SensitivityClass == SensitivityClass.Named
            && Vis(depotEv, 2) && Vis(depotEv, 3) && !Vis(depotEv, 1) && !Vis(depotEv, 6));

        // Clause 3, narrow-only about another person: Lisa's stated
        // Household mark cannot widen the pickup task's Named
        // ceiling past Daniel and herself.
        var pickupTelling = await svc.ProcessAsync(Input("Daniel needs picking up from the depot at five. Share this with the household.", 3, "19:20"));
        var pickupEv = pickupTelling.WrittenEvents[0];
        Check("Audience defaults: a stated Household mark about another person cannot widen the Named ceiling",
            pickupEv.Type == EventType.Task
            && pickupEv.SensitivityClass == SensitivityClass.Named
            && Vis(pickupEv, 2) && !Vis(pickupEv, 1));

        // Clause 3: a stated Named mark narrows Lisa's own telling to
        // Daniel and herself.
        var namedTelling = await svc.ProcessAsync(Input("The letter from the school came today, just for Daniel.", 3, "19:25"));
        var namedEv = namedTelling.WrittenEvents[0];
        Check("Audience defaults: a stated Named mark narrows the telling to the named person and the speaker",
            namedEv.SensitivityClass == SensitivityClass.Named
            && Vis(namedEv, 2) && Vis(namedEv, 3) && !Vis(namedEv, 4) && !Vis(namedEv, 1));

        // Clause 3: conflicting stated marks in one telling cancel.
        var conflictTelling = await svc.ProcessAsync(Input("The porch light is on, just for Daniel, share this with the household.", 3, "19:30"));
        var conflictEv = conflictTelling.WrittenEvents[0];
        Check("Audience defaults: conflicting stated marks in one telling cancel and the step yields nothing",
            conflictEv.SensitivityClass == SensitivityClass.Household && Vis(conflictEv, 1));

        // Clause 3's fences at the decision seam itself: a mark
        // naming a person outside the circle, and a mark quoting
        // words that were never said, both yield nothing; a clean
        // mark decides.
        var ctxD = await builder.BuildAsync(householdId);
        ExtractionInput DirectInput() => new()
        {
            UtteranceId = "audience-defaults-direct",
            Transcript = "The porch light is on.",
            SourceType = "tap",
            OccurredAtLocal = DateTimeOffset.Parse("2026-10-27T19:35:00-05:00"),
            Speaker = new ExtractionPersonRef { PersonId = 3, DisplayName = "Lisa Ellis", Role = "Family" },
            Context = ctxD
        };
        ExtractionEvent DirectEvent(ProposedStatedAudience mark) => new()
        {
            Type = "HouseholdNote",
            Body = "The porch light is on.",
            SubjectPersonId = 3,
            AuthorPersonId = 3,
            ConfirmationState = "Confirmed",
            StatedMarks = new List<ProposedStatedAudience> { mark }
        };
        var fencedMember = await audience.DecideAsync(DirectInput(), DirectEvent(
            new ProposedStatedAudience { Kind = "Named", PersonIds = new List<int> { 999 }, Clause = "The porch light is on." }));
        var fencedQuote = await audience.DecideAsync(DirectInput(), DirectEvent(
            new ProposedStatedAudience { Kind = "Named", PersonIds = new List<int> { 2 }, Clause = "words nobody said" }));
        var cleanMark = await audience.DecideAsync(DirectInput(), DirectEvent(
            new ProposedStatedAudience { Kind = "Named", PersonIds = new List<int> { 2 }, Clause = "The porch light is on." }));
        Check("Audience defaults: a stated mark naming a non-member or quoting words never said yields nothing",
            fencedMember.Class == SensitivityClass.Household && fencedQuote.Class == SensitivityClass.Household);
        Check("Audience defaults: a clean stated mark decides, as a proposal fenced by deterministic validation",
            cleanMark.Class == SensitivityClass.Named
            && cleanMark.FinalAudienceIds.SequenceEqual(new[] { 2, 3 }));

        // Clause 6: a Household default never lifts a journal entry
        // out of Personal. Margaret sets Household, then journals.
        var margaretReq = await audience.ProposeByTellingAsync(householdId, 1, 1, AudienceDefaultValue.Household, "Set the audience default to Household.", "tap", DateTime.UtcNow);
        await audience.ConfirmReadBackAsync(margaretReq.Id, 1, DateTime.UtcNow);
        var journalFree = await svc.ProcessAsync(Input("For my journal: the garden was quiet today.", 1, "20:00"));
        Check("Audience defaults: a Household default does not lift a journal entry out of Personal",
            journalFree.WrittenEvents.Count == 1
            && journalFree.WrittenEvents[0].SensitivityClass == SensitivityClass.Personal
            && Vis(journalFree.WrittenEvents[0], 1) && !Vis(journalFree.WrittenEvents[0], 2));

        // Clause 7 rationing again: Margaret's Ask default generates
        // no ask about a journal entry, already at its narrowest.
        var margaretAskReq = await audience.ProposeByTellingAsync(householdId, 1, 1, AudienceDefaultValue.Ask, "Set the audience default to Ask.", "tap", DateTime.UtcNow);
        await audience.ConfirmReadBackAsync(margaretAskReq.Id, 1, DateTime.UtcNow);
        var ctxJ = await builder.BuildAsync(householdId);
        // Hand-set open prompt, as at Ex6 above (no surface offers one).
        ctxJ.OpenPrompt = new ExtractionOpenPrompt { Kind = "Journal", PromptId = "jp-03", Text = "What are you thankful for today?" };
        var journalPrompted = await svc.ProcessAsync(Input("I am thankful for the quiet house.", 1, "20:05", ctxJ));
        Check("Audience defaults: no ask is generated about material already resolved to its narrowest state",
            journalPrompted.WrittenEvents.Count == 1
            && journalPrompted.AsksCreated.Count == 0
            && journalPrompted.WrittenEvents[0].SensitivityClass == SensitivityClass.Personal);

        // Clause 6, the health mention exclusion, under that same
        // Ask default: the mention takes no default and no ask; its
        // audience is its class rule and its route.
        var healthTelling = await svc.ProcessAsync(Input("My knee hurts today.", 1, "20:10"));
        Check("Audience defaults: a default never applies to a health mention (Ask holder, still Restricted by its class rule)",
            healthTelling.WrittenEvents.Count == 1
            && healthTelling.WrittenEvents[0].SensitivityClass == SensitivityClass.Restricted
            && (healthTelling.WrittenEvents[0].AudienceRule ?? "").Contains("\"RoutePersonId\":2")
            && healthTelling.AsksCreated.Count == 0);

        // Clause 8, minors: Timmy holds no row, so he is Ask until
        // set; Daniel narrows him to Private at once; Daniel's later
        // proposal to widen waits until Timmy's own act applies it.
        Check("Audience defaults: a minor with no default set holds Ask until set",
            await audience.GetDefaultRowAsync(householdId, 4) is null
            && await audience.GetEffectiveDefaultAsync(householdId, 4) == AudienceDefaultValue.Ask);
        await audience.NarrowMinorDefaultAsync(householdId, 2, 4, AudienceDefaultValue.Private, DateTime.UtcNow);
        Check("Audience defaults: a circle adult may narrow a minor's default at any time",
            await audience.GetEffectiveDefaultAsync(householdId, 4) == AudienceDefaultValue.Private);
        var widenProposal = await audience.ProposeByTellingAsync(householdId, 4, 2, AudienceDefaultValue.Household, "Set the audience default to Household.", "tap", DateTime.UtcNow);
        Check("Audience defaults: an adult's proposal to widen a minor's default is recorded and waits, unapplied",
            widenProposal.State == AudienceDefaultRequestState.ProposalWaiting
            && await audience.GetEffectiveDefaultAsync(householdId, 4) == AudienceDefaultValue.Private);
        await audience.AcceptProposalAsync(widenProposal.Id, 4, DateTime.UtcNow);
        Check("Audience defaults: only the minor may widen their own default, by their own act",
            await audience.GetEffectiveDefaultAsync(householdId, 4) == AudienceDefaultValue.Household);

        // Clause 4, holder-only, for an adult: Lisa's instruction
        // about Daniel's default is a proposal and changes nothing.
        var outsiderProposal = await audience.ProposeByTellingAsync(householdId, 2, 3, AudienceDefaultValue.Household, "Set the audience default to Household.", "tap", DateTime.UtcNow);
        Check("Audience defaults: anyone else's instruction about a person's default is a proposal, never applied",
            outsiderProposal.State == AudienceDefaultRequestState.ProposalWaiting
            && await audience.GetEffectiveDefaultAsync(householdId, 2) == AudienceDefaultValue.Private);

        // Clause 9: a person-free telling resolves by the ceiling
        // alone, with defaults in force all around it: no default
        // applies and no ask is generated.
        var personFree = await svc.ProcessAsync(Input("Toilet paper is low.", null, "20:15"));
        Check("Audience defaults: a person-free telling resolves by the ceiling alone, no default, no ask",
            personFree.WrittenEvents.Count == 1
            && personFree.WrittenEvents[0].SensitivityClass == SensitivityClass.Household
            && personFree.WrittenEvents[0].RecordPending
            && personFree.AsksCreated.Count == 0);

        // Clauses 1, 4, and 10: Daniel's default now changes to
        // Household, forward-looking only. The depot entry's stored
        // audience does not move, Margaret's brief still does not
        // carry it, and the whereabouts lookup, a pure read of the
        // stored record with no model in its path, answers Lisa
        // (named in the stored audience) and not Margaret.
        var danielHouseReq = await audience.ProposeByTellingAsync(householdId, 2, 2, AudienceDefaultValue.Household, "Set the audience default to Household.", "tap", DateTime.UtcNow);
        await audience.ConfirmReadBackAsync(danielHouseReq.Id, 2, DateTime.UtcNow);
        var depotAfter = await db.Events.FirstAsync(e => e.Id == depotEv.Id);
        var margaretBriefD = await briefs.BuildBriefAsync(1, today);
        Check("Audience defaults: a default change is forward-looking only; a stored audience is never recomputed",
            depotAfter.AudienceRule == depotRuleStored
            && !Vis(depotAfter, 1)
            && !margaretBriefD.Any(l => l.Text.Contains("Daniel is at the depot.")));
        var depotForLisa = await lookup.AnswerWhereaboutsAsync(3, 2);
        var depotForMargaret = await lookup.AnswerWhereaboutsAsync(1, 2);
        Check("Audience defaults: at read time the stored audience decides, with no model in the path",
            depotForLisa.Contains("at the depot")
            && !depotForMargaret.Contains("at the depot")
            && depotForMargaret.Contains("Domino's"));

        // Promise closing discipline:
        // every closing is the closer's own words on the record, and
        // Released is set by the promisee's statement only, except a
        // promise to the house, which its maker may release. The
        // Promises page handler and the confirmation service are
        // exercised directly, the way the Plans page checks above
        // exercise that page. These checks run last: they add Made
        // and closed promises to the record, which no check above
        // this point may see.
        async Task<Promise> MakePromise(int promisorId, int? promiseeId, string commitment)
        {
            var source = new Event
            {
                HouseholdId = householdId,
                Type = EventType.HouseholdNote,
                Body = commitment,
                SubjectPersonId = promisorId,
                AuthorPersonId = promisorId,
                CreatedAtUtc = DateTime.UtcNow,
                SensitivityClass = SensitivityClass.Household,
                AudienceRule = """{"Class":"Household"}"""
            };
            await confirmations.WriteStatementAsync(source, "tap");
            var promise = new Promise
            {
                HouseholdId = householdId,
                PromisorPersonId = promisorId,
                PromiseePersonId = promiseeId,
                CommitmentText = commitment,
                SourceEventId = source.Id,
                State = PromiseState.Made
            };
            db.Promises.Add(promise);
            await db.SaveChangesAsync();
            return promise;
        }
        async Task<Event> ClosingWords(int closerId, int promisorId, string words)
        {
            var ev = new Event
            {
                HouseholdId = householdId,
                Type = EventType.HouseholdNote,
                Body = words,
                SubjectPersonId = promisorId,
                AuthorPersonId = closerId,
                CreatedAtUtc = DateTime.UtcNow,
                SensitivityClass = SensitivityClass.Household,
                AudienceRule = """{"Class":"Household"}"""
            };
            await confirmations.WriteStatementAsync(ev, "tap");
            return ev;
        }
        async Task<bool> CloseThrew(Promise promise, Event closing, int closerId, string outcome)
        {
            try
            {
                await confirmations.ClosePromiseAsync(promise.Id, closing.Id, closerId, outcome, "tap");
                return false;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }

        var promisesPage = new HomeNode.Pages.PromisesModel(db, confirmations)
        {
            TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(
                new Microsoft.AspNetCore.Http.DefaultHttpContext(),
                new PassthroughTempDataProvider())
        };

        var pagePromise = await MakePromise(2, 3, "Daniel will water the plants on Friday.");
        var eventsBeforeEmpty = await db.Events.CountAsync();
        await promisesPage.OnPostCloseAsync(pagePromise.Id, 2, "Kept", "   ");
        var pagePromiseAfterEmpty = await db.Promises.FirstAsync(p => p.Id == pagePromise.Id);
        Check("Promise page: a close with no closing words writes no event and leaves the promise open",
            await db.Events.CountAsync() == eventsBeforeEmpty
            && pagePromiseAfterEmpty.State == PromiseState.Made
            && pagePromiseAfterEmpty.ClosedByEventId is null);
        Check("Promise page: a close with no closing words says the words are needed",
            promisesPage.Note == "A promise closes in the closer's own words, so the words are needed.");
        await promisesPage.OnPostCloseAsync(pagePromise.Id, 2, "Kept", "The plants are watered, like I said.");
        var pagePromiseAfterWords = await db.Promises.FirstAsync(p => p.Id == pagePromise.Id);
        var pageClosingEvent = await db.Events.FirstAsync(e => e.Id == pagePromiseAfterWords.ClosedByEventId);
        Check("Promise page: a close with words closes the promise and the closing event body is exactly the closer's words",
            pagePromiseAfterWords.State == PromiseState.Kept
            && pagePromiseAfterWords.ClosedByPersonId == 2
            && pageClosingEvent.Body == "The plants are watered, like I said.");

        var pageReleasePromise = await MakePromise(2, 3, "Daniel will bring the ladder on Saturday.");
        var eventsBeforeRefused = await db.Events.CountAsync();
        await promisesPage.OnPostCloseAsync(pageReleasePromise.Id, 1, "Released", "Letting this one go.");
        var pageReleaseAfter = await db.Promises.FirstAsync(p => p.Id == pageReleasePromise.Id);
        Check("Promise page: a release by anyone but the promisee is refused with a plain note, no event written, promise still open",
            await db.Events.CountAsync() == eventsBeforeRefused
            && pageReleaseAfter.State == PromiseState.Made
            && promisesPage.Note == ConfirmationService.ReleasedOnlyByPromiseeMessage);

        var namedPromise = await MakePromise(2, 3, "Daniel will fix the gate on Sunday.");
        var strangerClosing = await ClosingWords(1, 2, "Margaret says let the gate go.");
        var strangerThrew = await CloseThrew(namedPromise, strangerClosing, 1, nameof(PromiseState.Released));
        var namedAfterRefusal = await db.Promises.FirstAsync(p => p.Id == namedPromise.Id);
        Check("Promise service: Released by a person who is not the promisee is refused and the promise stays open",
            strangerThrew
            && namedAfterRefusal.State == PromiseState.Made
            && namedAfterRefusal.ClosedByEventId is null);
        var promiseeClosing = await ClosingWords(3, 2, "Do not worry about the gate, take your time.");
        await confirmations.ClosePromiseAsync(namedPromise.Id, promiseeClosing.Id, 3, nameof(PromiseState.Released), "tap");
        var namedAfterRelease = await db.Promises.FirstAsync(p => p.Id == namedPromise.Id);
        Check("Promise service: Released by the promisee closes the promise",
            namedAfterRelease.State == PromiseState.Released
            && namedAfterRelease.ClosedByPersonId == 3
            && namedAfterRelease.ClosedByEventId == promiseeClosing.Id);

        var housePromise = await MakePromise(2, null, "Daniel will sweep the porch on Monday.");
        var thirdClosing = await ClosingWords(3, 2, "Lisa says let the porch go.");
        var thirdThrew = await CloseThrew(housePromise, thirdClosing, 3, nameof(PromiseState.Released));
        var houseAfterRefusal = await db.Promises.FirstAsync(p => p.Id == housePromise.Id);
        Check("Promise service: a house promise released by a third person is refused and stays open",
            thirdThrew && houseAfterRefusal.State == PromiseState.Made);
        var makerClosing = await ClosingWords(2, 2, "I am letting the porch sweeping go.");
        await confirmations.ClosePromiseAsync(housePromise.Id, makerClosing.Id, 2, nameof(PromiseState.Released), "tap");
        var houseAfterRelease = await db.Promises.FirstAsync(p => p.Id == housePromise.Id);
        Check("Promise service: a house promise released by its maker closes the promise",
            houseAfterRelease.State == PromiseState.Released
            && houseAfterRelease.ClosedByPersonId == 2
            && houseAfterRelease.ClosedByEventId == makerClosing.Id);

        var witnessedPromise = await MakePromise(2, 3, "Daniel will be home by six on Tuesday.");
        var witnessClosing = await ClosingWords(1, 2, "Daniel was home by six, I saw him come in.");
        await confirmations.ClosePromiseAsync(witnessedPromise.Id, witnessClosing.Id, 1, nameof(PromiseState.Kept), "tap");
        var witnessedAfter = await db.Promises.FirstAsync(p => p.Id == witnessedPromise.Id);
        Check("Promise service: Kept stated by a witnessing third circle member still closes the promise",
            witnessedAfter.State == PromiseState.Kept
            && witnessedAfter.ClosedByPersonId == 1
            && witnessedAfter.ClosedByEventId == witnessClosing.Id);
        // Audience fence: the question path through the extraction pipeline
        // honors the stored audience. Two Restricted entries in the
        // ankle shape (Margaret is author and subject, Daniel is
        // the route person, Lisa only the backup, and the backup
        // is outside the audience until a delivery hops) are
        // written directly to the record, plus one Personal and
        // one Named marker, and asked about with the two question
        // shapes the slice lookup actually answers: "Where's X?"
        // and "What did X say about Tuesday?". Lisa (3) is outside
        // the Restricted audience; Margaret (1) and Daniel (2) are
        // inside it.
        var fenceRule = """{"Class":"Restricted","RoutePersonId":2,"BackupPersonId":3}""";
        var fenceWhere = new Event
        {
            HouseholdId = householdId,
            Type = EventType.Whereabouts,
            Body = "I'm at the clinic annex for my ankle.",
            SubjectPersonId = 1,
            AuthorPersonId = 1,
            CreatedAtUtc = DateTime.UtcNow,
            SensitivityClass = SensitivityClass.Restricted,
            AudienceRule = fenceRule
        };
        await confirmations.WriteStatementAsync(fenceWhere, "tap");
        var fenceNote = new Event
        {
            HouseholdId = householdId,
            Type = EventType.HouseholdNote,
            Body = "On Tuesday my ankle appointment is at the clinic annex.",
            SubjectPersonId = 1,
            AuthorPersonId = 1,
            CreatedAtUtc = DateTime.UtcNow,
            SensitivityClass = SensitivityClass.Restricted,
            AudienceRule = fenceRule
        };
        await confirmations.WriteStatementAsync(fenceNote, "tap");
        var fencePersonal = new Event
        {
            HouseholdId = householdId,
            Type = EventType.Journal,
            Body = "Audience fence marker: a personal journal line about the garden gate.",
            SubjectPersonId = 1,
            AuthorPersonId = 1,
            CreatedAtUtc = DateTime.UtcNow,
            SensitivityClass = SensitivityClass.Personal,
            AudienceRule = """{"Class":"Personal"}"""
        };
        await confirmations.WriteStatementAsync(fencePersonal, "tap");
        var fenceNamed = new Event
        {
            HouseholdId = householdId,
            Type = EventType.HouseholdNote,
            Body = "Audience fence marker: a named note about the spare key.",
            SubjectPersonId = 3,
            AuthorPersonId = 3,
            CreatedAtUtc = DateTime.UtcNow,
            SensitivityClass = SensitivityClass.Named,
            AudienceRule = """{"Class":"Named","AudiencePersonIds":[2,3]}"""
        };
        await confirmations.WriteStatementAsync(fenceNamed, "tap");

        var fenceSlice = await builder.BuildAsync(householdId);
        var fenceSliceNote = fenceSlice.RecentEvents.FirstOrDefault(e => e.Body == fenceNote.Body);
        Check("Audience fence: the extraction slice carries each recent event's stored sensitivity class and audience rule",
            fenceSliceNote is not null
            && fenceSliceNote.SensitivityClass == "Restricted"
            && (fenceSliceNote.AudienceRule ?? "").Contains("\"RoutePersonId\":2"));

        // The fence at the ExtractionService level: the context on
        // the processed input is the context the client received,
        // so what it holds after ProcessAsync is what the client
        // was handed.
        var fenceLisaInput = Input("Where's Margaret?", 3, "21:00");
        await svc.ProcessAsync(fenceLisaInput);
        Check("Audience fence: the context the client receives for a person outside the audience holds no Restricted event bodies",
            fenceLisaInput.Context.RecentEvents.All(e => BriefService.IsVisibleTo(e, 3))
            && !fenceLisaInput.Context.RecentEvents.Any(e => e.Body.Contains("clinic annex"))
            && !fenceLisaInput.Context.RecentEvents.Any(e => e.Body.Contains("ankle appointment")));
        Check("Audience fence: the fenced slice still carries the events that person may see",
            fenceLisaInput.Context.RecentEvents.Any(e => e.SensitivityClass == "Household"));

        var fenceLisaWhere = await svc.ProcessAsync(Input("Where's Margaret?", 3, "21:05"));
        Check("Audience fence: a person outside the audience asking where Margaret is hears none of the Restricted record",
            fenceLisaWhere.WrittenEvents.Count == 0
            && fenceLisaWhere.HouseReply == SpokenTemplates.DoNotKnow);
        var fenceLisaWhat = await svc.ProcessAsync(Input("What did Margaret say about Tuesday?", 3, "21:06"));
        Check("Audience fence: a person outside the audience asking what Margaret said about Tuesday gets none of the Restricted body text",
            fenceLisaWhat.HouseReply is not null
            && !fenceLisaWhat.HouseReply.Contains("clinic annex")
            && !fenceLisaWhat.HouseReply.Contains("ankle appointment")
            && !fenceLisaWhat.HouseReply.Contains(fenceNote.Body)
            && !fenceLisaWhat.HouseReply.Contains(fenceWhere.Body));

        var fenceDanielWhere = await svc.ProcessAsync(Input("Where's Margaret?", 2, "21:10"));
        Check("Audience fence: the route person inside the audience still gets the Restricted whereabouts from the record",
            fenceDanielWhere.HouseReply is not null
            && fenceDanielWhere.HouseReply.Contains("clinic annex"));
        var fenceDanielWhat = await svc.ProcessAsync(Input("What did Margaret say about Tuesday?", 2, "21:11"));
        var fenceMargaretWhat = await svc.ProcessAsync(Input("What did Margaret say about Tuesday?", 1, "21:12"));
        Check("Audience fence: a person inside the audience (route person and subject) still gets what Margaret said about Tuesday",
            fenceDanielWhat.HouseReply is not null && fenceDanielWhat.HouseReply.Contains("ankle appointment")
            && fenceMargaretWhat.HouseReply is not null && fenceMargaretWhat.HouseReply.Contains("ankle appointment"));

        // A speakerless input: the unfenced slice demonstrably
        // holds Personal, Named, and Restricted events before the
        // pipeline runs, so the fenced result is not vacuous.
        var fenceAnonInput = Input("What did Margaret say about Tuesday?", null, "21:15");
        var fenceAnonHadNarrow = fenceAnonInput.Context.RecentEvents.Any(e => e.SensitivityClass == "Restricted")
            && fenceAnonInput.Context.RecentEvents.Any(e => e.SensitivityClass == "Personal")
            && fenceAnonInput.Context.RecentEvents.Any(e => e.SensitivityClass == "Named");
        var fenceAnon = await svc.ProcessAsync(fenceAnonInput);
        Check("Audience fence: a speakerless input's context holds no Personal, Named, or Restricted event bodies",
            fenceAnonHadNarrow
            && fenceAnonInput.Context.RecentEvents.All(e => e.SensitivityClass is "Open" or "Household")
            && !fenceAnonInput.Context.RecentEvents.Any(e => e.Body.Contains("clinic annex"))
            && !fenceAnonInput.Context.RecentEvents.Any(e => e.Body.Contains("Audience fence marker")));
        Check("Audience fence: a speakerless question is answered only from the shared record",
            fenceAnon.HouseReply is not null
            && !fenceAnon.HouseReply.Contains("clinic annex")
            && !fenceAnon.HouseReply.Contains("ankle appointment"));

        // Dispatch: silence and the raw refusal text. An
        // unwired action confirmed by a person records its failure
        // and stays owed; a wired NotifyPerson is untouched; a
        // decline is not an execution and records no failure. A
        // telling the validator refuses shows the visitor plain
        // words only, while the raw errors go to the server log.
        async Task<Event> ActionProposal(EventActionType actionType, string body, string rule)
        {
            var ev = new Event
            {
                HouseholdId = householdId,
                Type = EventType.Request,
                Body = body,
                SubjectPersonId = 1,
                AuthorPersonId = 1,
                CreatedAtUtc = DateTime.UtcNow,
                SensitivityClass = SensitivityClass.Household,
                AudienceRule = rule
            };
            var action = new EventAction
            {
                ActionType = actionType,
                TargetPersonId = 2,
                CreatedAtUtc = DateTime.UtcNow
            };
            return await confirmations.ProposeAsync(ev, action, "tap");
        }

        var mediaProposal = await ActionProposal(EventActionType.MediaPlay,
            "Dispatch check: put my program on.", """{"Class":"Household"}""");
        await confirmations.ConfirmAsync(mediaProposal.Id, 1, "tap");
        var mediaAfter = await db.Events.FirstAsync(e => e.Id == mediaProposal.Id);
        Check("Dispatch: confirming a MediaPlay proposal leaves it Confirmed, one executor failure row, no delivery",
            mediaAfter.ConfirmationState == ConfirmationState.Confirmed
            && await db.EventAudits.CountAsync(a => a.EventId == mediaProposal.Id
                && a.Channel == "executor"
                && a.FromState == ConfirmationState.Confirmed
                && a.ToState == nameof(ConfirmationState.Confirmed)) == 1
            && await db.AlertDeliveries.CountAsync(d => d.EventId == mediaProposal.Id) == 0);

        var notifyProposal = await ActionProposal(EventActionType.NotifyPerson,
            "Dispatch check: tell Daniel this.",
            """{"Class":"Restricted","RoutePersonId":2,"BackupPersonId":3}""");
        await confirmations.ConfirmAsync(notifyProposal.Id, 1, "tap");
        var notifyAfter = await db.Events.FirstAsync(e => e.Id == notifyProposal.Id);
        Check("Dispatch: confirming a NotifyPerson proposal still delivers exactly once and completes, with no executor failure row",
            notifyAfter.ConfirmationState == ConfirmationState.Done
            && await db.AlertDeliveries.CountAsync(d => d.EventId == notifyProposal.Id) == 1
            && await db.EventAudits.CountAsync(a => a.EventId == notifyProposal.Id
                && a.Channel == "executor"
                && a.FromState == ConfirmationState.Confirmed
                && a.ToState == nameof(ConfirmationState.Confirmed)) == 0);

        var declinedProposal = await ActionProposal(EventActionType.CreateReminder,
            "Dispatch check: a reminder that will be declined.", """{"Class":"Household"}""");
        await confirmations.DeclineAsync(declinedProposal.Id, 1, "tap");
        var declinedAfter = await db.Events.FirstAsync(e => e.Id == declinedProposal.Id);
        Check("Dispatch: declining an unwired action writes no executor failure row",
            declinedAfter.ConfirmationState == ConfirmationState.NotConfirmed
            && await db.EventAudits.CountAsync(a => a.EventId == declinedProposal.Id
                && a.Channel == "executor"
                && a.FromState == ConfirmationState.Confirmed
                && a.ToState == nameof(ConfirmationState.Confirmed)) == 0);

        var eventsBeforeRefusal = await db.Events.CountAsync();
        var refusalLogger = new CapturingFrontDoorLogger();
        var refusingExtraction = new ExtractionService(db, new InvalidExtractionClient(), confirmations, audience);
        var refusingPage = new HomeNode.Pages.FrontDoorModel(db, builder, refusingExtraction, confirmations, audience, new TestSendService(new HttpClient(), Array.Empty<StandInEndpoint>()), refusalLogger)
        {
            TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(
                new Microsoft.AspNetCore.Http.DefaultHttpContext(),
                new PassthroughTempDataProvider())
        };
        await refusingPage.OnPostTellAsync(1, "A telling the house will refuse.");
        var refusalNote = refusingPage.PipelineNote;
        Check("Dispatch: a refused telling shows plain words only, writes nothing, and its raw errors go to the server log",
            refusalNote == "The house could not write that down as stated, so nothing was recorded."
            && await db.Events.CountAsync() == eventsBeforeRefusal
            && refusalLogger.Messages.Any(m => m.Contains("speaker 1") && m.Contains("NotARealType")));

        // Health mention routing: the route person
        // reports his own health mention. Daniel (2) is the default
        // route and Lisa (3) is the backup, per the seed above. The
        // confirmation prompt names Lisa, so the words Daniel
        // confirms must be the words dispatch acts on: the stored
        // rule must name Lisa as its route, and confirming must
        // deliver to Lisa only, never back to Daniel himself.
        var selfReport = await svc.ProcessAsync(Input("My knee hurts today.", 2, "21:40"));
        var selfEvent = selfReport.WrittenEvents.FirstOrDefault();
        Check("Health mention routing: the route person's own health mention confirms with the backup named",
            selfReport.WrittenEvents.Count == 1
            && selfEvent is not null && selfEvent.Type == EventType.HealthMention
            && selfReport.HouseReply == "I've written that down. Do you want me to tell Lisa?"
            && selfReport.Output.AlertDecision is { RouteToPersonId: 3, BackupPersonId: 2 });
        Check("Health mention routing: the stored audience rule for that mention names the backup as its route, not the speaker",
            selfEvent is not null
            && (selfEvent.AudienceRule ?? "").Contains("\"RoutePersonId\":3")
            && !(selfEvent.AudienceRule ?? "").Contains("\"RoutePersonId\":2")
            && !(selfEvent.AudienceRule ?? "").Contains("\"BackupPersonId\":2"));
        if (selfEvent is not null)
        {
            await confirmations.ConfirmAsync(selfEvent.Id, 2, "tap");
        }
        var selfAfter = selfEvent is null ? null : await db.Events.FirstAsync(e => e.Id == selfEvent.Id);
        Check("Health mention routing: confirming delivers the mention to the backup only; the speaker receives nothing",
            selfEvent is not null
            && selfAfter is not null && selfAfter.ConfirmationState == ConfirmationState.Done
            && await db.AlertDeliveries.CountAsync(d => d.EventId == selfEvent.Id && d.ToPersonId == 3) == 1
            && await db.AlertDeliveries.CountAsync(d => d.EventId == selfEvent.Id && d.ToPersonId == 2) == 0
            && await db.AlertDeliveries.CountAsync(d => d.EventId == selfEvent.Id) == 1);
        // ---------- Promises and open decisions follow the stored audience ----------
        async Task<Promise> StoredAudiencePromise(string text, SensitivityClass cls, string rule, int? promiseeId)
        {
            var src = new Event
            {
                HouseholdId = householdId,
                Type = EventType.Commitment,
                Body = text,
                SubjectPersonId = 2,
                AuthorPersonId = 2,
                CreatedAtUtc = DateTime.UtcNow,
                SensitivityClass = cls,
                AudienceRule = rule
            };
            await confirmations.WriteStatementAsync(src, "tap");
            var promise = new Promise
            {
                HouseholdId = householdId,
                PromisorPersonId = 2,
                PromiseePersonId = promiseeId,
                CommitmentText = text,
                SourceEventId = src.Id,
                State = PromiseState.Made
            };
            db.Promises.Add(promise);
            await db.SaveChangesAsync();
            return promise;
        }

        var makerOnlyPromise = await StoredAudiencePromise("Stored audience maker-only promise canary.",
            SensitivityClass.Named, """{"Class":"Named","MakerPersonId":2,"AudiencePersonIds":[2]}""", null);
        var makerBriefC = await briefs.BuildBriefAsync(2, today);
        var lisaBriefC = await briefs.BuildBriefAsync(3, today);
        var margaretBriefC = await briefs.BuildBriefAsync(1, today);
        Check("Stored audience: the brief shows a no-promisee promise stored maker-only to its maker only, never to everyone",
            makerBriefC.Any(l => l.Text.Contains("maker-only promise canary"))
            && !lisaBriefC.Any(l => l.Text.Contains("maker-only promise canary"))
            && !margaretBriefC.Any(l => l.Text.Contains("maker-only promise canary")));

        var namedPromiseC = await StoredAudiencePromise("Stored audience named promise canary.",
            SensitivityClass.Named, """{"Class":"Named","MakerPersonId":2,"PromiseePersonId":3,"AudiencePersonIds":[2,3]}""", 3);
        var lisaBriefNamed = await briefs.BuildBriefAsync(3, today);
        var margaretBriefNamed = await briefs.BuildBriefAsync(1, today);
        Check("Stored audience: the brief shows a named promise to its maker and promisee only",
            lisaBriefNamed.Any(l => l.Text.Contains("named promise canary"))
            && !margaretBriefNamed.Any(l => l.Text.Contains("named promise canary")));

        var housePromiseC = await StoredAudiencePromise("Stored audience house promise canary.",
            SensitivityClass.Household, """{"Class":"Household"}""", null);
        var margaretBriefHouse = await briefs.BuildBriefAsync(1, today);
        Check("Stored audience: a no-promisee promise whose stored rule is Household still reaches the house in the brief (the stored rule decides, not a blanket)",
            margaretBriefHouse.Any(l => l.Text.Contains("house promise canary")));

        var promisesPageC = new HomeNode.Pages.PromisesModel(db, confirmations)
        {
            TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(
                new Microsoft.AspNetCore.Http.DefaultHttpContext(),
                new PassthroughTempDataProvider())
        };
        promisesPageC.ViewerId = 3;
        await promisesPageC.OnGetAsync();
        Check("Stored audience: /Promises for Lisa lists the promise named to her but not the maker-only promise",
            promisesPageC.Promises.Any(p => p.Id == namedPromiseC.Id)
            && !promisesPageC.Promises.Any(p => p.Id == makerOnlyPromise.Id));
        promisesPageC.ViewerId = 2;
        await promisesPageC.OnGetAsync();
        Check("Stored audience: /Promises for the maker lists both the maker-only and the named promise",
            promisesPageC.Promises.Any(p => p.Id == makerOnlyPromise.Id)
            && promisesPageC.Promises.Any(p => p.Id == namedPromiseC.Id));
        promisesPageC.ViewerId = null;
        await promisesPageC.OnGetAsync();
        Check("Stored audience: /Promises with no reader named shows the Household promise and neither narrowed promise",
            promisesPageC.Promises.Any(p => p.Id == housePromiseC.Id)
            && !promisesPageC.Promises.Any(p => p.Id == makerOnlyPromise.Id)
            && !promisesPageC.Promises.Any(p => p.Id == namedPromiseC.Id));

        // A Private dinner question (Lisa's, stored Personal
        // under her seeded default shape) under Open decisions.
        var privateDecision = new Event
        {
            HouseholdId = householdId,
            Type = EventType.HouseholdNote,
            Body = "Stored audience private dinner question canary?",
            SubjectPersonId = 3,
            AuthorPersonId = 3,
            CreatedAtUtc = DateTime.UtcNow,
            SensitivityClass = SensitivityClass.Personal,
            AudienceRule = """{"Class":"Personal","AudiencePersonIds":[3]}""",
            Details = """{"DecisionTopic":"dinner","DecisionState":"Undecided"}"""
        };
        await confirmations.WriteStatementAsync(privateDecision, "tap");
        var plansPageC = new HomeNode.Pages.PlansModel(db, plans)
        {
            TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(
                new Microsoft.AspNetCore.Http.DefaultHttpContext(),
                new PassthroughTempDataProvider())
        };
        plansPageC.ViewerId = 2;
        await plansPageC.OnGetAsync();
        Check("Stored audience: /Plans open decisions do not show a Private note to another reader",
            !plansPageC.OpenDecisions.Any(e => e.Id == privateDecision.Id));
        plansPageC.ViewerId = 3;
        await plansPageC.OnGetAsync();
        Check("Stored audience: /Plans open decisions still show a Private note to its author",
            plansPageC.OpenDecisions.Any(e => e.Id == privateDecision.Id));
        plansPageC.ViewerId = null;
        await plansPageC.OnGetAsync();
        Check("Stored audience: /Plans with no reader named shows Household open decisions and never the Private note",
            plansPageC.OpenDecisions.Any(e => e.SensitivityClass == SensitivityClass.Household)
            && !plansPageC.OpenDecisions.Any(e => e.Id == privateDecision.Id));

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

    // An extraction client whose output always fails validation:
    // an unknown event type carrying words that were never said.
    // It stands in for a live output the validator refuses, so the
    // front door refusal path can be exercised without a network.
    private sealed class InvalidExtractionClient : IExtractionClient
    {
        public string Name => "InvalidStub";

        public Task<ExtractionOutput> ExtractAsync(ExtractionInput input, CancellationToken ct = default)
            => Task.FromResult(new ExtractionOutput
            {
                UtteranceId = input.UtteranceId,
                SpokenResponse = "Written down.",
                Events = new List<ExtractionEvent>
                {
                    new()
                    {
                        Type = "NotARealType",
                        Body = "Words that were never said in the telling.",
                        ConfirmationState = "Confirmed",
                        AuthorPersonId = 1
                    }
                }
            });
    }

    // Captures what a page model writes to its server log, so a
    // check can prove the validator's raw words landed there and
    // nowhere a visitor reads.
    private sealed class CapturingFrontDoorLogger : ILogger<HomeNode.Pages.FrontDoorModel>
    {
        public List<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
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

        // The seeded holder for the audience defaults exercise:
        // June holds a Private audience default, seeded the
        // way the demo seed seeds Lisa's. She authors and is the
        // subject of nothing in the corpus above, so the row is inert
        // until the exercise block reads it.
        db.PersonAudienceDefaults.Add(new PersonAudienceDefault
        {
            HouseholdId = household.Id,
            HolderPersonId = 6,
            Value = AudienceDefaultValue.Private,
            SetAtUtc = new DateTime(2026, 10, 27, 6, 0, 0, DateTimeKind.Utc)
        });
        db.SaveChanges();
        return household.Id;
    }
}
