using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using Xunit.Abstractions;

namespace ScreenFlow.CSharp.Tests.Fixture;

/// <summary>
/// Compiles the MiniPos fixture with Roslyn directly (05 §3.1: the fixture must compile with zero
/// errors).
/// <para>
/// This test deliberately does not use ScreenFlow's own compilation code: it is the fixture that is
/// under test here, so going through T06/T07 would make a fixture mistake and an extractor mistake
/// indistinguishable.
/// </para>
/// </summary>
public sealed class FixtureCompilesTest
{
    private readonly ITestOutputHelper _output;

    public FixtureCompilesTest(ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("Task", "T04")]
    public void Fixture_CompilesWithoutErrors_Editor()
        => AssertCompiles("UNITY_EDITOR", "UNITY_2022_3_68", "DEBUG");

    [Fact]
    [Trait("Task", "T04")]
    public void Fixture_CompilesWithoutErrors_Android()
        => AssertCompiles("UNITY_ANDROID", "UNITY_2022_3_68");

    /// <summary>
    /// The <c>#if TeamUI</c> block must stay out of both profiles: it is never defined in any real
    /// configuration (03 §21), and its content would otherwise produce a screen transition that
    /// does not exist (E-10).
    /// </summary>
    [Fact]
    [Trait("Task", "T04")]
    public void Fixture_TeamUiBlockIsNotCompiledInEitherProfile()
    {
        foreach (var symbols in new[]
                 {
                     new[] { "UNITY_EDITOR", "UNITY_2022_3_68", "DEBUG" },
                     new[] { "UNITY_ANDROID", "UNITY_2022_3_68" }
                 })
        {
            var compilation = Compile(symbols);
            var trees = compilation.SyntaxTrees
                .Where(t => t.FilePath.EndsWith("SSAW0001_Presenter.cs", StringComparison.Ordinal))
                .ToList();

            Assert.Single(trees);
            var text = trees[0].GetRoot().ToFullString();

            // The directive itself is still trivia; what matters is that the guarded statement is
            // not part of the compiled tree.
            var hasActiveSsaw0002Navigation = trees[0].GetRoot()
                .DescendantNodes()
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>()
                .Any(i => i.ToString().Contains("ScreenID.SSAW0002", StringComparison.Ordinal));

            Assert.False(
                hasActiveSsaw0002Navigation,
                $"SSAW0002 navigation must not be active under [{string.Join(", ", symbols)}].");
            Assert.Contains("TeamUI", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    [Trait("Task", "T04")]
    public void Fixture_UnityEditorBlockIsProfileDependent()
    {
        // SCEW0001 is reachable only from a #if UNITY_EDITOR block (E-11).
        Assert.True(HasActiveNavigationTo("SCEW0001", "UNITY_EDITOR", "UNITY_2022_3_68", "DEBUG"));
        Assert.False(HasActiveNavigationTo("SCEW0001", "UNITY_ANDROID", "UNITY_2022_3_68"));
    }

    private bool HasActiveNavigationTo(string screenField, params string[] symbols)
        => Compile(symbols).SyntaxTrees
            .SelectMany(t => t.GetRoot().DescendantNodes()
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>())
            .Any(i => i.ToString().Contains("ScreenID." + screenField, StringComparison.Ordinal));

    private void AssertCompiles(params string[] preprocessorSymbols)
    {
        var compilation = Compile(preprocessorSymbols);
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        foreach (var error in errors.Take(25))
            _output.WriteLine(error.ToString());

        Assert.True(
            errors.Count == 0,
            $"The fixture must compile with zero errors under [{string.Join(", ", preprocessorSymbols)}]; " +
            $"found {errors.Count}. First: {(errors.Count > 0 ? errors[0].ToString() : "(none)")}");
    }

    private static CSharpCompilation Compile(IEnumerable<string> preprocessorSymbols)
    {
        var parseOptions = new CSharpParseOptions(
            LanguageVersion.CSharp9,
            DocumentationMode.None,
            preprocessorSymbols: preprocessorSymbols);

        var trees = FixtureLayout.SourceFiles()
            .Select(path => CSharpSyntaxTree.ParseText(
                SourceText(path), parseOptions, path: FixtureLayout.Relative(path)))
            .ToList();

        return CSharpCompilation.Create(
            "Assembly-CSharp",
            trees,
            FixtureLayout.HostFrameworkReferences(),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true,
                nullableContextOptions: NullableContextOptions.Disable));
    }

    /// <summary>
    /// Reads a fixture source with the same encoding rules the extractor uses: UTF-8 unless the
    /// bytes are not valid UTF-8, then CP932 (03 §1, T05 §4). KEY01 is the file that forces this
    /// path, and decoding it wrongly would corrupt every Japanese literal in it.
    /// </summary>
    private static Microsoft.CodeAnalysis.Text.SourceText SourceText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var encoding = FixtureLayout.DetectEncoding(bytes);
        return Microsoft.CodeAnalysis.Text.SourceText.From(encoding.GetString(bytes), encoding);
    }
}

/// <summary>Locates the fixture and the host reference assemblies.</summary>
internal static class FixtureLayout
{
    /// <summary>Saved as CP932 (05 §3.4), so it is the file that exercises the encoding fallback.</summary>
    public const string Cp932FileName = "KEY01_KeyCalculator.cs";

    public static string Cp932FilePath => Path.Combine(AssetsRoot, "Common", "Legacy", Cp932FileName);

    public static string RepoRoot { get; } = FindRepoRoot();

    public static string MiniPos => Path.Combine(RepoRoot, "tests", "fixtures", "MiniPos");

    public static string AssetsRoot => Path.Combine(MiniPos, "Assets");

    /// <summary>
    /// Every fixture source, KEY01 included: once T05 has generated it, the compile test has to
    /// cover it too, or the one file with a non-UTF-8 encoding would never be compiled.
    /// </summary>
    public static IReadOnlyList<string> SourceFiles()
        => Directory.EnumerateFiles(AssetsRoot, "*.cs", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// UTF-8 with BOM, then strict UTF-8, then CP932 (03 §1). Strict decoding is what makes the
    /// fallback detectable: a lenient decoder would turn invalid bytes into replacement characters
    /// and shift every column position after the first bad byte.
    /// </summary>
    public static Encoding DetectEncoding(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

        try
        {
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
            return new UTF8Encoding(false);
        }
        catch (DecoderFallbackException)
        {
            return Cp932;
        }
    }

    /// <summary>
    /// CP932. The provider has to be registered explicitly: .NET ships the assembly in the shared
    /// framework but does not load the code pages by default.
    /// </summary>
    public static Encoding Cp932
    {
        get
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(932);
        }
    }

    public static string Relative(string absolute)
    {
        var root = Path.GetFullPath(MiniPos).Replace('\\', '/').TrimEnd('/');
        var path = Path.GetFullPath(absolute).Replace('\\', '/');
        return path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)
            ? path[(root.Length + 1)..]
            : path;
    }

    /// <summary>
    /// The host runtime's reference assemblies. Permitted for a fixture profile only
    /// (02 §4.1, <c>allowHostFrameworkReferences</c>): on a real project a missing reference has to
    /// stay a diagnostic.
    /// </summary>
    public static ImmutableArray<MetadataReference> HostFrameworkReferences()
    {
        var trusted = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Where(File.Exists)
            .ToList();

        if (trusted.Count == 0)
        {
            throw new InvalidOperationException(
                "TRUSTED_PLATFORM_ASSEMBLIES is empty, so the fixture cannot be compiled against the host BCL.");
        }

        return trusted.Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToImmutableArray();
    }

    private static string FindRepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ScreenFlow.sln"))) return directory.FullName;
        }

        throw new InvalidOperationException($"Could not find ScreenFlow.sln above {AppContext.BaseDirectory}.");
    }
}
