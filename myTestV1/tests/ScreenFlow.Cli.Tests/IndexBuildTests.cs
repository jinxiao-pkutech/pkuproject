using System.Text;
using System.Text.Json;
using Xunit;

namespace ScreenFlow.Cli.Tests;

/// <summary>
/// The <c>index build</c>, <c>index status</c> and <c>index dump</c> commands (02 §11).
/// <para>
/// What is under test is the contract the MCP service depends on: one JSON document on stdout and
/// an exit code that says what kind of failure it was (02 §11.3, 04 §2).
/// </para>
/// </summary>
[Collection("cli")]
public sealed class IndexBuildTests
{
    /// <summary>The fixture build, which the status and dump cases reuse.</summary>
    private static readonly Lazy<CliRun> LazyBuild = new(BuildFixture, LazyThreadSafetyMode.ExecutionAndPublication);

    [Fact]
    [Trait("Task", "T11")]
    public void Cli_IndexBuild_Fixture_Envelope()
    {
        var run = LazyBuild.Value;

        Assert.Equal(ExitCodes.Ok, run.ExitCode);

        var envelope = run.Json();
        Assert.True(envelope.GetProperty("ok").GetBoolean());

        var data = envelope.GetProperty("data");
        Assert.Equal("fixture", data.GetProperty("profileId").GetString());
        Assert.Equal("native", data.GetProperty("derivation").GetString());
        Assert.True(data.GetProperty("counts").GetProperty("symbols").GetInt64() > 0);
        Assert.Equal(66, data.GetProperty("counts").GetProperty("files").GetInt64());

        var meta = envelope.GetProperty("meta");
        Assert.Equal("1", meta.GetProperty("irVersion").GetString());
        Assert.Equal("1", meta.GetProperty("rulesVersion").GetString());
        Assert.False(meta.GetProperty("stale").GetBoolean());

        // Exactly one document on stdout (02 §11.3).
        Assert.Single(run.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries));

        // Progress went to stderr as NDJSON, and every line parses (02 §2).
        var progress = run.Stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.NotEmpty(progress);

        foreach (var line in progress)
        {
            using var document = JsonDocument.Parse(line);
            Assert.NotNull(document.RootElement.GetProperty("phase").GetString());
            Assert.True(document.RootElement.GetProperty("elapsedMs").GetInt64() >= 0);
        }

        Assert.True(File.Exists(run.DbPath));
    }

    /// <summary>
    /// 02 §11.3: a project with no response files cannot be built, and that is exit code 4 with
    /// <c>E_NO_COMPILATION_INPUT</c> — not an empty index that would answer every question with "no".
    /// </summary>
    [Fact]
    [Trait("Task", "T11")]
    public void Cli_IndexBuild_NoRsp_ReturnsENoCompilationInput()
    {
        var empty = Path.Combine(TempRoot(), "empty-project");
        Directory.CreateDirectory(Path.Combine(empty, "Assets"));

        var run = Invoke(
            "index", "build",
            "--project", empty,
            "--profile", "fixture",
            "--db", Path.Combine(empty, "index.db"),
            "--config-dir", Path.Combine(RepoRoot, "config"));

        Assert.Equal(ExitCodes.BuildFailed, run.ExitCode);

        var envelope = run.Json();
        Assert.False(envelope.GetProperty("ok").GetBoolean());
        Assert.Equal(
            ErrorCodes.NoCompilationInput,
            envelope.GetProperty("error").GetProperty("code").GetString());

        // The failed build left nothing behind (02 §2).
        Assert.False(File.Exists(Path.Combine(empty, "index.db")));
    }

    /// <summary>02 §11.2: <c>index status</c> reports the meta, the counts and whether it is stale.</summary>
    [Fact]
    [Trait("Task", "T11")]
    public void Cli_IndexStatus_ReportsMetaAndFreshness()
    {
        var build = LazyBuild.Value;

        var run = Invoke(
            "index", "status",
            "--project", FixtureProject,
            "--profile", "fixture",
            "--db", build.DbPath,
            "--config-dir", Path.Combine(RepoRoot, "config"));

        Assert.Equal(ExitCodes.Ok, run.ExitCode);

        var data = run.Json().GetProperty("data");
        Assert.False(data.GetProperty("stale").GetBoolean());
        Assert.True(data.GetProperty("staleChecked").GetBoolean());
        Assert.Equal("fixture", data.GetProperty("meta").GetProperty("profileId").GetString());
        Assert.True(data.GetProperty("counts").GetProperty("symbols").GetInt64() > 0);
    }

    /// <summary>
    /// 02 §7.5: the canonical dump is what the determinism test compares, so the command has to
    /// produce the file and name its hash.
    /// </summary>
    [Fact]
    [Trait("Task", "T11")]
    public void Cli_IndexDump_WritesCanonicalDump()
    {
        var build = LazyBuild.Value;
        var outPath = Path.Combine(TempRoot(), "dump-" + Guid.NewGuid().ToString("N") + ".tsv");

        var run = Invoke(
            "index", "dump",
            "--db", build.DbPath,
            "--profile", "fixture",
            "--out", outPath,
            "--config-dir", Path.Combine(RepoRoot, "config"));

        Assert.Equal(ExitCodes.Ok, run.ExitCode);

        var data = run.Json().GetProperty("data");
        Assert.Equal(64, data.GetProperty("sha256").GetString()!.Length);
        Assert.True(data.GetProperty("lines").GetInt64() > 0);
        Assert.True(File.Exists(outPath));
    }

    /// <summary>02 §11.3: an unknown profile is an input error, which is exit code 2.</summary>
    [Fact]
    [Trait("Task", "T11")]
    public void Cli_UnknownProfile_IsInvalidInput()
    {
        var run = Invoke(
            "index", "status",
            "--profile", "no-such-profile",
            "--config-dir", Path.Combine(RepoRoot, "config"));

        Assert.Equal(ExitCodes.InvalidInput, run.ExitCode);
        Assert.Equal(
            ErrorCodes.ProfileNotFound,
            run.Json().GetProperty("error").GetProperty("code").GetString());
    }

    // ------------------------------------------------------------------ harnessing

    private static string FixtureProject => Path.Combine(RepoRoot, "tests", "fixtures", "MiniPos");

    private static CliRun BuildFixture()
    {
        var dbPath = Path.Combine(TempRoot(), "cli-" + Guid.NewGuid().ToString("N"), "fixture.db");

        return Invoke(
            "index", "build",
            "--project", FixtureProject,
            "--profile", "fixture",
            "--db", dbPath,
            "--config-dir", Path.Combine(RepoRoot, "config")) with
        {
            DbPath = dbPath
        };
    }

    /// <summary>
    /// Runs the command in process with stdout and stderr captured. In process rather than as a
    /// child process so that a failure shows the real stack trace instead of an exit code.
    /// </summary>
    private static CliRun Invoke(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);

            var exitCode = Program.Main(args);
            return new CliRun(exitCode, stdout.ToString(), stderr.ToString(), string.Empty);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
        }
    }

    private static string TempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "screenflow-tests", "cli");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string RepoRoot { get; } = FindRepoRoot();

    private static string FindRepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ScreenFlow.sln"))) return directory.FullName;
        }

        throw new InvalidOperationException($"Could not find ScreenFlow.sln above {AppContext.BaseDirectory}.");
    }

    private sealed record CliRun(int ExitCode, string Stdout, string Stderr, string DbPath)
    {
        public JsonElement Json()
        {
            var line = Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()
                       ?? throw new InvalidOperationException("the command wrote nothing to stdout");

            return JsonDocument.Parse(line).RootElement.Clone();
        }
    }
}

/// <summary>
/// The CLI writes to the process-wide console, so its tests cannot run beside each other.
/// </summary>
[CollectionDefinition("cli", DisableParallelization = true)]
public sealed class CliCollection
{
}
