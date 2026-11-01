namespace HomeNode.Data;

// The one place confirmation states turn into words for the screen, so the
// phrasing stays the same on every surface. One pending phrase everywhere
// ("Needs confirmation"); a settled state names the person who acted and
// when, never the words "by a person".
public static class ConfirmationWording
{
    public static string For(ConfirmationState state) => state switch
    {
        ConfirmationState.Proposed => "Needs confirmation",
        ConfirmationState.Confirmed => "Confirmed",
        ConfirmationState.Done => "Done",
        ConfirmationState.NotConfirmed => "Not sent",
        _ => state.ToString()
    };

    // The detailed form: a settled state carries who acted and the
    // household time they acted at, read from the record's audit row.
    public static string For(ConfirmationState state, string? actorFirstName, string? timeText) =>
        state switch
        {
            ConfirmationState.Confirmed when actorFirstName is not null && timeText is not null =>
                $"Confirmed · {actorFirstName}, {timeText}",
            ConfirmationState.Done when actorFirstName is not null && timeText is not null =>
                $"Done · {actorFirstName}, {timeText}",
            _ => For(state)
        };
}
