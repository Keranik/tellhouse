using HomeNode.Data;

namespace HomeNode.Services;

// What a page hands the shared trace partial (Pages/Shared/_Trace):
// the item, and how the page answers a proposal (its path, the field
// name its handlers bind the actor from, and the reader, if named).
public sealed record TraceView(ActivityItem Item, TraceOptions Options);

public sealed class TraceOptions
{
    // The page whose handlers answer this trace's decision buttons.
    public string PagePath { get; init; } = "/";

    // The form field the page's Confirm and Decline handlers bind the
    // acting person from ("actorPersonId" on Home, "actorId" on
    // Timeline).
    public string ActorField { get; init; } = "actorPersonId";

    // The reader the page is showing, who answers as themselves.
    public int? ActorId { get; init; }

    // When there is no reader, the people a "Confirming as" choice is
    // offered from (the message just sent on Home). Empty means the
    // trace asks the reader to choose who they are first.
    public IReadOnlyList<Person> People { get; init; } = Array.Empty<Person>();
    public int? DefaultActorId { get; init; }

    // Whether the page has a Close handler for person-free entries
    // (Timeline); otherwise Confirm closes them (Home).
    public bool HasCloseHandler { get; init; }

    // The reader's lens, carried through a decision so the page comes
    // back to the same view.
    public string? LensField { get; init; }

    // False where the page shows a trace but does not take answers
    // (the model page): a waiting step then says where to answer.
    public bool AllowDecision { get; init; } = true;

    public bool ShowRaw { get; init; } = true;
    public bool Compact { get; init; }

    // Home's conversation shows the words and the decision buttons
    // itself, above the trace: the embedded trace then leaves out its
    // own header, words and buttons, and keeps the receipt and every
    // step.
    public bool HideHead { get; init; }
    public bool HideDecisions { get; init; }
    public TimeZoneInfo Zone { get; init; } = HouseholdTime.ZoneFor(null);
}

public static class PersonStyle
{
    // A stable color slot for a person's initial badge.
    public static int Slot(int? personId) => personId is int id ? (id - 1) % 6 + 1 : 0;

    public static string Initial(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "·" : name.Trim()[..1].ToUpperInvariant();
}

// The person switcher in the shell: the household and its people, the
// person the current page shows, and the address to come back to.
public sealed record PersonSwitcherView(IReadOnlyList<HomeNode.Data.Person> People, int? Current, string ReturnTo);

// One delivery card in the Inbox: what was passed on, how it got here
// (only when the member may see the entry it came from), and the
// picture card Muse Image drew for it, if any.
public sealed record DeliveryView(HomeNode.Services.MemberDeliveryItem Item, ActivityItem? Trace, HomeNode.Data.Person Member,
    IReadOnlyList<HomeNode.Data.Person> People, TimeZoneInfo Zone, string TimelineHref, int? PictureCallId = null);
