namespace HomeNode.Data;

// The one place confirmation states turn into words for the screen, so the
// phrasing stays the same on every surface: a state is confirmed by a person,
// in plain words.
public static class ConfirmationWording
{
    public static string For(ConfirmationState state) => state switch
    {
        ConfirmationState.Proposed => "Waiting for a person to confirm",
        ConfirmationState.Confirmed => "Confirmed by a person",
        ConfirmationState.Done => "Done",
        ConfirmationState.NotConfirmed => "Not confirmed",
        _ => state.ToString()
    };
}
