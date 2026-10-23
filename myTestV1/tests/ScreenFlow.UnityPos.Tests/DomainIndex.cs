using System.Text.Json;
using System.Text.Json.Nodes;
using ScreenFlow.Core.Config;
using ScreenFlow.Core.Ir;
using ScreenFlow.Core.Pipeline;
using ScreenFlow.Core.Storage;
using ScreenFlow.CSharp;

namespace ScreenFlow.UnityPos.Tests;

/// <summary>
/// The MiniPos fixture built with the POS domain stage, so the adapters are tested on the shapes 03
/// documents rather than on a hand-built index.
/// </summary>
internal sealed class DomainIndex : IDisposable
{
    private static readonly Lazy<DomainIndex> LazyEditor =
        new(() => Build("fixture"), LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<DomainIndex> LazyAndroid =
        new(() => Build("fixture-android"), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The editor profile, where the <c>#if UNITY_EDITOR</c> region is active (E-11).</summary>
    public static DomainIndex Editor => LazyEditor.Value;

    public static DomainIndex Android => LazyAndroid.Value;

    private DomainIndex(IndexDb db, BuildReport report)
    {
        Db = db;
        Report = report;
        Reader = new IndexReader(db);
    }

    public IndexDb Db { get; }

    public IndexReader Reader { get; }

    public BuildReport Report { get; }

    public static string RepoRoot { get; } = FindRepoRoot();

    public static string MiniPos => Path.Combine(RepoRoot, "tests", "fixtures", "MiniPos");

    public static string ConfigDirectory => Path.Combine(RepoRoot, "config");

    /// <summary>Builds the fixture with the real pipeline; <paramref name="configDirectory"/> may be a copy.</summary>
    public static DomainIndex Build(string profileId, string? configDirectory = null)
    {
        var directory = configDirectory ?? ConfigDirectory;
        var (profiles, domain) = ConfigLoader.LoadAll(directory);

        var profile = profiles.Find(profileId)
                      ?? throw new InvalidOperationException($"profile '{profileId}' is missing");

        var output = Path.Combine(
            Path.GetTempPath(), "screenflow-tests", "domain-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(output);
        var dbPath = Path.Combine(output, profileId + ".db");

        var report = new IndexBuilder(new IDomainStage[] { new UnityPosDomain() }).Build(
            MiniPos,
            profile,
            domain,
            dbPath,
            directory,
            () => new CSharpExtractor(domain));

        return new DomainIndex(IndexDb.Open(dbPath), report);
    }

    /// <summary>
    /// A copy of <c>config/</c> with one JSON path changed, for the cases where an anchor has to
    /// fail. The real configuration is never modified.
    /// </summary>
    public static string ConfigCopyWith(Action<JsonNode> edit)
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "screenflow-tests", "config-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(directory);

        File.Copy(
            Path.Combine(ConfigDirectory, ConfigLoader.ProfilesFileName),
            Path.Combine(directory, ConfigLoader.ProfilesFileName));

        var domain = JsonNode.Parse(File.ReadAllText(Path.Combine(ConfigDirectory, ConfigLoader.DomainFileName)))
                     ?? throw new InvalidOperationException("unitypos.json is empty");

        edit(domain);

        File.WriteAllText(
            Path.Combine(directory, ConfigLoader.DomainFileName),
            domain.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        return directory;
    }

    public IReadOnlyList<ScreenRow> Screens() => Reader.Screens();

    public IReadOnlyList<DomainEdgeRow> Edges(string kind) => Reader.DomainEdges(kind);

    /// <summary>The screen ids a navigate edge names.</summary>
    public static IReadOnlyList<string> ScreenIdsOf(DomainEdgeRow edge)
    {
        var json = JsonNode.Parse(edge.Json)!;
        return json["screenIds"]!.AsArray().Select(node => node!.GetValue<string>()).ToList();
    }

    public SymbolRec Symbol(string kind, string fqName)
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
            if (File.Exists(Path.Combine(directory.FullName, "ScreenFlow.sln"))) return directory.FullName;
        }

        throw new InvalidOperationException($"Could not find ScreenFlow.sln above {AppContext.BaseDirectory}.");
    }
}
