using System.Text;
using ScreenFlow.Core.Config;
using ScreenFlow.Core.Ir;
using ScreenFlow.CSharp.Compilation;
using ScreenFlow.CSharp.Tests.Fixture;
using Xunit;

namespace ScreenFlow.CSharp.Tests.Compilation;

/// <summary>
/// Response-file discovery, parsing, preflight and encoding detection (02 §6.1–§6.3), checked
/// against the MiniPos fixture. Covers E-01, E-02 and E-03.
/// </summary>
public sealed class RspTests
{
    private static ProfilesFile Profiles => ConfigLoader.LoadProfiles(
        Path.Combine(FixtureLayout.RepoRoot, "config", "profiles.json"));

    private static ProfileConfig Profile(string id) => Profiles.Find(id)
        ?? throw new InvalidOperationException($"profile '{id}' is missing from config/profiles.json");

    [Fact]
    [Trait("Task", "T06")]
    public void Rsp_Fixture_SelectsEditorAndMergesNested()
    {
        var result = Preflight.Run(FixtureLayout.MiniPos, Profile("fixture"));

        var input = Assert.Single(result.Inputs);
        Assert.Equal("Assembly-CSharp", input.Name);

        // The editor response file is the one chosen, and the mvfrm decoy is excluded by name.
        Assert.Contains("ed01.dag", input.RspPath, StringComparison.Ordinal);
        Assert.Contains(result.Diagnostics, d =>
            d.Code == Preflight.RspExcluded && d.Message.Contains("mvfrm", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Inputs, i => i.RspPath.Contains("mvfrm", StringComparison.Ordinal));

        // common.rsp is pulled in with @, so its options have to appear in the result.
        Assert.Equal("9.0", input.LangVersion);
        Assert.True(input.AllowUnsafe);
        Assert.Equal("disable", input.Nullable);

        Assert.Equal(RspPlatforms.Editor, input.Platform);
        Assert.Equal(RspPlatforms.Editor, result.PlatformDetected);
        Assert.Equal(Derivations.Native, result.Derivation);
        Assert.Equal("2022.3.68f1", result.UnityDeclared);
        Assert.Equal("ed01.dag", result.UnityCache);

        // Every fixture source is listed, quoted or not.
        Assert.Equal(66, input.Sources.Count);
        Assert.Contains(input.Sources, s => s.EndsWith("KEY01_KeyCalculator.cs", StringComparison.Ordinal));
        Assert.Contains(input.Sources, s => s.EndsWith("ScreenID.cs", StringComparison.Ordinal));

        // -target:library is recognised rather than ignored.
        Assert.DoesNotContain(input.IgnoredOptions, o => o.Contains("target", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    [Trait("Task", "T06")]
    public void Rsp_FixtureAndroid_SelectsAndroid()
    {
        var result = Preflight.Run(FixtureLayout.MiniPos, Profile("fixture-android"));

        var input = Assert.Single(result.Inputs);
        Assert.Contains("an01.dag", input.RspPath, StringComparison.Ordinal);
        Assert.Equal("an01.dag", result.UnityCache);

        Assert.Equal(RspPlatforms.Android, input.Platform);
        Assert.Equal(RspPlatforms.Android, result.PlatformDetected);

        // A real Android cache was found, so the snapshot is native and defines.add is not applied.
        Assert.Equal(Derivations.Native, result.Derivation);
        Assert.Contains("UNITY_ANDROID", input.Defines);
        Assert.DoesNotContain("UNITY_EDITOR", input.Defines);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == Preflight.PlatformDerived);
    }

    [Fact]
    [Trait("Task", "T06")]
    public void Rsp_Defines_TeamUIRemoved()
    {
        // The fixture's response files define TeamUI on purpose; every profile removes it, because
        // no real configuration defines it (03 §21).
        var rsp = RspParser.Parse(
            Path.Combine(FixtureLayout.MiniPos, "Library", "Bee", "artifacts", "ed01.dag", "Assembly-CSharp.rsp"),
            FixtureLayout.MiniPos);
        Assert.Contains("TeamUI", rsp.Defines);

        foreach (var id in new[] { "fixture", "fixture-android" })
        {
            var result = Preflight.Run(FixtureLayout.MiniPos, Profile(id));
            var input = Assert.Single(result.Inputs);
            Assert.DoesNotContain("TeamUI", input.Defines);
            Assert.DoesNotContain("TeamUIDummy", input.Defines);
        }
    }

    [Fact]
    [Trait("Task", "T06")]
    public void Rsp_DerivedProfile_AppliesDefineAdditions()
    {
        // android-onsite asks for an Android cache but excludes everything except Assembly-CSharp
        // and strips UNITY_EDITOR. Pointed at the fixture, whose editor cache is the only match for
        // its filter, it must report derived rather than silently pass as native.
        var androidOnsite = Profile("android-onsite") with
        {
            Rsp = Profile("android-onsite").Rsp with { SelectPlatform = RspConfig.Platforms.Android },
            Defines = new DefinesConfig
            {
                Add = new[] { "UNITY_ANDROID" },
                Remove = Profile("android-onsite").Defines.Remove
            }
        };

        // Restrict discovery to the editor cache so no Android response file is available.
        var editorOnly = Path.Combine(FixtureLayout.MiniPos, "Library", "Bee", "artifacts", "ed01.dag");
        var result = RspDiscovery.Discover(
            FixtureLayout.MiniPos,
            androidOnsite with { Rsp = androidOnsite.Rsp with { Root = editorOnly } });

        Assert.Equal(Derivations.Derived, result.Derivation);
        Assert.Contains(result.Diagnostics, d => d.Code == Preflight.PlatformDerived);

        var input = Assert.Single(result.Inputs);
        // defines.add applies only to a derived snapshot (02 §4.1).
        Assert.Contains("UNITY_ANDROID", input.Defines);
        Assert.DoesNotContain("UNITY_EDITOR", input.Defines);
    }

    [Fact]
    [Trait("Task", "T06")]
    public void Preflight_Fixture_ReportsMissingRefAndCp932()
    {
        var result = Preflight.Run(FixtureLayout.MiniPos, Profile("fixture"));

        var missingReferences = result.Diagnostics.Where(d => d.Code == Preflight.ReferenceMissing).ToList();
        var missingReference = Assert.Single(missingReferences);
        Assert.Contains("Nothing.dll", missingReference.Message, StringComparison.Ordinal);

        var nonUtf8Diagnostics = result.Diagnostics.Where(d => d.Code == Preflight.NonUtf8).ToList();
        var nonUtf8 = Assert.Single(nonUtf8Diagnostics);
        Assert.Contains(FixtureLayout.Cp932FileName, nonUtf8.Message, StringComparison.Ordinal);
        Assert.Equal("Assets/Common/Legacy/KEY01_KeyCalculator.cs", nonUtf8.File);

        // The fixture's response files list every source that exists, so nothing is missing or
        // unlisted. Both checks run; they simply find nothing.
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == Preflight.SourceMissing);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == Preflight.SourceNotInRsp);
    }

    [Fact]
    [Trait("Task", "T06")]
    public void Preflight_ReportsUnlistedAndMissingSources()
    {
        using var workspace = new TempProject(FixtureLayout.MiniPos);

        // A source on disk that the response file does not mention.
        var unlisted = Path.Combine(workspace.Root, "Assets", "Extra", "NotInRsp.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(unlisted)!);
        File.WriteAllText(unlisted, "namespace Extra { public class NotInRsp { } }");

        // A source the response file lists but which is gone.
        File.Delete(Path.Combine(workspace.Root, "Assets", "Stubs", "Mqtt.cs"));

        var result = Preflight.Run(workspace.Root, Profile("fixture"));

        var unlisted_ = result.Diagnostics.Where(d => d.Code == Preflight.SourceNotInRsp).ToList();
        var unlistedDiagnostic = Assert.Single(unlisted_);
        Assert.Equal("Assets/Extra/NotInRsp.cs", unlistedDiagnostic.File);

        var missing_ = result.Diagnostics.Where(d => d.Code == Preflight.SourceMissing).ToList();
        var missingDiagnostic = Assert.Single(missing_);
        Assert.Equal("Assets/Stubs/Mqtt.cs", missingDiagnostic.File);

        // A listed-but-absent source produces no file record rather than an empty one.
        var files = Preflight.FileRecords(workspace.Root, result);
        Assert.DoesNotContain(files, f => f.Path == "Assets/Stubs/Mqtt.cs");
    }

    [Fact]
    [Trait("Task", "T06")]
    public void Preflight_FileRecords_CarryEncodingAndHash()
    {
        var result = Preflight.Run(FixtureLayout.MiniPos, Profile("fixture"));
        var files = Preflight.FileRecords(FixtureLayout.MiniPos, result);

        Assert.Equal(66, files.Count);
        Assert.All(files, f => Assert.Equal("Assembly-CSharp", f.Assembly));
        Assert.All(files, f => Assert.False(string.IsNullOrEmpty(f.ContentHash)));
        Assert.All(files, f => Assert.True(f.LineCount > 0));

        var cp932Files = files.Where(f => f.Path.EndsWith(FixtureLayout.Cp932FileName, StringComparison.Ordinal)).ToList();
        var key01 = Assert.Single(cp932Files);
        Assert.Equal(Encodings.Cp932, key01.Encoding);

        Assert.All(
            files.Where(f => !f.Path.EndsWith(FixtureLayout.Cp932FileName, StringComparison.Ordinal)),
            f => Assert.Equal(Encodings.Utf8, f.Encoding));

        // Deterministic order, which is what the files table relies on.
        Assert.Equal(files.Select(f => f.Path).OrderBy(p => p, StringComparer.Ordinal), files.Select(f => f.Path));
    }

    [Fact]
    [Trait("Task", "T06")]
    public void Rsp_Parser_QuotesSlashOptionsAndNested()
    {
        using var workspace = new TempProject();

        workspace.Write("Library/Bee/artifacts/x.dag/main.rsp", string.Join("\n",
            "-target:library",
            "/out:\"Library/Bee/artifacts/x.dag/Sample.dll\"",
            "-define:ALPHA;BETA",
            "/define:GAMMA",
            "-D:DELTA",
            "-r:\"Library/Refs/One.dll\"",
            "-reference:alias=Library/Refs/Two.dll",
            "@Library/Bee/artifacts/x.dag/nested.rsp",
            "-deterministic",
            "/nowarn:0168",
            "-additionalfile:\"Assets/Extra.txt\"",
            "\"Assets/Quoted.cs\"",
            "Assets/Bare.CS",
            ""));

        workspace.Write("Library/Bee/artifacts/x.dag/nested.rsp", string.Join("\n",
            "-langversion:9.0",
            "/unsafe",
            "-nullable:disable",
            "Assets/FromNested.cs",
            ""));

        var parsed = RspParser.Parse(workspace.PathOf("Library/Bee/artifacts/x.dag/main.rsp"), workspace.Root);

        Assert.EndsWith("Sample.dll", parsed.OutPath!, StringComparison.Ordinal);
        Assert.Equal("library", parsed.Target);

        // Both - and / prefixes, and the -d short form; multi-value defines are split on ';'.
        Assert.Equal(new[] { "ALPHA", "BETA", "DELTA", "GAMMA" }, parsed.Defines.ToArray());

        Assert.Equal("9.0", parsed.LangVersion);
        Assert.True(parsed.AllowUnsafe);
        Assert.Equal("disable", parsed.Nullable);

        // An alias= prefix on a reference is stripped; a path is not mistaken for an alias.
        Assert.Equal(2, parsed.References.Count);
        Assert.Contains(parsed.References, r => r.EndsWith("One.dll", StringComparison.Ordinal));
        Assert.Contains(parsed.References, r => r.EndsWith("Two.dll", StringComparison.Ordinal));

        // Quoted, bare, upper-case extension, and a source from the nested file.
        Assert.Equal(3, parsed.Sources.Count);
        Assert.Contains(parsed.Sources, s => s.EndsWith("Quoted.cs", StringComparison.Ordinal));
        Assert.Contains(parsed.Sources, s => s.EndsWith("Bare.CS", StringComparison.Ordinal));
        Assert.Contains(parsed.Sources, s => s.EndsWith("FromNested.cs", StringComparison.Ordinal));

        // Unknown options are kept, not rejected and not dropped.
        Assert.Contains("-deterministic", parsed.IgnoredOptions);
        Assert.Contains("/nowarn:0168", parsed.IgnoredOptions);
        Assert.Contains(parsed.IgnoredOptions, o => o.Contains("additionalfile", StringComparison.Ordinal));

        Assert.Single(parsed.Included);
    }

    [Fact]
    [Trait("Task", "T06")]
    public void Rsp_Parser_DetectsCycle()
    {
        using var workspace = new TempProject();

        workspace.Write("a.rsp", "@b.rsp\n");
        workspace.Write("b.rsp", "@a.rsp\n");

        var error = Assert.Throws<RspCycleException>(
            () => RspParser.Parse(workspace.PathOf("a.rsp"), workspace.Root));

        Assert.Contains("a.rsp", error.Message, StringComparison.Ordinal);
        Assert.Contains("b.rsp", error.Message, StringComparison.Ordinal);

        // Self-inclusion is the degenerate case and must be caught too.
        workspace.Write("self.rsp", "@self.rsp\n");
        Assert.Throws<RspCycleException>(() => RspParser.Parse(workspace.PathOf("self.rsp"), workspace.Root));
    }

    [Fact]
    [Trait("Task", "T06")]
    public void Rsp_Discovery_PicksTheRicherDuplicate()
    {
        using var workspace = new TempProject();

        workspace.Write("Assets/One.cs", "public class One { }");
        workspace.Write("Assets/Two.cs", "public class Two { }");

        workspace.Write("Library/Bee/artifacts/thin.dag/Assembly-CSharp.rsp", string.Join("\n",
            "-out:\"bin/Assembly-CSharp.dll\"", "-define:UNITY_EDITOR", "Assets/One.cs", ""));
        workspace.Write("Library/Bee/artifacts/rich.dag/Assembly-CSharp.rsp", string.Join("\n",
            "-out:\"bin/Assembly-CSharp.dll\"", "-define:UNITY_EDITOR", "Assets/One.cs", "Assets/Two.cs", ""));

        var result = Preflight.Run(workspace.Root, Profile("fixture"));

        var input = Assert.Single(result.Inputs);
        Assert.Equal(2, input.Sources.Count);
        Assert.Contains("rich.dag", input.RspPath, StringComparison.Ordinal);

        var duplicates = result.Diagnostics.Where(d => d.Code == Preflight.DuplicateAssemblyRsp).ToList();
        var warning = Assert.Single(duplicates);
        Assert.Contains("most sources", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Task", "T06")]
    public void Preflight_NoRsp_Throws()
    {
        using var empty = new TempProject();

        var error = Assert.Throws<NoCompilationInputException>(
            () => Preflight.Run(empty.Root, Profile("fixture")));

        Assert.Contains(NoCompilationInputException.ErrorCode, error.Message, StringComparison.Ordinal);
        Assert.Contains("Unity 2022.3.68f1", error.Message, StringComparison.Ordinal);
        Assert.Contains("Android Player build", error.Message, StringComparison.Ordinal);

        // A response file that names no assembly is not a compilation input either.
        empty.Write("Library/Bee/artifacts/x.dag/empty.rsp", "-define:UNITY_EDITOR\n");
        Assert.Throws<NoCompilationInputException>(() => Preflight.Run(empty.Root, Profile("fixture")));
    }

    [Fact]
    [Trait("Task", "T06")]
    public void Preflight_ReportsUnityVersionMismatch()
    {
        using var workspace = new TempProject(FixtureLayout.MiniPos);

        workspace.Write("ProjectSettings/ProjectVersion.txt", "m_EditorVersion: 2021.3.10f1\n");

        var result = Preflight.Run(workspace.Root, Profile("fixture"));
        var mismatches = result.Diagnostics.Where(d => d.Code == Preflight.UnityVersionMismatch).ToList();
        var mismatch = Assert.Single(mismatches);
        Assert.Equal(Severities.Warning, mismatch.Severity);
        Assert.Contains("2021.3.10f1", mismatch.Message, StringComparison.Ordinal);
        Assert.Contains("2022.3.68", mismatch.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Task", "T06")]
    public void Rsp_UnityVersionAndPlatformInference()
    {
        // The most specific UNITY_ version define wins (T06 §3).
        Assert.Equal("2022.3.68", RspParser.UnityVersionFrom(new[] { "UNITY_2022_3", "UNITY_2022_3_68", "DEBUG" }));
        Assert.Null(RspParser.UnityVersionFrom(new[] { "DEBUG", "UNITY_ANDROID" }));

        Assert.Equal(RspPlatforms.Editor, RspParser.PlatformFrom(new[] { "UNITY_EDITOR", "UNITY_ANDROID" }));
        Assert.Equal(RspPlatforms.Android, RspParser.PlatformFrom(new[] { "UNITY_ANDROID" }));
        Assert.Equal(RspPlatforms.Unknown, RspParser.PlatformFrom(new[] { "DEBUG" }));

        Assert.Equal("2022.3.68", RspDiscovery.NumericPrefix("2022.3.68f1"));
        Assert.Equal("2022.3", RspDiscovery.NumericPrefix("2022.3"));
    }

    [Fact]
    [Trait("Task", "T06")]
    public void Encoding_Detects_Utf8Bom_Utf8_Cp932()
    {
        var withBom = new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(Encoding.UTF8.GetBytes("class A { } // 合計"))
            .ToArray();
        var bomResult = SourceEncoding.Decode(withBom);
        Assert.Equal(Encodings.Utf8Bom, bomResult.EncodingName);
        Assert.StartsWith("class A", bomResult.Text, StringComparison.Ordinal);
        Assert.Contains("合計", bomResult.Text, StringComparison.Ordinal);

        var plain = Encoding.UTF8.GetBytes("class B { } // 合計金額");
        var plainResult = SourceEncoding.Decode(plain);
        Assert.Equal(Encodings.Utf8, plainResult.EncodingName);
        Assert.Contains("合計金額", plainResult.Text, StringComparison.Ordinal);

        var cp932 = SourceEncoding.Cp932.GetBytes("class C { } // 合計金額");
        var cp932Result = SourceEncoding.Decode(cp932);
        Assert.Equal(Encodings.Cp932, cp932Result.EncodingName);
        Assert.Contains("合計金額", cp932Result.Text, StringComparison.Ordinal);

        // The real fixture file, through the same path.
        var key01 = SourceEncoding.DecodeFile(FixtureLayout.Cp932FilePath);
        Assert.Equal(Encodings.Cp932, key01.EncodingName);
        Assert.Contains("public const string Label = \"合計金額\";", key01.Text, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Task", "T06")]
    public void Encoding_LineCountMatchesMixedEndings()
    {
        // 75 files in the target repository mix CRLF and LF (03 §1), so the count must not depend on
        // which separator a line happens to use.
        Assert.Equal(3, SourceEncoding.CountLines("a\nb\nc"));
        Assert.Equal(3, SourceEncoding.CountLines("a\r\nb\r\nc"));
        Assert.Equal(3, SourceEncoding.CountLines("a\r\nb\nc"));
        Assert.Equal(3, SourceEncoding.CountLines("a\nb\nc\n"));
        Assert.Equal(1, SourceEncoding.CountLines("a"));
        Assert.Equal(1, SourceEncoding.CountLines(""));
    }
}

/// <summary>
/// A throwaway project directory, optionally seeded from the fixture, so a test can mutate inputs
/// without touching the fixture itself.
/// </summary>
internal sealed class TempProject : IDisposable
{
    public TempProject(string? seedFrom = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "screenflow-rsp-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(Root);
        if (seedFrom is not null) CopyDirectory(seedFrom, Root);
    }

    public string Root { get; }

    public string PathOf(string relative)
        => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    public void Write(string relative, string content)
    {
        var path = PathOf(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(directory.Replace(source, destination));

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(source, destination), overwrite: true);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory must not fail a test.
        }
    }
}
