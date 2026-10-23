using System.Runtime.CompilerServices;
using ScreenFlow.Core.Config;
using ScreenFlow.Core.Extraction;
using ScreenFlow.Core.Ir;
using ScreenFlow.CSharp.Compilation;
using ScreenFlow.CSharp.Values;
using ScreenFlow.CSharp.Walkers;

namespace ScreenFlow.CSharp;

/// <summary>
/// The C# fact extractor (02 §1.1, §6): discovery, compilation and IR emission for a Unity project.
/// <para>
/// It emits the record kinds of 02 §5 and nothing else — the core consumes IR, so every answer the
/// index later gives traces back to a record produced here. Conditions and reachability
/// (<c>cfg</c>) arrive in T10; this stage covers meta, file, symbol, impl, callsite, assign,
/// creation, marker and diag.
/// </para>
/// </summary>
public sealed class CSharpExtractor : IFactExtractor
{
    /// <summary>Recorded in <see cref="MetaRec.Extractor"/> (02 §5.8).</summary>
    public const string ExtractorName = "csharp-roslyn";

    /// <summary>Pipeline phase that reports symbol collection progress.</summary>
    public const string SymbolPhase = "P3";

    /// <summary>Pipeline phase that reports method body extraction progress.</summary>
    public const string BodyPhase = "P4";

    private readonly DomainConfig _domain;
    private readonly int _parallelism;

    public CSharpExtractor(DomainConfig domain, int parallelism = 0)
    {
        _domain = domain;
        _parallelism = parallelism;
    }

    public string ExtractorId => ExtractorName;

    public string IrVersion => IrValidator.SupportedIrVersion;

    public async Task<ExtractionResult> ExtractAsync(ExtractionRequest request, CancellationToken ct)
    {
        var records = await Task.Run(() => Collect(request, ct), ct).ConfigureAwait(false);
        return new ExtractionResult(Stream(records, ct));
    }

    /// <summary>
    /// Runs the extraction and returns the whole record stream. Everything is produced before the
    /// first record is handed out, because a half-written index would answer questions wrongly
    /// rather than not at all.
    /// </summary>
    public IReadOnlyList<IrRecord> Collect(ExtractionRequest request, CancellationToken ct = default)
    {
        var profile = request.Profile;
        var preflight = Preflight.Run(request.ProjectRoot, profile);
        var compilations = new CompilationBuilder()
            .Build(request.ProjectRoot, preflight, profile, request.Progress, ct);

        var symbols = new SymbolCollector(profile.Id, _parallelism);
        var implCollector = new ImplCollector();
        var markerScanner = new MarkerScanner(_domain.ChangeMarkers);

        var impls = new List<ImplRec>();
        var markers = new List<MarkerRec>();
        var bodies = new List<IrRecord>();

        var extracted = compilations.ToExtract;
        for (var i = 0; i < extracted.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var assembly = extracted[i];
            request.Progress.Report(new ProgressEvent(SymbolPhase, assembly.Name, i, extracted.Count));

            symbols.Collect(assembly, ct);
            impls.AddRange(implCollector.Collect(assembly, symbols, ct));
            markers.AddRange(markerScanner.Scan(assembly, ct));

            request.Progress.Report(new ProgressEvent(BodyPhase, assembly.Name, i, extracted.Count));

            // The initialisers are folded first: a body may read a static field whose value is in
            // one, and the folder follows that reference (02 §5.4).
            var values = new ValueBuilder(symbols, assembly.Compilation);
            symbols.FoldInitializers(assembly, values, ct);
            bodies.AddRange(new BodyWalker(symbols).Walk(assembly, ct).Records);
        }

        var records = new List<IrRecord>
        {
            new MetaRec
            {
                IrVersion = IrVersion,
                Extractor = ExtractorId,
                ProfileId = profile.Id,
                UnityDeclared = preflight.UnityDeclared,
                UnityCache = preflight.UnityCache,
                PlatformDetected = preflight.PlatformDetected,
                Derivation = preflight.Derivation
            }
        };

        records.AddRange(Preflight.FileRecords(request.ProjectRoot, preflight));

        // Symbols are read after the impl pass, because resolving an interface member is what pulls
        // the metadata symbols behind it into the index (02 §6.5).
        records.AddRange(symbols.Symbols);

        records.AddRange(impls
            .OrderBy(r => r.TypeId, StringComparer.Ordinal)
            .ThenBy(r => r.InterfaceMemberId, StringComparer.Ordinal)
            .ThenBy(r => r.ImplMemberId, StringComparer.Ordinal));

        records.AddRange(bodies);

        records.AddRange(markers
            .OrderBy(m => m.File, StringComparer.Ordinal)
            .ThenBy(m => m.Line));

        records.AddRange(preflight.Diagnostics);
        records.AddRange(compilations.Diagnostics);

        return records;
    }

    private static async IAsyncEnumerable<IrRecord> Stream(
        IReadOnlyList<IrRecord> records,
        [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var record in records)
        {
            ct.ThrowIfCancellationRequested();
            yield return record;
        }

        // The records are materialised before streaming; this is only what makes the iterator async.
        await Task.CompletedTask.ConfigureAwait(false);
    }
}
