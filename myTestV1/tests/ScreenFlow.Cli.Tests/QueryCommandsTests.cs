using System.Text.Json;
using ScreenFlow.Core.Storage;
using Xunit;

namespace ScreenFlow.Cli.Tests;

/// <summary>
/// The T20 query commands: every command returns the envelope of 02 §11.3, errors map onto the
/// right exit codes, no absolute path leaks into the data, and the build stays deterministic.
/// </summary>
[Collection("cli")]
public sealed class QueryCommandsTests
{
    private static readonly Lazy<CliRun> LazyBuild = new(BuildFixture, LazyThreadSafetyMode.ExecutionAndPublication);

    [Fact]
    [Trait("Task", "T20")]
    public void Cli_ScreenControls_0903_TextJa()
    {
        var run = Invoke("screen", "controls", "--id", "SSAW0903", "--locale", "ja", "--db", LazyBuild.Value.DbPath, "--config-dir", ConfigDir);

        Assert.Equal(ExitCodes.Ok, run.ExitCode);

        var data = run.Json().GetProperty("data");
        var texts = data.GetProperty("controls")
            .EnumerateArray()
            .Where(control => control.TryGetProperty("text", out _))
            .Select(control => control.GetProperty("text").GetProperty("plain").GetString())
            .ToArray();

        Assert.Contains("その他の金額を入力\n(1,000円単位)", texts);
    }

    [Fact]
    [Trait("Task", "T20")]
    public void Cli_AllCommands_ReturnValidEnvelope()
    {
        var db = LazyBuild.Value.DbPath;

        foreach (var args in Commands(db))
        {
            var run = Invoke(args);
            Assert.True(run.ExitCode == ExitCodes.Ok,
                $"'{string.Join(' ', args)}' exited {run.ExitCode}: {run.Stdout}");

            var envelope = run.Json();
            Assert.True(envelope.GetProperty("ok").GetBoolean(),
                $"'{string.Join(' ', args)}' reported ok: false");
            Assert.True(envelope.TryGetProperty("data", out _));
            Assert.False(envelope.GetProperty("meta").GetProperty("stale").GetBoolean(),
                $"'{string.Join(' ', args)}' reported a stale index unexpectedly");
        }
    }

    [Fact]
    [Trait("Task", "T20")]
    public void Cli_ErrorCodesAndExitCodes()
    {
        var db = LazyBuild.Value.DbPath;

        // Missing required argument: input error, exit code 2.
        var missingArg = Invoke("screen", "get", "--db", db, "--config-dir", ConfigDir);
        Assert.Equal(ExitCodes.InvalidInput, missingArg.ExitCode);
        Assert.Equal(ErrorCodes.InvalidInput, missingArg.Json().GetProperty("error").GetProperty("code").GetString());

        // A screen that does not exist: not found, exit code 3.
        var missingScreen = Invoke("screen", "get", "--id", "SSAW9999", "--db", db, "--config-dir", ConfigDir);
        Assert.Equal(ExitCodes.NotFound, missingScreen.ExitCode);
        Assert.Equal(ErrorCodes.NotFound, missingScreen.Json().GetProperty("error").GetProperty("code").GetString());

        // No database at the path: E_INDEX_NOT_FOUND.
        var noDb = Invoke(
            "screen", "get", "--id", "SSAW0903",
            "--db", Path.Combine(TempRoot(), "does-not-exist.db"),
            "--config-dir", ConfigDir);
        Assert.Equal(ExitCodes.NotFound, noDb.ExitCode);
        Assert.Equal(ErrorCodes.IndexNotFound, noDb.Json().GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    [Trait("Task", "T20")]
    public void Cli_NoAbsolutePathsInOutput()
    {
        var db = LazyBuild.Value.DbPath;

        foreach (var args in Commands(db))
        {
            var run = Invoke(args);
            Assert.True(run.ExitCode == ExitCodes.Ok, $"'{string.Join(' ', args)}' exited {run.ExitCode}");

            var rooted = RootedPathStrings(run.Json().GetProperty("data"));
            Assert.Empty(rooted);
        }
    }

    [Fact]
    [Trait("Task", "T20")]
    public void Cli_Stale_WhenFixtureFileTouched()
    {
        var project = Path.Combine(TempRoot(), "stale-" + Guid.NewGuid().ToString("N"));
        CopyDirectory(FixtureProject, project);

        var db = Path.Combine(project, "index.db");
        var build = Invoke("index", "build", "--project", project, "--profile", "fixture", "--db", db, "--config-dir", ConfigDir);
        Assert.Equal(ExitCodes.Ok, build.ExitCode);

        var status = Invoke("index", "status", "--project", project, "--profile", "fixture", "--db", db, "--config-dir", ConfigDir);
        Assert.False(status.Json().GetProperty("data").GetProperty("stale").GetBoolean());

        // Touch one analysed source: the fingerprint covers size and mtime (02 §7.6).
        var source = Directory.EnumerateFiles(Path.Combine(project, "Assets"), "*.cs", SearchOption.AllDirectories).First();
        File.AppendAllText(source, "\n// touched by test\n");

        var after = Invoke("index", "status", "--project", project, "--profile", "fixture", "--db", db, "--config-dir", ConfigDir);
        Assert.True(after.Json().GetProperty("data").GetProperty("stale").GetBoolean());
    }

    [Fact]
    [Trait("Task", "T20")]
    public void Determinism_TwoBuilds_ProduceIdenticalDump()
    {
        var dir = Path.Combine(TempRoot(), "determinism-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        var db1 = Path.Combine(dir, "one.db");
        var db2 = Path.Combine(dir, "two.db");

        Assert.Equal(ExitCodes.Ok, Invoke("index", "build", "--project", FixtureProject, "--profile", "fixture", "--db", db1, "--config-dir", ConfigDir).ExitCode);
        Assert.Equal(ExitCodes.Ok, Invoke("index", "build", "--project", FixtureProject, "--profile", "fixture", "--db", db2, "--config-dir", ConfigDir).ExitCode);

        var dump1 = Path.Combine(dir, "one.tsv");
        var dump2 = Path.Combine(dir, "two.tsv");

        Assert.Equal(ExitCodes.Ok, Invoke("index", "dump", "--db", db1, "--profile", "fixture", "--out", dump1, "--config-dir", ConfigDir).ExitCode);
        Assert.Equal(ExitCodes.Ok, Invoke("index", "dump", "--db", db2, "--profile", "fixture", "--out", dump2, "--config-dir", ConfigDir).ExitCode);

        Assert.Equal(File.ReadAllBytes(dump1), File.ReadAllBytes(dump2));
    }

    // ------------------------------------------------------------------ commands

    private static string[][] Commands(string db)
    {
        using var index = IndexDb.Open(db);
        var reader = new IndexReader(index);
        var breakId = reader.Breaks().First().Id;
        var symbolId = reader.SymbolsByName("EdyCharge_SSAW0903_Presenter").First().Id;

        using var evidenceCommand = index.Command("SELECT id FROM evidence ORDER BY id LIMIT 1;");
        var evidenceId = (string)evidenceCommand.ExecuteScalar()!;

        const string callsiteFile = "Assets/SceneCustomer/Reception/EdyCharge/View/Script/EdyCharge_SSAW0903_View.cs";

        return new[]
        {
            new[] { "screen", "get", "--id", "SSAW0903", "--db", db, "--config-dir", ConfigDir },
            new[] { "screen", "controls", "--id", "SSAW0903", "--locale", "ja", "--db", db, "--config-dir", ConfigDir },
            new[] { "screen", "constraints", "--id", "SSAW0907", "--locale", "ja", "--db", db, "--config-dir", ConfigDir },
            new[] { "screen", "transitions", "--id", "SSAW0903", "--direction", "out", "--db", db, "--config-dir", ConfigDir },
            new[] { "path", "find", "--from", "SSAW0903", "--to", "SSAW0907", "--detail", "full", "--db", db, "--config-dir", ConfigDir },
            new[] { "path", "prefix", "--to", "SSAW0907", "--db", db, "--config-dir", ConfigDir },
            new[] { "break", "list", "--db", db, "--config-dir", ConfigDir },
            new[] { "break", "get", "--id", breakId, "--db", db, "--config-dir", ConfigDir },
            new[] { "symbol", "find", "--name", "EdyCharge_SSAW0903_Presenter", "--db", db, "--config-dir", ConfigDir },
            new[] { "symbol", "get", "--id", symbolId, "--db", db, "--config-dir", ConfigDir },
            new[] { "symbol", "callers", "--id", symbolId, "--db", db, "--config-dir", ConfigDir },
            new[] { "symbol", "callees", "--id", symbolId, "--db", db, "--config-dir", ConfigDir },
            new[] { "evidence", "get", "--id", evidenceId, "--project", FixtureProject, "--db", db, "--config-dir", ConfigDir },
            new[] { "text", "get", "--key", "SSAW0903_12", "--locale", "ja", "--db", db, "--config-dir", ConfigDir },
            new[] { "message", "get", "--code", "ERRB022", "--locale", "ja", "--db", db, "--config-dir", ConfigDir },
            new[] { "debug", "stats", "--db", db, "--config-dir", ConfigDir },
            new[] { "debug", "callsites", "--file", callsiteFile, "--from-line", "1", "--to-line", "200", "--db", db, "--config-dir", ConfigDir },
            new[] { "debug", "sends", "--key-contains", "SSAW0281", "--db", db, "--config-dir", ConfigDir }
        };
    }

    // ------------------------------------------------------------------ harnessing

    private static CliRun BuildFixture()
    {
        var dbPath = Path.Combine(TempRoot(), "query-" + Guid.NewGuid().ToString("N"), "fixture.db");

        return Invoke(
            "index", "build",
            "--project", FixtureProject,
            "--profile", "fixture",
            "--db", dbPath,
            "--config-dir", ConfigDir) with
        {
            DbPath = dbPath
        };
    }

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

    private static IReadOnlyList<string> RootedPathStrings(JsonElement element)
    {
        var rooted = new List<string>();

        void Walk(JsonElement current)
        {
            switch (current.ValueKind)
            {
                case JsonValueKind.String:
                    var value = current.GetString();
                    if (value is null) break;

                    if (value.Length >= 2 && char.IsAsciiLetter(value[0]) && value[1] == ':')
                        rooted.Add(value);
                    else if (value.StartsWith('/') || value.StartsWith('\\'))
                        rooted.Add(value);
                    break;

                case JsonValueKind.Array:
                    foreach (var item in current.EnumerateArray()) Walk(item);
                    break;

                case JsonValueKind.Object:
                    foreach (var property in current.EnumerateObject()) Walk(property.Value);
                    break;
            }
        }

        Walk(element);
        return rooted;
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static string TempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "screenflow-tests", "cli");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string FixtureProject => Path.Combine(RepoRoot, "tests", "fixtures", "MiniPos");

    private static string ConfigDir => Path.Combine(RepoRoot, "config");

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
                       ?? throw new InvalidOperationException("no stdout");
            return JsonDocument.Parse(line).RootElement.Clone();
        }
    }
}
