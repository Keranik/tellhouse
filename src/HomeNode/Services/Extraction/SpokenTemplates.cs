namespace HomeNode.Services.Extraction;

// The fixed speech templates. The pipeline selects a template and
// fills its named slots; it never composes a sentence. This table
// is the complete set and nothing else: four further templates
// (T-PROMISE-BORN, T-PROMISE-CLOSED, T-PLAN-READY, T-NOTSHARED)
// were considered during design and are deliberately not here.
public static class SpokenTemplates
{
    public static string Route(string firstName) =>
        $"I've written that down. Do you want me to tell {firstName}?";

    public static string RouteQuestion(string firstName) =>
        $"I've written that down exactly as you said it. Do you want me to tell {firstName}?";

    public const string Recorded = "Written down.";

    public static string RelayConfirm(string firstName, string messageText) =>
        $"Do you want me to tell {firstName}: {messageText}?";

    public static string MediaConfirm(string whatText) =>
        $"Shall I put {whatText} on?";

    public static string RemindConfirm(string remindOnText, string taskText) =>
        $"Shall I remind you on {remindOnText} to {taskText}?";

    public static string Supply(string itemName, string directionWord) =>
        $"Noted. {itemName} marked {directionWord}. The morning brief will carry it.";

    public static string SupplyForecast(string itemName, string directionWord, string dayName) =>
        $"Noted. {itemName} marked {directionWord}. At the usual rate, it runs out around {dayName}.";

    public const string DoNotKnow = "That is not in the record.";

    public static string Clarify(string questionText) =>
        $"I want to get that right. {questionText}";

    public const string Done = "Done.";

    public const string Kept = "Kept here. Nothing sent.";

    public static string DirectionWord(string direction) => direction switch
    {
        "Low" => "low",
        "Out" => "out",
        "Used" => "used",
        "Restocked" => "restocked",
        _ => direction.ToLowerInvariant()
    };
}
