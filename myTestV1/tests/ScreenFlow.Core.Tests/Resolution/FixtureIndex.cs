using ScreenFlow.Core.Config;
using ScreenFlow.Core.Pipeline;
using ScreenFlow.Core.Resolution;
using ScreenFlow.Core.Storage;
using ScreenFlow.CSharp;

namespace ScreenFlow.Core.Tests.Resolution;

/// <summary>
/// The MiniPos fixture built through the real pipeline once, kept open for the resolution tests.
/// <para>
/// Built rather than hand-written, because what is under test here is whether dispatch and value
/// resolution work on the shapes 03 documents — a hand-built index would only prove that the test
/// and the code agree.
/// </para>
/// </summary>
internal sealed class FixtureIndex : IDisposable
{
    private static readonly Lazy<FixtureIndex> LazyShared =
        new(() => Create("fixture"), LazyThreadSafetyMode.ExecutionAndPublication);

    public static FixtureIndex Shared => LazyShared.Value;

    private static readonly Lazy<FixtureIndex> LazyGraph =
        new(() => Create("fixture", withDomain: true), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The fixture built with the POS adapters and the screen graph (P6-P8).</summary>
    public static FixtureIndex WithGraph => LazyGraph.Value;

    private FixtureIndex(IndexDb db, BuildReport report, DomainConfig domain, string path)
    {
        Db = db;
        Report = report;
        Domain = domain;
        Path = path;

        Reader = new IndexReader(db);
        Resolution = ResolutionIndex.Build(Reader);
        Fields = new FieldPointsTo(Reader, Resolution);
        Values = new ValueResolver(Reader, Resolution, Fields);
        Dispatcher = new Dispatcher(Reader, Resolution, Values, domain.Traversal);
    }

    public IndexDb Db { get; }
    public IndexReader Reader { get; }
    public BuildReport Report { get; }
    public DomainConfig Domain { get; }
    public string Path { get; }
    public ResolutionIndex Resolution { get; }
    public FieldPointsTo Fields { get; }
    public ValueResolver Values { get; }
    public Dispatcher Dispatcher { get; }

    public ResolveContext Context(string? currentTypeId = null)
        => ResolveContext.FromConfig(Domain.Traversal, providers: null, currentTypeId);

    public static string RepoRoot { get; } = FindRepoRoot();

    public static string MiniPos => System.IO.Path.Combine(RepoRoot, "tests", "fixtures", "MiniPos");

    public static FixtureIndex Create(string profileId, bool withDomain = false)
    {
        var configDirectory = System.IO.Path.Combine(RepoRoot, "config");
        var (profiles, domain) = ConfigLoader.LoadAll(configDirectory);

        var profile = profiles.Find(profileId)
                      ?? throw new InvalidOperationException($"profile '{profileId}' is missing");

        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "screenflow-tests",
            "resolution-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(directory);
        var dbPath = System.IO.Path.Combine(directory, profileId + ".db");

        var stages = withDomain
            ? new IDomainStage[] { new ScreenFlow.UnityPos.UnityPosDomain(), new ScreenFlow.Core.Graph.ScreenGraphBuilder() }
            : Array.Empty<IDomainStage>();

        var report = new IndexBuilder(stages).Build(
            MiniPos,
            profile,
            domain,
            dbPath,
            configDirectory,
            () => new CSharpExtractor(domain));

        return new FixtureIndex(IndexDb.Open(dbPath), report, domain, dbPath);
    }

    /// <summary>
    /// Finds a symbol by its fully qualified name. Never by simple name: 03 §1 records 125 duplicate
    /// simple type names in the real project, so a short-name lookup is a different symbol's facts.
    /// </summary>
    public Ir.SymbolRec Symbol(string kind, string fqName)
    {
        var matches = Reader.AllSymbols()
            .Where(s => s.Kind == kind && s.FqName == fqName && s.IsSource)
            .ToList();

        if (matches.Count != 1)
        {
            throw new InvalidOperationException(
                $"expected exactly one source {kind} named '{fqName}', found {matches.Count}");
        }

        return matches[0];
    }

    public void Dispose() => Db.Dispose();

    private static string FindRepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(System.IO.Path.Combine(directory.FullName, "ScreenFlow.sln"))) return directory.FullName;
        }

        throw new InvalidOperationException($"Could not find ScreenFlow.sln above {AppContext.BaseDirectory}.");
    }
}
