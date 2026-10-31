namespace HomeNode.Data;

// Plain-word labels for stored names, so no surface prints a raw
// enum or code word to a reader.
// The stored values stay exactly as they are; only their rendering
// changes.
public static class EventKindWording
{
    public static string For(EventType type) => type switch
    {
        EventType.Request => "Request",
        EventType.HouseholdNote => "Household note",
        EventType.Supply => "Supply",
        EventType.Journal => "Journal",
        EventType.FacilityNotice => "Facility notice",
        EventType.Whereabouts => "Whereabouts",
        EventType.HealthMention => "Health mention",
        EventType.Commitment => "Commitment",
        EventType.Task => "Task",
        _ => type.ToString()
    };
}

public static class StateWording
{
    public static string ForPlan(PlanState state) => state switch
    {
        PlanState.Assembling => "Still being put together",
        PlanState.Proposed => "Waiting on answers",
        PlanState.Acknowledged => "The runner has the plan",
        PlanState.Completed => "Done",
        PlanState.Superseded => "Replaced by a newer plan",
        PlanState.Dropped => "Dropped",
        _ => state.ToString()
    };

    public static string ForElement(PlanElementState state) => state switch
    {
        PlanElementState.Open => "Still open",
        PlanElementState.Settled => "Settled",
        PlanElementState.Dropped => "Dropped",
        _ => state.ToString()
    };

    public static string ForDisposal(PlanDisposal disposal) => disposal switch
    {
        PlanDisposal.Agreed => "Agreed",
        PlanDisposal.Declined => "Declined",
        PlanDisposal.Countered => "Proposed a change",
        _ => disposal.ToString()
    };

    public static string ForStanding(ConnectionStanding standing) => standing switch
    {
        ConnectionStanding.Active => "Active",
        ConnectionStanding.KnockOnly => "Knock only",
        ConnectionStanding.Revoked => "Revoked",
        _ => standing.ToString()
    };

    // Connection levels are stored as the words the inviter picked
    // ("Standard", "KnockOnly"); only the joined code word needs
    // respacing for a reader.
    public static string ForLevel(string level) => level switch
    {
        "KnockOnly" => "Knock only",
        _ => level
    };
}
