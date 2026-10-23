using System.Diagnostics;
using System.Text.Json;
using ScreenFlow.Core.Config;
using ScreenFlow.Core.Extraction;
using ScreenFlow.Core.Ir;
using ScreenFlow.Core.Resolution;
using ScreenFlow.Core.Storage;

namespace ScreenFlow.Core.Pipeline;

/// <summary>
/// One domain stage of the build (pipeline phases P6–P8, 02 §2).
/// <para>
/// The core runs these without knowing what they do: resources, POS adapters and the screen graph
/// all live outside it, which is what keeps <c>ScreenFlow.Core</c> free of POS specifics (02 §1.1).
/// </para>
/// </summary>
public interface IDomainStage
{
    /// <summary>Pipeline phase, for progress reporting: <c>P6</c>, <c>P7</c> or <c>P8</c>.</summary>
    string Phase { get; }

    /// <summary>Stage name, as it appears in diagnostics and in the build report.</summary>
    string Name { get; }

    void Run(DomainStageContext context);
}

/// <summary>Everything a domain stage is given, and everything it may add (02 §2).</summary>
public sealed class DomainStageContext
{
    public required IndexDb Db { get; init; }
    public required IndexReader Reader { get; init; }
    public required string ProjectRoot { get; init; }
    public required ProfileConfig Profile { get; init; }
    public required DomainConfig Domain { get; init; }
    public required IProgress<ProgressEvent> Progress { get; init; }
    public required ResolutionIndex Resolution { get; init; }
    public required ValueResolver Values { get; init; }
    public required Dispatcher Dispatcher { get; init; }
    public CancellationToken Ct { get; init; }

    /// <summary>Diagnostics the stage produces; written to the index in P9.</summary>
    public List<DiagRec> Diagnostics { get; } = new();

    /// <summary>
    /// Features this stage had to switch off, typically because an anchor did not resolve. Listed
    /// in the build report so a missing answer is never mistaken for an empty one (02 §4.3).
    /// </summary>
    public List<string> DisabledFeatures { get; } = new();

    /// <summary>
    /// Providers a stage registers for the stages after it: the DI adapter teaches the resolver
    /// what <c>GetService&lt;T&gt;()</c> means, and the screen graph then benefits (02 §8.2).
    /// </summary>
    public List<IValueProvider> Providers { get; } = new();

    /// <summary>Files a stage read, which count towards the staleness fingerprint (02 §7.6).</summary>
    public List<string> ReadFiles { get; } = new();

    /// <summary>
    /// Phases the stage actually ran. A stage may cover more than one phase of 02 §2 — the POS
    /// domain stage reads resources (P6) before running its adapters (P7) — and the build report
    /// names each of them rather than only the stage's own.
    /// </summary>
    public List<string> RanPhases { get; } = new();
}

/// <summary>
/// Runs the build pipeline (02 §2).
/// <para>
/// The index is written to <c>&lt;db&gt;.building</c> and only swapped in once every phase has
/// succeeded, so a failed build leaves the previous index in place rather than a half-written one
/// that would answer questions wrongly.
/// </para>
/// </summary>
public sealed class IndexBuilder
{
    /// <summary>Phases the core itself runs; P6–P8 come from the stage list (02 §2).</summary>
    public const string PreflightPhase = "P1";
    public const string StorePhase = "P4";
    public const string ResolutionPhase = "P5";
    public const string FinishPhase = "P9";

    private readonly IReadOnlyList<IDomainStage> _stages;

    public IndexBuilder(IReadOnlyList<IDomainStage>? stages = null)
        => _stages = stages ?? Array.Empty<IDomainStage>();

    public IReadOnlyList<IDomainStage> Stages => _stages;

    /// <summary>
    /// Builds the index. Synchronous by design: it is driven by a CLI command and the extraction is
    /// CPU-bound, so there is no caller to yield to.
    /// </summary>
    public BuildReport Build(
        string projectRoot,
        ProfileConfig profile,
        DomainConfig domain,
        string dbPath,
        string configDirectory,
        Func<IFactExtractor> extractorFactory,
        IProgress<ProgressEvent>? progress = null,
        CancellationToken ct = default)
        => BuildAsync(
                projectRoot, profile, domain, dbPath, configDirectory, extractorFactory, progress, ct)
            .GetAwaiter()
            .GetResult();

    public async Task<BuildReport> BuildAsync(
        string projectRoot,
        ProfileConfig profile,
        DomainConfig domain,
        string dbPath,
        string configDirectory,
        Func<IFactExtractor> extractorFactory,
        IProgress<ProgressEvent>? progress = null,
        CancellationToken ct = default)
    {
        var clock = Stopwatch.StartNew();
        var report = progress ?? ProgressReporter.Null;
        var phases = new List<string>();

        using var build = AtomicBuild.Begin(dbPath);
        var db = build.Db;

        // P1–P4: the extractor runs discovery, compilation and extraction; the writer stores it.
        var extractor = extractorFactory();
        if (extractor.IrVersion != IrValidator.SupportedIrVersion)
        {
            throw new IrSchemaException(
                $"extractor '{extractor.ExtractorId}' emits IR version '{extractor.IrVersion}', " +
                $"this core supports '{IrValidator.SupportedIrVersion}'.");
        }

        var request = new ExtractionRequest(projectRoot, profile, report);
        var extraction = await extractor.ExtractAsync(request, ct).ConfigureAwait(false);

        MetaRec? meta;
        using (var writer = new IrWriter(db, IrWriter.DefaultBatchSize, SnippetHasher(db, projectRoot)))
        {
            await writer.WriteAllAsync(extraction.Records, ct).ConfigureAwait(false);
            meta = writer.Meta;
        }

        phases.AddRange(new[] { PreflightPhase, "P2", "P3", StorePhase });

        if (meta is null)
        {
            throw new IrSchemaException(
                $"extractor '{extractor.ExtractorId}' produced no meta record; " +
                "the index would not know which profile or platform it describes (02 §5.8).");
        }

        // P5: the in-memory indexes dispatch needs.
        report.Report(new ProgressEvent(ResolutionPhase, "resolution"));
        var reader = new IndexReader(db);
        var resolution = ResolutionIndex.Build(reader);
        var fields = new FieldPointsTo(reader, resolution);
        var resolver = new ValueResolver(reader, resolution, fields);
        var dispatcher = new Dispatcher(reader, resolution, resolver, domain.Traversal);
        phases.Add(ResolutionPhase);

        // P6–P8: the domain stages, in the order they were given (02 §2).
        var diagnostics = new List<DiagRec>();
        var disabled = new List<string>();
        var readFiles = new List<string>();
        var providers = new List<IValueProvider>();

        foreach (var stage in _stages)
        {
            ct.ThrowIfCancellationRequested();
            report.Report(new ProgressEvent(stage.Phase, stage.Name));

            var context = new DomainStageContext
            {
                Db = db,
                Reader = reader,
                ProjectRoot = projectRoot,
                Profile = profile,
                Domain = domain,
                Progress = report,
                Resolution = resolution,
                Values = resolver,
                Dispatcher = dispatcher,
                Ct = ct
            };

            context.Providers.AddRange(providers);
            stage.Run(context);

            diagnostics.AddRange(context.Diagnostics);
            disabled.AddRange(context.DisabledFeatures);
            readFiles.AddRange(context.ReadFiles);
            providers.AddRange(context.Providers.Where(p => !providers.Contains(p)));

            foreach (var phase in new[] { stage.Phase }.Concat(context.RanPhases))
                if (!phases.Contains(phase)) phases.Add(phase);
        }

        // P9: stage diagnostics, meta, fingerprint, atomic replace.
        report.Report(new ProgressEvent(FinishPhase, "meta"));

        if (diagnostics.Count > 0)
        {
            using var writer = new IrWriter(db) { Phase = FinishPhase };
            foreach (var diagnostic in diagnostics) writer.Write(diagnostic);
            writer.Flush();
        }

        var fingerprint = Fingerprint.Compute(FingerprintFiles(projectRoot, reader, readFiles), configDirectory);

        // Meta first, then the counts: meta rows are rows too, and a count taken before they exist
        // would report the meta table as empty.
        WriteMeta(db, meta, fingerprint, disabled);
        var counts = db.AllCounts();
        db.SetMeta(MetaKeys.Counts, JsonSerializer.Serialize(counts));

        db.SealForQuerying();
        phases.Add(FinishPhase);

        var byCode = CountDiagnostics(reader, "code");
        var bySeverity = CountDiagnostics(reader, "severity");

        build.Commit();

        return new BuildReport
        {
            ProfileId = meta.ProfileId,
            Derivation = meta.Derivation,
            PlatformDetected = meta.PlatformDetected,
            UnityDeclared = meta.UnityDeclared,
            UnityCache = meta.UnityCache,
            DbPath = Ids.Normalize(dbPath),
            Counts = counts,
            Diagnostics = byCode,
            DiagnosticsBySeverity = bySeverity,
            DisabledFeatures = disabled.Distinct(StringComparer.Ordinal).OrderBy(f => f, StringComparer.Ordinal).ToList(),
            Fingerprint = fingerprint.ToString(),
            ElapsedMs = clock.ElapsedMilliseconds,

            // In pipeline order, whichever order the stages reported them in (02 §2).
            Phases = phases.OrderBy(phase => phase, StringComparer.Ordinal).ToList()
        };
    }

    private static void WriteMeta(
        IndexDb db,
        MetaRec meta,
        Fingerprint.Value fingerprint,
        IReadOnlyList<string> disabled)
    {
        db.SetMeta(MetaKeys.IrVersion, meta.IrVersion);
        db.SetMeta(MetaKeys.RulesVersion, ScreenFlowVersion.Rules);
        db.SetMeta(MetaKeys.ProfileId, meta.ProfileId);
        db.SetMeta(MetaKeys.Derivation, meta.Derivation);
        db.SetMeta(MetaKeys.Fingerprint, fingerprint.ToString());
        db.SetMeta(
            MetaKeys.DisabledFeatures,
            JsonSerializer.Serialize(disabled.Distinct(StringComparer.Ordinal).OrderBy(f => f, StringComparer.Ordinal)));

        // The only timestamp in the index, and the one key the canonical dump excludes (02 §7.5).
        db.SetMeta(MetaKeys.BuiltAt, DateTimeOffset.UtcNow.ToString("O"));

        if (meta.PlatformDetected is not null) db.SetMeta(MetaKeys.PlatformDetected, meta.PlatformDetected);
        if (meta.UnityDeclared is not null) db.SetMeta(MetaKeys.UnityDeclared, meta.UnityDeclared);
        if (meta.UnityCache is not null) db.SetMeta(MetaKeys.UnityCache, meta.UnityCache);
    }

    /// <summary>
    /// The files the fingerprint covers (02 §7.6): every analysed source, plus the resources the
    /// domain stages read. Paths are made absolute because the fingerprint is taken from the file
    /// system, not from the index.
    /// </summary>
    private static IEnumerable<string> FingerprintFiles(
        string projectRoot,
        IndexReader reader,
        IReadOnlyList<string> readFiles)
        => reader.AllFiles()
            .Select(file => Path.Combine(projectRoot, file.Path))
            .Concat(readFiles.Select(file => Path.IsPathRooted(file) ? file : Path.Combine(projectRoot, file)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(file => file, StringComparer.Ordinal);

    /// <summary>
    /// Hashes each evidence snippet (02 §7.6). The hasher runs at flush time, after the
    /// <c>files</c> rows of the same batch are in the database, so the recorded encoding is the
    /// one used to decode the snippet.
    /// </summary>
    private static Func<string, Span, string?> SnippetHasher(IndexDb db, string projectRoot)
        => (file, span) =>
        {
            var encoding = EncodingOf(db, file) ?? Encodings.Utf8;
            return Storage.SnippetHasher.Hash(projectRoot, file, span, encoding);
        };

    private static string? EncodingOf(IndexDb db, string file)
    {
        using var command = db.Command("SELECT encoding FROM files WHERE path = $p;");
        command.Parameters.AddWithValue("$p", file);
        return command.ExecuteScalar() is string encoding ? encoding : null;
    }

    private static IReadOnlyDictionary<string, int> CountDiagnostics(IndexReader reader, string column)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        using var command = reader.Db.Command($"SELECT {column}, COUNT(*) FROM diagnostics GROUP BY {column};");
        using var rows = command.ExecuteReader();

        while (rows.Read()) counts[rows.GetString(0)] = rows.GetInt32(1);

        return counts.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }
}
