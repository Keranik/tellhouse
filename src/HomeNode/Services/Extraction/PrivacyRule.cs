using System.Text.RegularExpressions;

namespace HomeNode.Services.Extraction;

// The house's privacy line for reading. A message is private when the
// person marked it so (the Private switch on Home), or when its words
// are health-shaped: a body, a symptom, a medicine, a doctor, or how
// someone is feeling inside. A private message is read by the house's
// own deterministic rules only. It is never handed to any model,
// on any path, and there is no fallback to one: see
// RoutedExtractionClient.
//
// The rule errs toward private. A message that trips it by accident
// loses nothing but the model's reading: the deterministic reader
// still writes it down in the person's own words, and the person
// can always say it again with different words. A health message
// that slipped past it would reach a model, which cannot be undone,
// so the word list is broad on purpose.
//
// The decision carries a kind ("switch" or "rule") and never the
// matched words, so nothing about the content travels into the
// reading's counted telemetry.
public static class PrivacyRule
{
    public const string BySwitch = "switch";
    public const string ByRule = "rule";

    // Bodies and how they feel. The deterministic reader's own body
    // state words are all here, so anything it would file as a health
    // mention is also kept from the models.
    private static readonly string[] BodyWords =
    {
        "swollen", "swelling", "sprained", "sprain", "hurts", "hurt", "hurting", "pain", "painful",
        "sore", "aching", "ache", "aches", "dizzy", "dizziness", "fever", "nauseous", "nausea",
        "bleeding", "bled", "bruised", "bruise", "headache", "migraine", "rash", "itchy", "infection",
        "infected", "wound", "injury", "injured", "fell", "fallen", "faint", "fainted", "vomit",
        "vomited", "vomiting", "threw up", "throwing up", "cough", "coughing", "wheezing",
        "short of breath", "can't breathe", "cannot breathe", "chest", "seizure", "stroke",
        "heart attack", "blood pressure", "blood sugar", "diabetes", "diabetic", "insulin",
        "incontinent", "incontinence", "constipated", "diarrhea", "unwell", "sick", "ill",
        "symptom", "symptoms", "numb", "numbness", "tingling", "swallow", "choking", "limping",
        "confused", "confusion", "forgetful", "memory"
    };

    // Medicines and care.
    private static readonly string[] CareWords =
    {
        "pill", "pills", "tablet", "tablets", "medication", "medications", "medicine", "medicines",
        "meds", "prescription", "prescriptions", "dose", "doses", "dosage", "inhaler", "antibiotic",
        "antibiotics", "pharmacy", "pharmacist", "doctor", "doctors", "doctor's", "dr", "nurse",
        "hospital", "clinic", "er", "emergency room", "ambulance", "surgery", "operation",
        "therapy", "therapist", "physio", "diagnosis", "diagnosed", "test results", "scan", "x-ray",
        "blood test", "checkup", "check-up"
    };

    // How someone is inside, and words that ask for privacy outright.
    private static readonly string[] InnerWords =
    {
        "depressed", "depression", "anxious", "anxiety", "panic", "panicking", "suicidal",
        "self-harm", "hopeless", "lonely", "scared", "crying", "can't sleep", "cannot sleep",
        "insomnia", "private", "privately", "secret", "don't tell", "do not tell",
        "between us", "just for me", "only for me", "keep this to yourself"
    };

    private static readonly Regex Pattern = Build();

    private static Regex Build()
    {
        var all = BodyWords.Concat(CareWords).Concat(InnerWords)
            .Select(w => Regex.Escape(w).Replace("\\ ", "\\s+").Replace("'", "['’]"))
            .OrderByDescending(w => w.Length);
        return new Regex(@"(?<![\p{L}\p{N}])(" + string.Join("|", all) + @")(?![\p{L}\p{N}])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }

    // True when the words alone are health-shaped or ask for privacy.
    public static bool WordsArePrivate(string? words) =>
        !string.IsNullOrWhiteSpace(words) && Pattern.IsMatch(words);

    // The decision for one input: null when the message may be read
    // by a model; otherwise why it may not.
    // The words come first: a health-shaped message is private by the
    // house's rule even when the switch was also on (Home turns the
    // switch on by itself when it sees such words as they are typed).
    public static string? Decide(ExtractionInput input)
    {
        if (WordsArePrivate(input.Transcript))
        {
            return ByRule;
        }
        return input.Private ? BySwitch : null;
    }
}
