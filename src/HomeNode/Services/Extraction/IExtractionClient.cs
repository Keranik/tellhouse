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
}
