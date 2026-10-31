using HomeNode.Data;

namespace HomeNode.Services;

// Rendering for an event's provenance, shared by every surface that
// shows the record (front door recent list, timeline, member views),
// so a federated event reads the same way wherever it appears. An
// event is federated when the node recorded an origin for it when
// its packet was opened; a plain local event has no origin and
// renders no provenance line at all. Provenance is part of the
// event, so it travels with the event through each surface's stored
// audience filter: a reader who may not see the event never sees
// its provenance either, and there is no separate provenance list
// that could leak an event a surface hides.
public static class ProvenanceRendering
{
    public static bool IsFederated(Event ev) => !string.IsNullOrWhiteSpace(ev.ProvenanceOrigin);

    // One plain sentence: where the telling came from. The
    // connection and packet identifiers stay in the record's own
    // fields; they are protocol working details and are not printed
    // on the reader surfaces.
    public static string? Describe(Event ev)
    {
        if (!IsFederated(ev))
        {
            return null;
        }
        return $"Came via {ev.ProvenanceOrigin}.";
    }
}
