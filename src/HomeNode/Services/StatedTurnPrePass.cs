using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HomeNode.Services.Extraction;

// The stated-turn shape, built on the mock path, moved into a deterministic
// pre-pass in the node's extraction pipeline. It runs before the
// extraction client, on every mode, so the shape cannot be lost on
// the live path: a live model output has no turn field to carry
// it, and without this pass a live extraction of the trash turn
// telling could land as a plain note with no action behind it.
// The shape is a narrow regex over the teller's verbatim words,
// exactly the mock shapes; the model adds nothing to it, so
// no API call is spent on a telling this pass shapes. The output
// is that shape verbatim: one Task, Proposed, carrying
// TaskKind, TaskText, TurnPersonId and TurnItem in Details, one
// proposed NotifyPerson action for the assigned person only, the
// confirmation prompt naming that person, and the alert decision
// routed to the assigned person (the stored Named route).
public static class StatedTurnPrePass
{
    public static ExtractionOutput? TryExtract(ExtractionInput input)
    {
        var ctx = input.Context;
        var speaker = input.Speaker;
        if (speaker is null)
        {
            return null;
        }
        var transcript = input.Transcript;
        var lower = transcript.ToLowerInvariant();
        var m = Regex.Match(lower,
            @"^(?:tonight is|today is|it is|it's)\s+(\w+)'s turn to take\s+(?:out\s+)?the\s+(trash|bins?)\b");
        if (!m.Success)
        {
            m = Regex.Match(lower, @"^(\w+)'s turn to take\s+(?:out\s+)?the\s+(trash|bins?)\b");
        }
        if (!m.Success)
        {
            m = Regex.Match(lower, @"^(\w+) takes the\s+(trash|bins?)\s+out\b");
        }
        if (!m.Success)
        {
            return null;
        }
        var assigned = ctx.Persons.FirstOrDefault(p =>
            string.Equals(p.FirstName, m.Groups[1].Value, StringComparison.OrdinalIgnoreCase));
        if (assigned is null)
        {
            return null;
        }
        var item = m.Groups[2].Value.StartsWith("bin") ? "bins" : "trash";
        var output = new ExtractionOutput { UtteranceId = input.UtteranceId };
        var details = new JsonObject
        {
            ["TaskKind"] = "General",
            ["TaskText"] = $"take the {item} out",
            ["TurnPersonId"] = assigned.PersonId,
            ["TurnItem"] = item
        };
        var due = Regex.Match(lower, @"\b(?:at|by|comes at)\s+(one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve)\b");
        if (due.Success)
        {
            details["DueText"] = due.Groups[1].Value;
        }
        output.Events.Add(new ExtractionEvent
        {
            Type = "Task",
            Body = transcript,
            SubjectPersonId = assigned.PersonId,
            AuthorPersonId = speaker.PersonId,
            ConfirmationState = "Proposed",
            Details = details
        });
        output.ProposedAction = new ExtractionAction
        {
            ActionType = "NotifyPerson",
            TargetPersonId = assigned.PersonId,
            EventIndex = 0,
            Payload = new JsonObject { ["AboutEventIndex"] = 0 }
        };
        var prompt = $"Do you want me to tell {assigned.FirstName}?";
        output.ConfirmationPrompt = prompt;
        output.SpokenResponse = prompt;
        output.AlertDecision = new ExtractionAlertDecision
        {
            Fire = false,
            FiresOn = "Confirmation",
            LevelId = "now",
            RouteToPersonId = assigned.PersonId,
            BackupPersonId = null,
            Reason = "TurnReminderOnConfirmation"
        };
        return output;
    }
}
