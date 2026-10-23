using ScreenFlow.Core.Config;
using ScreenFlow.Core.Extraction;
using ScreenFlow.Core.Ir;
using ScreenFlow.Core.Tests.ConfigTests;
using Xunit;

namespace ScreenFlow.Core.Tests.IrTests;

/// <summary>
/// Exercises <see cref="ExternalProcessExtractor"/> against <c>tests/IrEmitterHelper</c>, a console
/// program that writes raw NDJSON. The helper does not reference the core, which is the point: a
/// future Java or Python extractor will have nothing but the wire contract either.
/// </summary>
public sealed class ExternalProcessExtractorTests
{
    [Fact]
    [Trait("Task", "T02")]
    public async Task Ir_ExternalProcess_ReadsNdjson()
    {
        var records = await CollectAsync();

        // Two valid records, then the stderr line passed through as a diagnostic.
        Assert.Equal(3, records.Count);

        var meta = Assert.IsType<MetaRec>(records[0]);
        Assert.Equal(IrValidator.SupportedIrVersion, meta.IrVersion);
        Assert.Equal("ir-emitter-helper", meta.Extractor);
        Assert.Equal(Derivations.Native, meta.Derivation);

        var symbol = Assert.IsType<SymbolRec>(records[1]);
        Assert.Equal("Fixture.Sample", symbol.FqName);
        Assert.Equal(SymbolKinds.Type, symbol.Kind);
        Assert.True(symbol.IsSource);
        Assert.Equal("Assets/Fixture/Sample.cs", symbol.File);

        // stderr is surfaced, not swallowed.
        var diagnostic = Assert.IsType<DiagRec>(records[2]);
        Assert.Equal(ExternalProcessExtractor.StderrDiagnosticCode, diagnostic.Code);
        Assert.Contains("emitted 2 record(s)", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Task", "T02")]
    public async Task Ir_ExternalProcess_RejectsInvalidRecordWithLineNumber()
    {
        var error = await Assert.ThrowsAsync<IrSchemaException>(
            () => CollectAsync(HelperArguments.Invalid));

        // The invalid record is the second line, and the number has to be reported so the offending
        // record can be found in a stream of millions.
        Assert.Equal(2, error.LineNumber);
        Assert.Contains("line 2", error.Message, StringComparison.Ordinal);
        Assert.Contains(error.Problems, p => p.Contains("expected prefix 's_'", StringComparison.Ordinal));
        Assert.Contains(error.Problems, p => p.Contains("symbol.kind is 'widget'", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Task", "T02")]
    public async Task Ir_ExternalProcess_RejectsMalformedJsonWithLineNumber()
    {
        var error = await Assert.ThrowsAsync<IrSchemaException>(
            () => CollectAsync(HelperArguments.Malformed));

        Assert.Equal(2, error.LineNumber);
        Assert.Contains("not valid JSON", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Task", "T02")]
    public async Task Ir_ExternalProcess_FailsOnNonZeroExitCode()
    {
        var error = await Assert.ThrowsAsync<IrSchemaException>(
            () => CollectAsync(HelperArguments.Fail));

        Assert.Contains("exited with code 3", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Task", "T02")]
    public async Task Ir_ExternalProcess_RejectsUnsupportedIrVersion()
    {
        var extractor = new ExternalProcessExtractor(new ExternalExtractorOptions
        {
            ExtractorId = "from-the-future",
            FileName = "dotnet",
            IrVersion = "2"
        });

        // Refused before the process is started: a version the core cannot read is a build failure,
        // not something to discover halfway through a stream.
        var error = await Assert.ThrowsAsync<IrSchemaException>(
            () => extractor.ExtractAsync(Request(), CancellationToken.None));
        Assert.Contains("declares IR version '2'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Task", "T02")]
    public async Task Ir_ExternalProcess_ReportsAnUnstartableCommand()
    {
        var extractor = new ExternalProcessExtractor(new ExternalExtractorOptions
        {
            ExtractorId = "missing",
            FileName = "screenflow-no-such-extractor-" + Guid.NewGuid().ToString("n")
        });

        var result = await extractor.ExtractAsync(Request(), CancellationToken.None);
        var error = await Assert.ThrowsAsync<IrSchemaException>(async () =>
        {
            await foreach (var _ in result.Records) { }
        });
        Assert.Contains("could not be started", error.Message, StringComparison.Ordinal);
    }

    private static class HelperArguments
    {
        public const string Invalid = IrEmitterHelperLocator.InvalidFlag;
        public const string Malformed = IrEmitterHelperLocator.MalformedFlag;
        public const string Fail = IrEmitterHelperLocator.FailFlag;
    }

    private static async Task<List<IrRecord>> CollectAsync(params string[] helperArguments)
    {
        var extractor = new ExternalProcessExtractor(new ExternalExtractorOptions
        {
            ExtractorId = "ir-emitter-helper",
            FileName = "dotnet",
            Arguments = new[] { IrEmitterHelperLocator.AssemblyPath }.Concat(helperArguments).ToArray(),
            WorkingDirectory = RepoLayout.RepoRoot
        });

        var result = await extractor.ExtractAsync(Request(), CancellationToken.None);

        var records = new List<IrRecord>();
        await foreach (var record in result.Records) records.Add(record);
        return records;
    }

    private static ExtractionRequest Request() => new(
        RepoLayout.RepoRoot,
        ConfigLoader.LoadProfiles(RepoLayout.ProfilesPath).Find("fixture")!,
        new Progress<ProgressEvent>());
}

/// <summary>
/// Finds the built helper assembly. The flags are duplicated as constants rather than referenced,
/// because the test project deliberately does not link the helper.
/// </summary>
internal static class IrEmitterHelperLocator
{
    public const string InvalidFlag = "--invalid";
    public const string MalformedFlag = "--malformed";
    public const string FailFlag = "--fail";

    public static string AssemblyPath { get; } = Locate();

    private static string Locate()
    {
        var binRoot = Path.Combine(RepoLayout.RepoRoot, "tests", "IrEmitterHelper", "bin");
        if (!Directory.Exists(binRoot))
            throw new InvalidOperationException($"IrEmitterHelper has not been built; {binRoot} does not exist.");

        var candidates = Directory
            .EnumerateFiles(binRoot, "IrEmitterHelper.dll", SearchOption.AllDirectories)
            .ToList();
        if (candidates.Count == 0)
            throw new InvalidOperationException($"No IrEmitterHelper.dll found under {binRoot}.");

        // Prefer the build configuration this test assembly itself was built in.
        var configuration = AppContext.BaseDirectory.Contains(
            Path.DirectorySeparatorChar + "Release" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? "Release"
            : "Debug";

        return candidates.FirstOrDefault(c => c.Contains(
                   Path.DirectorySeparatorChar + configuration + Path.DirectorySeparatorChar, StringComparison.Ordinal))
               ?? candidates[0];
    }
}
