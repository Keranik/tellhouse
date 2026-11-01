namespace HouseholdCoordinator.Data;

// The one place confirmation states turn into words for the screen, so the
// phrasing stays the same on every surface: a state is confirmed by a person,
// in plain words.
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
}
