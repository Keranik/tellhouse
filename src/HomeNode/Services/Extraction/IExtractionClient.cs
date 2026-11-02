namespace HomeNode.Services.Extraction;

// The model-role seam: one interface, two paths behind it. The live
// path calls the Meta Model API (extraction role: Muse Spark,
// Standard tier for anything demo-facing; the reserved clarification
// role, Muse Wonder, is never called by this build). The mock path is
// a deterministic local extractor used for development, tests, and
// the demo suite, so no build step ever needs an exploratory API
// call: mocks and fixtures are used until a run is genuinely
// ready.
public interface IExtractionClient
{
    string Name { get; }
    Task<ExtractionOutput> ExtractAsync(ExtractionInput input, CancellationToken ct = default);

    // What the most recent call reported about itself: the model
    // that served it and the tokens it used, when the client knows.
    // The pipeline keeps it on the message's reading so the pages
    // can show the model's work. Clients that call no model (the
    // local stand-in, the suites' stubs) report nothing.
    ExtractionCallInfo? LastCall => null;
}

public sealed record ExtractionCallInfo(string? Model, int? PromptTokens, int? CompletionTokens);
