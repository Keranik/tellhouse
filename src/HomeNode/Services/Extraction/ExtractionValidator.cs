using System.Text.RegularExpressions;

namespace HomeNode.Services.Extraction;

// Checks a pipeline output against the pipeline invariants before
// anything is written. Hard failures stop the write; the record
// never carries an output the rules forbid. The checks here enforce
// the design's fences: one action at most, health
// content as exactly one event, no medical-instruction language in
// product-composed speech, bodies verbatim from the transcript, and
// person links only into the slice the pipeline was given.
public class ExtractionValidation
{
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();
    public bool IsValid => Errors.Count == 0;
}

public static class ExtractionValidator
{
    private static readonly string[] ValidTypes =
    {
        "Request", "HouseholdNote", "Supply", "Journal", "FacilityNotice",
        "Whereabouts", "HealthMention", "Commitment", "Task"
    };

    private static readonly string[] ValidStates = { "Proposed", "Confirmed", "Done", "NotConfirmed" };

    // Medication-taking instruction shapes that may never appear in
    // product-composed text. Verbatim speaker words in
    // an event Body are exempt; speech the product composes is not.
    private static readonly Regex TakingInstruction = new(
        @"\b(take|skip|stop taking|increase|decrease|change)\b[^.!?]*\b(pill|pills|ones|medicine|dose|dosage)\b",
        RegexOptions.IgnoreCase);

    public static ExtractionValidation Validate(ExtractionInput input, ExtractionOutput output)
    {
        var v = new ExtractionValidation();
        var sliceIds = input.Context.Persons.Select(p => p.PersonId).ToHashSet();

        if (output.ClarificationNeeded && output.Events.Count > 0)
        {
            v.Errors.Add("A clarifying output writes no events.");
        }
        // The event cap: five events, plus at most one fold note.
        if (output.Events.Count > 6)
        {
            v.Errors.Add($"{output.Events.Count} events exceeds the cap of five plus a fold note.");
        }
        if (output.Events.Count == 6 && output.Events[5].Type != "HouseholdNote")
        {
            v.Errors.Add("A sixth event is only ever the fold HouseholdNote.");
        }
        if (output.Events.Count(e => e.Type == "HealthMention") > 0 && output.Events.Count != 1)
        {
            v.Errors.Add("Priority rule 1: health content is exactly one HealthMention event.");
        }
        foreach (var ev in output.Events)
        {
            if (!ValidTypes.Contains(ev.Type))
            {
                v.Errors.Add($"Unknown event type '{ev.Type}'.");
            }
            if (!ValidStates.Contains(ev.ConfirmationState))
            {
                v.Errors.Add($"Unknown confirmation state '{ev.ConfirmationState}'.");
            }
            // The body is the speaker's words. A clause body must
            // appear in the transcript once the fold joinery is undone.
            var probe = ev.Body.Trim().TrimEnd('.');
            if (probe.Length > 0 && !input.Transcript.Contains(probe, StringComparison.OrdinalIgnoreCase)
                && !probe.Split(", and ").All(part => input.Transcript.Contains(part.Trim().TrimEnd('.'), StringComparison.OrdinalIgnoreCase)))
            {
                v.Errors.Add($"An event body is not verbatim from the transcript: '{ev.Body}'");
            }
            foreach (var link in new[] { ev.SubjectPersonId, ev.AuthorPersonId })
            {
                if (link is int id && !sliceIds.Contains(id))
                {
                    v.Errors.Add($"Person link {id} is not in the context slice.");
                }
            }
            if (ev.Type == "Commitment")
            {
                if (ev.AuthorPersonId is null)
                {
                    v.Errors.Add("A Commitment with no identifiable maker is never written.");
                }
                if (!ev.Details.ContainsKey("CommitmentText"))
                {
                    v.Errors.Add("A Commitment carries CommitmentText, the clause verbatim.");
                }
            }
            if (ev.Type == "HealthMention" && ev.Details.Count > 1)
            {
                v.Warnings.Add("A HealthMention carries Form and nothing further, ever.");
            }
        }
        if (output.ProposedAction is not null)
        {
            var a = output.ProposedAction;
            if (a.EventIndex < 0 || a.EventIndex >= output.Events.Count)
            {
                v.Errors.Add("The proposed action points outside the event list.");
            }
            if (output.AlertDecision.Fire)
            {
                v.Errors.Add("The pipeline never fires an alert from an utterance.");
            }
        }
        // Product-composed speech carries no instruction
        // and no banned vocabulary.
        foreach (var speech in new[] { output.SpokenResponse, output.ConfirmationPrompt })
        {
            if (string.IsNullOrWhiteSpace(speech))
            {
                continue;
            }
            if (TakingInstruction.IsMatch(speech))
            {
                v.Errors.Add($"Product speech contains a medication-taking instruction: '{speech}'");
            }
            foreach (var banned in ConfirmationPromptsNeverUsed.BannedWords())
            {
                if (Regex.IsMatch(speech, $@"\b{banned}\b", RegexOptions.IgnoreCase))
                {
                    v.Errors.Add($"Product speech contains banned vocabulary '{banned}': '{speech}'");
                }
            }
        }
        return v;
    }
}

// The banned list for product-generated output (speaker utterances
// are exempt; they are recorded verbatim). Kept beside the validator
// because only composed text is ever checked against it.
internal static class ConfirmationPromptsNeverUsed
{
    public static IEnumerable<string> BannedWords()
    {
        yield return "diagnose";
        yield return "diagnosis";
        yield return "treat";
        yield return "treatment";
        yield return "recommend";
        yield return "monitor health";
        yield return "patient";
        yield return "symptom";
        yield return "dose";
        yield return "dosage";
        yield return "adherence";
        yield return "compliance";
        yield return "triage";
    }
}
