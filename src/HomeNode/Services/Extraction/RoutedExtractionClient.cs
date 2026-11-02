namespace HomeNode.Services.Extraction;

// The seam's router: one client in front of two readers.
//
//   - A message the PrivacyRule calls private (the person's Private
//     switch, or health-shaped words) is read by the house's own
//     deterministic reader, on this node, and by nothing else.
//   - Every other message goes to the configured reader: Muse Spark
//     over the Meta Model API on a Live instance, the local stand-in
//     otherwise.
//
// Fail safe: the model reader is never a fallback for a private
// message. If the house's reader throws, the exception propagates
// and nothing is written (the pipeline saves nothing when its engine
// throws); the private words go nowhere else. The router decides
// before any reader sees the input, so the decision can never be
// influenced by what a model said.
public sealed class RoutedExtractionClient : IExtractionClient
{
    private readonly IExtractionClient _model;
    private readonly IExtractionClient _house;
    private bool _lastWasPrivate;

    public RoutedExtractionClient(IExtractionClient model, IExtractionClient house)
    {
        _model = model;
        _house = house;
    }

    // The engine that produced the most recent output: "private" when
    // the house's own reader read it, the model reader's name
    // otherwise. Read by the pipeline right after the call, for the
    // message's reading record.
    public string Name => _lastWasPrivate ? HomeNode.Data.ModelReadingEngine.Private : _model.Name;

    public ExtractionCallInfo? LastCall => _lastWasPrivate ? null : _model.LastCall;

    // The configured model reader's name, for surfaces that name the
    // engine of the instance rather than of one message.
    public string ModelReaderName => _model.Name;

    public async Task<ExtractionOutput> ExtractAsync(ExtractionInput input, CancellationToken ct = default)
    {
        _lastWasPrivate = PrivacyRule.Decide(input) is not null;
        return _lastWasPrivate
            ? await _house.ExtractAsync(input, ct)
            : await _model.ExtractAsync(input, ct);
    }
}
