using ScreenFlow.Core.Config;
using ScreenFlow.Core.Ir;

namespace ScreenFlow.Core.Extraction;

/// <summary>
/// Build progress, written to stderr by the CLI so stdout stays a single JSON document
/// (02 §11.3).
/// </summary>
public sealed record ProgressEvent(
    string Phase,
    string? Item = null,
    int Completed = 0,
    int Total = 0,
    string? Message = null);

public sealed record ExtractionRequest(
    string ProjectRoot,
    ProfileConfig Profile,
    IProgress<ProgressEvent> Progress);

/// <summary>
/// The extracted record stream. Diagnostics travel inside it as <see cref="DiagRec"/> rather than
/// alongside it, so nothing can be reported without being ordered against the facts it concerns
/// (02 §5.1).
/// </summary>
public sealed record ExtractionResult(IAsyncEnumerable<IrRecord> Records);

/// <summary>
/// The contract between a language and the core (02 §1.1). The core consumes IR and nothing
/// else, so supporting Java or Python later means writing one more implementation of this — the
/// storage, graph, path and query layers are untouched.
/// </summary>
public interface IFactExtractor
{
    /// <summary>Stable extractor identity, recorded in <see cref="MetaRec.Extractor"/>.</summary>
    string ExtractorId { get; }

    /// <summary>The IR version this extractor emits. Must equal <see cref="IrValidator.SupportedIrVersion"/>.</summary>
    string IrVersion { get; }

    Task<ExtractionResult> ExtractAsync(ExtractionRequest request, CancellationToken ct);
}
