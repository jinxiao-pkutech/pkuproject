using ScreenFlow.Core.Config;
using ScreenFlow.Core.Extraction;
using ScreenFlow.Core.Ir;
using ScreenFlow.CSharp;
using ScreenFlow.CSharp.Tests.Fixture;
using Xunit;

namespace ScreenFlow.CSharp.Tests.Walkers;

/// <summary>
/// The MiniPos fixture run through the real extractor once per profile. Compiling 66 files is the
/// expensive part of these tests, so the result is shared rather than rebuilt per test.
/// </summary>
internal sealed class ExtractedFixture
{
    private static readonly Lazy<ExtractedFixture> LazyEditor =
        new(() => Create("fixture"), LazyThreadSafetyMode.ExecutionAndPublication);

    public static ExtractedFixture Editor => LazyEditor.Value;

    private ExtractedFixture(string profileId, IReadOnlyList<IrRecord> records)
    {
        ProfileId = profileId;
        Records = records;
    }

    public string ProfileId { get; }

    public IReadOnlyList<IrRecord> Records { get; }

    public IReadOnlyList<SymbolRec> Symbols => Records.OfType<SymbolRec>().ToList();

    public IReadOnlyList<ImplRec> Impls => Records.OfType<ImplRec>().ToList();

    public IReadOnlyList<MarkerRec> Markers => Records.OfType<MarkerRec>().ToList();

    public IReadOnlyList<CallsiteRec> Callsites => Records.OfType<CallsiteRec>().ToList();

    public IReadOnlyList<AssignRec> Assigns => Records.OfType<AssignRec>().ToList();

    public IReadOnlyList<CreationRec> Creations => Records.OfType<CreationRec>().ToList();

    public IReadOnlyList<CfgRec> Cfgs => Records.OfType<CfgRec>().ToList();

    public IReadOnlyList<DiagRec> Diagnostics => Records.OfType<DiagRec>().ToList();

    /// <summary>Runs the extractor from scratch; used to check that ids do not move between runs.</summary>
    public static ExtractedFixture Create(string profileId)
    {
        var configDirectory = Path.Combine(FixtureLayout.RepoRoot, "config");
        var (profiles, domain) = ConfigLoader.LoadAll(configDirectory);

        var profile = profiles.Find(profileId)
                      ?? throw new InvalidOperationException($"profile '{profileId}' is missing");

        var request = new ExtractionRequest(FixtureLayout.MiniPos, profile, new Progress<ProgressEvent>());
        var records = new CSharpExtractor(domain).Collect(request);

        return new ExtractedFixture(profileId, records);
    }

    /// <summary>
    /// Locates a symbol by its fully qualified name. Never by simple name: the real project has 125
    /// duplicate simple type names, so a short-name lookup would be a different symbol's facts
    /// (CLAUDE.md §4).
    /// </summary>
    public SymbolRec Symbol(string kind, string fqName)
    {
        var matches = Symbols
            .Where(s => s.Kind == kind && s.FqName == fqName && s.IsSource)
            .ToList();

        Assert.True(
            matches.Count == 1,
            $"expected exactly one source {kind} named '{fqName}', found {matches.Count}");

        return matches[0];
    }
}
