using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ScreenFlow.Core.Config;
using ScreenFlow.Core.Ir;
using ScreenFlow.CSharp.Compilation;
using ScreenFlow.CSharp.Tests.Fixture;
using Xunit;
using Xunit.Abstractions;

namespace ScreenFlow.CSharp.Tests.Compilation;

/// <summary>
/// Compilation construction (02 §6.4), driven through the real T06 discovery path so that a
/// mistake in either step surfaces here. Covers E-04.
/// </summary>
public sealed class CompilationBuilderTests
{
    private readonly ITestOutputHelper _output;

    public CompilationBuilderTests(ITestOutputHelper output) => _output = output;

    private static ProfileConfig Profile(string id)
        => ConfigLoader.LoadProfiles(Path.Combine(FixtureLayout.RepoRoot, "config", "profiles.json")).Find(id)
           ?? throw new InvalidOperationException($"profile '{id}' is missing");

    private static (CompilationSet Set, PreflightResult Preflight) BuildFixture(string profileId)
    {
        var profile = Profile(profileId);
        var preflight = Preflight.Run(FixtureLayout.MiniPos, profile);
        var set = new CompilationBuilder().Build(FixtureLayout.MiniPos, preflight, profile);
        return (set, preflight);
    }

    [Fact]
    [Trait("Task", "T07")]
    public void Compile_Fixture_NoErrors()
    {
        var (set, _) = BuildFixture("fixture");

        var assembly = Assert.Single(set.Assemblies);
        Assert.Equal("Assembly-CSharp", assembly.Name);
        Assert.True(assembly.ExtractIr);

        var errors = assembly.Compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        foreach (var error in errors.Take(20)) _output.WriteLine(error.ToString());
        Assert.True(errors.Count == 0, $"{errors.Count} compile error(s); first: {errors.FirstOrDefault()}");

        Assert.Equal(0, set.ErrorCounts["Assembly-CSharp"]);
        Assert.DoesNotContain(set.Diagnostics, d => d.Code == CompilationBuilder.CompileDiagnosticCode);

        // Every source became a syntax tree with its project-relative path, which is what evidence
        // paths are built from (02 §11.3).
        Assert.Equal(66, assembly.Compilation.SyntaxTrees.Count());
        Assert.All(assembly.TreePaths.Values, p => Assert.StartsWith("Assets/", p, StringComparison.Ordinal));
        Assert.All(assembly.TreePaths.Values, p => Assert.DoesNotContain('\\', p));

        // The CP932 file was decoded with CP932, not as UTF-8 with replacement characters.
        Assert.Equal(Encodings.Cp932, assembly.Encodings["Assets/Common/Legacy/KEY01_KeyCalculator.cs"]);
        var key01 = assembly.Compilation.SyntaxTrees
            .Single(t => t.FilePath.EndsWith(FixtureLayout.Cp932FileName, StringComparison.Ordinal));
        Assert.Contains("合計金額", key01.GetText().ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain('\uFFFD', key01.GetText().ToString());
    }

    [Fact]
    [Trait("Task", "T07")]
    public void Compile_FixtureAndroid_DefinesApplied()
    {
        var (editor, _) = BuildFixture("fixture");
        var (android, _) = BuildFixture("fixture-android");

        Assert.Empty(android.Assemblies.Single().Compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error));

        // SCEW0001 sits inside a #if UNITY_EDITOR block (E-11), so it is in the editor profile's
        // active region and absent from the Android one.
        Assert.True(HasActiveCall(editor, "SSAW0001_Presenter.cs", "ScreenID.SCEW0001"));
        Assert.False(HasActiveCall(android, "SSAW0001_Presenter.cs", "ScreenID.SCEW0001"));

        // TeamUI is removed by both profiles, so SSAW0002 is never in an active region (E-10).
        Assert.False(HasActiveCall(editor, "SSAW0001_Presenter.cs", "ScreenID.SSAW0002"));
        Assert.False(HasActiveCall(android, "SSAW0001_Presenter.cs", "ScreenID.SSAW0002"));

        var androidDefines = android.Assemblies.Single().Input.Defines;
        Assert.Contains("UNITY_ANDROID", androidDefines);
        Assert.DoesNotContain("UNITY_EDITOR", androidDefines);
        Assert.DoesNotContain("TeamUI", androidDefines);
    }

    /// <summary>
    /// A call is "active" when it is part of the compiled tree. A directive-excluded region is
    /// parsed as disabled text, so its statements never appear as nodes.
    /// </summary>
    private static bool HasActiveCall(CompilationSet set, string fileSuffix, string callFragment)
        => set.Assemblies
            .SelectMany(a => a.Compilation.SyntaxTrees)
            .Where(t => t.FilePath.EndsWith(fileSuffix, StringComparison.Ordinal))
            .SelectMany(t => t.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            .Any(i => i.ToString().Contains(callFragment, StringComparison.Ordinal));

    [Fact]
    [Trait("Task", "T07")]
    public void Compile_HostRefsOnlyWhenAllowed()
    {
        // The fixture's response file references nothing but a missing dll, so the only way it
        // compiles is the host framework fallback, which the fixture profile allows.
        var (allowed, _) = BuildFixture("fixture");
        Assert.Contains(allowed.Diagnostics, d => d.Code == "I_HOST_FRAMEWORK_REFERENCES");
        Assert.Empty(allowed.Assemblies.Single().Compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error));

        // The same fixture under a profile that forbids it must not get the fallback. The profile is
        // renamed away from the fixture prefix, which is exactly what the config loader enforces.
        var strict = Profile("fixture") with { Id = "windows-editor", AllowHostFrameworkReferences = false };
        var preflight = Preflight.Run(FixtureLayout.MiniPos, strict);
        var builder = new CompilationBuilder();
        var set = builder.Build(FixtureLayout.MiniPos, preflight, strict);

        Assert.DoesNotContain(set.Diagnostics, d => d.Code == "I_HOST_FRAMEWORK_REFERENCES");

        // Without a base class library almost nothing binds. That is reported, and it is reported as
        // a diagnostic rather than by pretending the code is broken.
        Assert.Contains(set.Diagnostics, d =>
            d.Code == Preflight.ReferenceMissing &&
            d.Message.Contains("references no base class library", StringComparison.Ordinal));
        Assert.True(set.ErrorCounts["Assembly-CSharp"] > 0);
        Assert.Contains(set.Diagnostics, d => d.Code == CompilationBuilder.CompileDiagnosticCode);
    }

    [Fact]
    [Trait("Task", "T07")]
    public void Compile_SourceAssemblyReferenceUsesCompilationReference()
    {
        using var workspace = new TempProject();

        workspace.Write("Assets/Lib/Shared.cs", "namespace Lib { public class Shared { public int Value => 7; } }");
        workspace.Write("Assets/App/Consumer.cs",
            "namespace App { public class Consumer { public int Read() => new Lib.Shared().Value; } }");

        workspace.Write("Library/Bee/artifacts/x.dag/Shared.rsp", string.Join("\n",
            "-out:\"bin/Shared.dll\"", "-define:UNITY_EDITOR", "Assets/Lib/Shared.cs", ""));

        // Consumer references Shared by file name, which is how a source-assembly dependency looks
        // in a Unity response file (02 §6.4).
        workspace.Write("Library/Bee/artifacts/x.dag/Consumer.rsp", string.Join("\n",
            "-out:\"bin/Consumer.dll\"", "-define:UNITY_EDITOR", "-r:\"bin/Shared.dll\"",
            "Assets/App/Consumer.cs", ""));

        var profile = Profile("fixture");
        var preflight = Preflight.Run(workspace.Root, profile);
        var set = new CompilationBuilder().Build(workspace.Root, preflight, profile);

        // Dependency first.
        Assert.Equal(new[] { "Shared", "Consumer" }, set.Assemblies.Select(a => a.Name).ToArray());

        var consumer = set.ByName("Consumer")!;
        Assert.Contains(consumer.Compilation.References, r => r is CompilationReference);

        // The cross-assembly call bound, which only works if the reference is the real compilation.
        Assert.Empty(consumer.Compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    [Trait("Task", "T07")]
    public void Compile_ExcludedAssemblyIsStillAReference()
    {
        using var workspace = new TempProject();

        workspace.Write("Assets/Lib/Shared.cs", "namespace Lib { public class Shared { public int Value => 7; } }");
        workspace.Write("Assets/App/Consumer.cs",
            "namespace App { public class Consumer { public int Read() => new Lib.Shared().Value; } }");

        workspace.Write("Library/Bee/artifacts/x.dag/Shared.rsp", string.Join("\n",
            "-out:\"bin/Shared.dll\"", "-define:UNITY_EDITOR", "Assets/Lib/Shared.cs", ""));
        workspace.Write("Library/Bee/artifacts/x.dag/Assembly-CSharp.rsp", string.Join("\n",
            "-out:\"bin/Assembly-CSharp.dll\"", "-define:UNITY_EDITOR", "-r:\"bin/Shared.dll\"",
            "Assets/App/Consumer.cs", ""));

        // android-onsite extracts Assembly-CSharp only (02 §4.1).
        var profile = Profile("android-onsite") with
        {
            Id = "fixture-include-test",
            AllowHostFrameworkReferences = true,
            Rsp = Profile("android-onsite").Rsp with { SelectPlatform = RspConfig.Platforms.Any }
        };

        var preflight = Preflight.Run(workspace.Root, profile);

        Assert.Equal(2, preflight.Inputs.Count);
        var extracted = Assert.Single(preflight.ExtractedInputs);
        Assert.Equal("Assembly-CSharp", extracted.Name);
        Assert.Contains(preflight.Diagnostics, d => d.Code == Preflight.AssemblyNotIncluded);

        // Shared is compiled anyway, so Consumer's cross-assembly call still binds; it just
        // contributes no IR of its own.
        var set = new CompilationBuilder().Build(workspace.Root, preflight, profile);
        Assert.Equal(2, set.Assemblies.Count);
        Assert.Single(set.ToExtract);
        Assert.Empty(set.ByName("Assembly-CSharp")!.Compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error));

        // The files table covers the analysed assembly only.
        var files = Preflight.FileRecords(workspace.Root, preflight);
        Assert.Equal(new[] { "Assets/App/Consumer.cs" }, files.Select(f => f.Path).ToArray());
    }

    [Fact]
    [Trait("Task", "T07")]
    public void Compile_AssemblyCycleIsReportedNotFatal()
    {
        using var workspace = new TempProject();

        workspace.Write("Assets/A/A.cs", "namespace A { public class Ta { } }");
        workspace.Write("Assets/B/B.cs", "namespace B { public class Tb { } }");

        workspace.Write("Library/Bee/artifacts/x.dag/Aa.rsp", string.Join("\n",
            "-out:\"bin/Aa.dll\"", "-define:UNITY_EDITOR", "-r:\"bin/Bb.dll\"", "Assets/A/A.cs", ""));
        workspace.Write("Library/Bee/artifacts/x.dag/Bb.rsp", string.Join("\n",
            "-out:\"bin/Bb.dll\"", "-define:UNITY_EDITOR", "-r:\"bin/Aa.dll\"", "Assets/B/B.cs", ""));

        var profile = Profile("fixture");
        var preflight = Preflight.Run(workspace.Root, profile);
        var set = new CompilationBuilder().Build(workspace.Root, preflight, profile);

        // Both assemblies are still built; the cycle is reported rather than failing the run.
        Assert.Equal(2, set.Assemblies.Count);
        Assert.Contains(set.Diagnostics, d => d.Code == "W_ASSEMBLY_CYCLE");
    }

    [Fact]
    [Trait("Task", "T07")]
    public void Compile_ParseOptionsFollowTheResponseFile()
    {
        var (set, _) = BuildFixture("fixture");
        var assembly = set.Assemblies.Single();

        // common.rsp says -langversion:9.0, /unsafe and -nullable:disable.
        var options = (Microsoft.CodeAnalysis.CSharp.CSharpParseOptions)assembly.Compilation.SyntaxTrees.First().Options;
        Assert.Equal(Microsoft.CodeAnalysis.CSharp.LanguageVersion.CSharp9, options.LanguageVersion);
        Assert.Equal(DocumentationMode.None, options.DocumentationMode);

        var compilationOptions = (Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions)assembly.Compilation.Options;
        Assert.True(compilationOptions.AllowUnsafe);
        Assert.Equal(NullableContextOptions.Disable, compilationOptions.NullableContextOptions);
        Assert.Equal(OutputKind.DynamicallyLinkedLibrary, compilationOptions.OutputKind);
        Assert.True(compilationOptions.Deterministic);
    }
}
