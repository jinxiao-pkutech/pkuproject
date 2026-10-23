using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ScreenFlow.Core;
using ScreenFlow.Core.Config;
using ScreenFlow.Core.Extraction;
using ScreenFlow.Core.Ir;
using ScreenFlow.Core.Graph;
using ScreenFlow.Core.Pathing;
using ScreenFlow.Core.Pipeline;
using ScreenFlow.Core.Query;
using ScreenFlow.Core.Storage;
using ScreenFlow.CSharp;
using ScreenFlow.CSharp.Compilation;
using ScreenFlow.UnityPos;
using ScreenFlow.UnityPos.Resources;

namespace ScreenFlow.Cli;

/// <summary>
/// The command line (02 §11).
/// <para>
/// stdout carries exactly one JSON document and nothing else — progress goes to stderr — because
/// the MCP service parses stdout as a whole (04 §2). T11 implements <c>index build</c>,
/// <c>index status</c> and <c>index dump</c>; the query commands follow in T20.
/// </para>
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        var clock = Stopwatch.StartNew();
        TrySetUtf8();

        try
        {
            return Run(args, clock);
        }
        catch (CliException exception)
        {
            return Fail(exception.Code, exception.Message, clock, exception.Details);
        }
        catch (NoCompilationInputException exception)
        {
            return Fail(NoCompilationInputException.ErrorCode, exception.Message, clock);
        }
        catch (IdCollisionException exception)
        {
            return Fail(IdCollisionException.ErrorCode, exception.Message, clock);
        }
        catch (IrSchemaException exception)
        {
            return Fail(ErrorCodes.IrSchema, exception.Message, clock);
        }
        catch (OperationCanceledException)
        {
            return Fail(ErrorCodes.Internal, "The build was cancelled.", clock);
        }
        catch (Exception exception)
        {
            return Fail(ErrorCodes.Internal, exception.Message, clock);
        }
    }

    private static int Run(string[] args, Stopwatch clock)
    {
        var options = CliOptions.Parse(args);

        return options.Command switch
        {
            "index build" => IndexBuild(options, clock),
            "index status" => IndexStatus(options, clock),
            "index dump" => IndexDump(options, clock),
            "screen get" => ScreenGet(options, clock),
            "screen controls" => ScreenControls(options, clock),
            "screen constraints" => ScreenConstraints(options, clock),
            "screen transitions" => ScreenTransitions(options, clock),
            "path find" => PathFind(options, clock),
            "path prefix" => PathPrefix(options, clock),
            "break list" => BreakList(options, clock),
            "break get" => BreakGet(options, clock),
            "symbol find" => SymbolFind(options, clock),
            "symbol get" => SymbolGet(options, clock),
            "symbol callers" => SymbolCallers(options, clock),
            "symbol callees" => SymbolCallees(options, clock),
            "evidence get" => EvidenceGet(options, clock),
            "text get" => TextGet(options, clock),
            "message get" => MessageGet(options, clock),
            "debug stats" => DebugStats(options, clock),
            "debug callsites" => DebugCallsites(options, clock),
            "debug sends" => DebugSends(options, clock),
            _ => throw new CliException(
                ErrorCodes.InvalidInput,
                options.Command.Length == 0
                    ? "No command given. Run 'screenflow' without arguments is not supported; " +
                      "try 'index build', 'screen get', 'path find', 'break list', 'symbol find'."
                    : $"Command '{options.Command}' is not implemented in this build.")
        };
    }

    // ------------------------------------------------------------------ commands

    private static int IndexBuild(CliOptions options, Stopwatch clock)
    {
        var projectRoot = options.RequireProject();
        var (profiles, domain) = LoadConfig(options);
        var profile = FindProfile(profiles, options.Profile);
        var dbPath = options.Db ?? IndexDb.DefaultPath(projectRoot, profile.Id);

        var progress = new ProgressReporter(Console.Error, clock);

        // P6-P8 come from the POS domain stage (02 §2); the core itself knows nothing about them.
        var report = new IndexBuilder(new IDomainStage[] { new UnityPosDomain(), new ScreenGraphBuilder() }).Build(
            projectRoot,
            profile,
            domain,
            dbPath,
            options.ConfigDirectory ?? ConfigLoader.ResolveConfigDirectory(options.ConfigDirectory),
            () => new CSharpExtractor(domain, options.Parallel),
            progress);

        return Succeed(
            JsonSerializer.SerializeToNode(report, SerializerOptions),
            clock,
            options,
            profile.Id,
            report.Derivation,
            stale: false);
    }

    private static int IndexStatus(CliOptions options, Stopwatch clock)
    {
        var (profiles, domain) = LoadConfig(options);
        var profile = FindProfile(profiles, options.Profile);
        var dbPath = options.Db ?? DefaultDbPath(options, profile.Id);

        if (!File.Exists(dbPath))
        {
            throw new CliException(
                ErrorCodes.IndexNotFound,
                $"No index at '{Ids.Normalize(dbPath)}'. Run 'index build' first.");
        }

        using var db = IndexDb.Open(dbPath);
        var reader = new IndexReader(db);
        var meta = reader.Meta();

        var staleness = Staleness(options, reader, meta, domain);

        var data = new JsonObject
        {
            ["meta"] = JsonSerializer.SerializeToNode(meta, SerializerOptions),
            ["counts"] = JsonSerializer.SerializeToNode(db.AllCounts(), SerializerOptions),
            ["stale"] = staleness.Stale,
            ["staleChecked"] = staleness.Checked,
            ["staleReasons"] = JsonSerializer.SerializeToNode(staleness.Reasons, SerializerOptions),
            ["db"] = Ids.Normalize(dbPath)
        };

        return Succeed(
            data,
            clock,
            options,
            meta.GetValueOrDefault(MetaKeys.ProfileId, profile.Id),
            meta.GetValueOrDefault(MetaKeys.Derivation, Derivations.Native),
            staleness.Stale);
    }

    private static int IndexDump(CliOptions options, Stopwatch clock)
    {
        var (profiles, _) = LoadConfig(options);
        var profile = FindProfile(profiles, options.Profile);
        var dbPath = options.Db ?? DefaultDbPath(options, profile.Id);

        if (!File.Exists(dbPath))
        {
            throw new CliException(
                ErrorCodes.IndexNotFound,
                $"No index at '{Ids.Normalize(dbPath)}'. Run 'index build' first.");
        }

        var outPath = options.Out
                      ?? throw new CliException(ErrorCodes.InvalidInput, "index dump requires --out <file>.");

        using var db = IndexDb.Open(dbPath);
        var result = ScreenFlowDump.WriteToFile(db, outPath);
        var meta = new IndexReader(db).Meta();

        var data = new JsonObject
        {
            ["file"] = Ids.Normalize(outPath),
            ["sha256"] = result.Sha256,
            ["lines"] = result.Lines,
            ["bytes"] = result.Bytes
        };

        return Succeed(
            data,
            clock,
            options,
            meta.GetValueOrDefault(MetaKeys.ProfileId, profile.Id),
            meta.GetValueOrDefault(MetaKeys.Derivation, Derivations.Native),
            stale: false);
    }

    // ------------------------------------------------------------------ query commands

    private static int ScreenGet(CliOptions options, Stopwatch clock)
    {
        var id = options.Require(nameof(CliOptions.Id));
        using var context = Open(options);
        var results = new ScreenQuery(context.Reader, context.Domain).Screens(id, options.Terminal);
        if (results.Count == 0) throw NotFound("screen", id);

        return SucceedNode(Shape(results), options, clock, context, context.FingerprintStale(options, context.Domain));
    }

    private static int ScreenControls(CliOptions options, Stopwatch clock)
    {
        var id = options.Require(nameof(CliOptions.Id));
        using var context = Open(options);
        var results = new ScreenQuery(context.Reader, context.Domain).Controls(id, options.Terminal, options.Locale);
        if (results.Count == 0) throw NotFound("screen", id);

        return SucceedNode(Shape(results), options, clock, context, context.FingerprintStale(options, context.Domain));
    }

    private static int ScreenConstraints(CliOptions options, Stopwatch clock)
    {
        var id = options.Require(nameof(CliOptions.Id));
        using var context = Open(options);
        var results = new ScreenQuery(context.Reader, context.Domain).Constraints(id, options.Terminal, options.Locale);
        if (results.Count == 0) throw NotFound("screen", id);

        return SucceedNode(Shape(results), options, clock, context, context.FingerprintStale(options, context.Domain));
    }

    private static int ScreenTransitions(CliOptions options, Stopwatch clock)
    {
        var id = options.Require(nameof(CliOptions.Id));
        var direction = options.Direction ?? throw new CliException(ErrorCodes.InvalidInput, "screen transitions requires --direction out|in.");
        if (direction is not ("out" or "in"))
            throw new CliException(ErrorCodes.InvalidInput, $"screen transitions --direction must be 'out' or 'in', got '{direction}'.");

        using var context = Open(options);
        var results = new ScreenQuery(context.Reader, context.Domain).Transitions(id, options.Terminal, direction, options.Limit ?? 50);
        if (results.Count == 0) throw NotFound("screen", id);

        return SucceedNode(Shape(results), options, clock, context, context.FingerprintStale(options, context.Domain));
    }

    private static int PathFind(CliOptions options, Stopwatch clock)
    {
        var from = options.Require(nameof(CliOptions.From));
        var to = options.Require(nameof(CliOptions.To));
        using var context = Open(options);

        var service = new PathService(context.Reader, context.Domain);
        var result = service.Find(from, to, new PathOptions
        {
            TerminalFrom = options.TerminalFrom,
            TerminalTo = options.TerminalTo,
            K = options.K ?? 5,
            MaxHops = options.MaxHops ?? 25,
            IncludePrefix = options.IncludePrefix,
            Detail = options.Detail ?? PathDetail.Summary,
            Locale = options.Locale
        });

        return SucceedNode(PathShape(result), options, clock, context, context.FingerprintStale(options, context.Domain));
    }

    private static int PathPrefix(CliOptions options, Stopwatch clock)
    {
        var to = options.Require(nameof(CliOptions.To));
        using var context = Open(options);

        var service = new PathService(context.Reader, context.Domain);
        var result = service.Prefix(to, new PathOptions
        {
            TerminalFrom = options.Terminal,
            TerminalTo = options.Terminal,
            Detail = options.Detail ?? PathDetail.Summary,
            Locale = options.Locale,
            MaxHops = options.MaxHops ?? 25
        });

        return SucceedNode(PathShape(result), options, clock, context, context.FingerprintStale(options, context.Domain));
    }

    private static int BreakList(CliOptions options, Stopwatch clock)
    {
        using var context = Open(options);
        var results = new BreakQuery(context.Reader).List(options.Category, options.Screen, options.Edge, options.Limit ?? 100);

        return SucceedNode(JsonSerializer.SerializeToNode(results, SerializerOptions), options, clock, context, context.FingerprintStale(options, context.Domain));
    }

    private static int BreakGet(CliOptions options, Stopwatch clock)
    {
        var id = options.Require(nameof(CliOptions.Id));
        using var context = Open(options);
        var result = new BreakQuery(context.Reader).Get(id) ?? throw NotFound("break", id);

        return SucceedNode(result, options, clock, context, context.FingerprintStale(options, context.Domain));
    }

    private static int SymbolFind(CliOptions options, Stopwatch clock)
    {
        var name = options.Require(nameof(CliOptions.Name));
        using var context = Open(options);
        var results = new SymbolQuery(context.Reader).Find(name, options.Kind, options.Limit ?? 20);

        return SucceedNode(JsonSerializer.SerializeToNode(results, SerializerOptions), options, clock, context, context.FingerprintStale(options, context.Domain));
    }

    private static int SymbolGet(CliOptions options, Stopwatch clock)
    {
        var id = options.Require(nameof(CliOptions.Id));
        using var context = Open(options);
        var result = new SymbolQuery(context.Reader).Get(id) ?? throw NotFound("symbol", id);

        return SucceedNode(JsonSerializer.SerializeToNode(result, SerializerOptions), options, clock, context, context.FingerprintStale(options, context.Domain));
    }

    private static int SymbolCallers(CliOptions options, Stopwatch clock)
    {
        var id = options.Require(nameof(CliOptions.Id));
        using var context = Open(options);
        var results = new SymbolQuery(context.Reader).Callers(id);

        return SucceedNode(JsonSerializer.SerializeToNode(results, SerializerOptions), options, clock, context, context.FingerprintStale(options, context.Domain));
    }

    private static int SymbolCallees(CliOptions options, Stopwatch clock)
    {
        var id = options.Require(nameof(CliOptions.Id));
        using var context = Open(options);
        var results = new SymbolQuery(context.Reader).Callees(id);

        return SucceedNode(JsonSerializer.SerializeToNode(results, SerializerOptions), options, clock, context, context.FingerprintStale(options, context.Domain));
    }

    private static int EvidenceGet(CliOptions options, Stopwatch clock)
    {
        var id = options.Require(nameof(CliOptions.Id));
        using var context = Open(options);
        var query = new EvidenceQuery(context.Reader, ProjectRoot(options, context.DbPath));
        var result = query.Get(id, options.Context ?? 3) ?? throw NotFound("evidence", id);

        var stale = context.FingerprintStale(options, context.Domain)
                    || (context.Reader.Evidence(id) is { } stored && query.IsStale(stored));

        return SucceedNode(JsonSerializer.SerializeToNode(result, SerializerOptions), options, clock, context, stale);
    }

    private static int TextGet(CliOptions options, Stopwatch clock)
    {
        var key = options.Require(nameof(CliOptions.Key));
        using var context = Open(options);
        var results = new TextQuery(context.Reader).Get(key, options.Collection, options.Locale);
        if (results.Count == 0) throw NotFound("text", key);

        return SucceedNode(Shape(results), options, clock, context, context.FingerprintStale(options, context.Domain));
    }

    private static int MessageGet(CliOptions options, Stopwatch clock)
    {
        var code = options.Require(nameof(CliOptions.Code));
        using var context = Open(options);
        var result = new MessageQuery(context.Reader).Get(code, options.Locale ?? "ja");
        if (result is null) throw NotFound("message", code);

        return SucceedNode(JsonSerializer.SerializeToNode(result, SerializerOptions), options, clock, context, context.FingerprintStale(options, context.Domain));
    }

    private static int DebugStats(CliOptions options, Stopwatch clock)
    {
        using var context = Open(options);
        var result = new DebugQuery(context.Reader).Stats();

        return SucceedNode(JsonSerializer.SerializeToNode(result, SerializerOptions), options, clock, context, context.FingerprintStale(options, context.Domain));
    }

    private static int DebugCallsites(CliOptions options, Stopwatch clock)
    {
        var file = options.Require(nameof(CliOptions.File));
        var fromLine = options.FromLine ?? throw new CliException(ErrorCodes.InvalidInput, "debug callsites requires --from-line.");
        var toLine = options.ToLine ?? throw new CliException(ErrorCodes.InvalidInput, "debug callsites requires --to-line.");
        using var context = Open(options);

        var result = new DebugQuery(context.Reader).Sites(file, fromLine, toLine);

        return SucceedNode(JsonSerializer.SerializeToNode(result, SerializerOptions), options, clock, context, context.FingerprintStale(options, context.Domain));
    }

    private static int DebugSends(CliOptions options, Stopwatch clock)
    {
        using var context = Open(options);
        var results = new DebugQuery(context.Reader).Sends(options.KeyContains, options.TargetScreen);

        return SucceedNode(JsonSerializer.SerializeToNode(results, SerializerOptions), options, clock, context, context.FingerprintStale(options, context.Domain));
    }

    // ------------------------------------------------------------------ helpers

    private static (ProfilesFile Profiles, DomainConfig Domain) LoadConfig(CliOptions options)
    {
        var directory = ConfigLoader.ResolveConfigDirectory(options.ConfigDirectory);

        try
        {
            return ConfigLoader.LoadAll(directory);
        }
        catch (Exception exception) when (exception is not CliException)
        {
            throw new CliException(
                ErrorCodes.InvalidInput,
                $"The configuration in '{Ids.Normalize(directory)}' could not be loaded: {exception.Message}");
        }
    }

    private static ProfileConfig FindProfile(ProfilesFile profiles, string? id)
    {
        var wanted = id ?? ProfilesFile.DefaultProfileId;

        return profiles.Find(wanted)
               ?? throw new CliException(
                   ErrorCodes.ProfileNotFound,
                   $"No profile '{wanted}'. Known profiles: {string.Join(", ", profiles.Profiles.Select(p => p.Id))}.");
    }

    private static string DefaultDbPath(CliOptions options, string profileId)
        => options.Project is null
            ? Path.Combine(IndexDb.DefaultDirectory, profileId + ".db")
            : IndexDb.DefaultPath(options.Project, profileId);

    private static CliContext Open(CliOptions options)
    {
        var (profiles, domain) = LoadConfig(options);
        var profile = FindProfile(profiles, options.Profile);
        var dbPath = options.Db ?? DefaultDbPath(options, profile.Id);

        if (!File.Exists(dbPath))
        {
            throw new CliException(
                ErrorCodes.IndexNotFound,
                $"No index at '{Ids.Normalize(dbPath)}'. Run 'index build' first.");
        }

        var db = IndexDb.Open(dbPath);
        var reader = new IndexReader(db);

        return new CliContext
        {
            Db = db,
            Reader = reader,
            DbPath = dbPath,
            Meta = reader.Meta(),
            Domain = domain,
            Profile = profile
        };
    }

    private static CliException NotFound(string kind, string value)
        => new(ErrorCodes.NotFound, $"No {kind} '{value}'.");

    private static int SucceedNode(
        JsonNode? data,
        CliOptions options,
        Stopwatch clock,
        CliContext context,
        bool stale)
        => Succeed(data, clock, options, context.ProfileId, context.Derivation, stale);

    /// <summary>
    /// One result is a single object; two or more are an array (02 §11.5-1, Q-17). A caller that
    /// named a terminal always gets the single-object shape.
    /// </summary>
    private static JsonNode? Shape<T>(IReadOnlyList<T> items)
        => items.Count == 1
            ? JsonSerializer.SerializeToNode(items[0], SerializerOptions)
            : JsonSerializer.SerializeToNode(items, SerializerOptions);

    private static JsonNode? PathShape(PathFindResult result)
    {
        var node = JsonSerializer.SerializeToNode(result, SerializerOptions) as JsonObject
                   ?? new JsonObject();

        node["from"] = Shape(result.From);
        node["to"] = Shape(result.To);

        return node;
    }

    /// <summary>
    /// The project root for reading source files back. <c>--project</c> wins; otherwise the
    /// default database location <c>&lt;project&gt;/.screenflow/&lt;profile&gt;.db</c> implies it.
    /// </summary>
    private static string ProjectRoot(CliOptions options, string dbPath)
    {
        if (options.Project is { Length: > 0 } project) return project;

        var directory = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        var parent = directory is null ? null : Path.GetDirectoryName(directory);

        if (directory is not null
            && string.Equals(Path.GetFileName(directory), IndexDb.DefaultDirectory, StringComparison.Ordinal)
            && parent is not null)
        {
            return parent;
        }

        throw new CliException(
            ErrorCodes.InvalidInput,
            "evidence get needs --project when the database is not at <project>/.screenflow/<profile>.db.");
    }

    private sealed class CliContext : IDisposable
    {
        public required IndexDb Db { get; init; }
        public required IndexReader Reader { get; init; }
        public required string DbPath { get; init; }
        public required IReadOnlyDictionary<string, string> Meta { get; init; }
        public required DomainConfig Domain { get; init; }
        public required ProfileConfig Profile { get; init; }

        public string ProfileId => Meta.GetValueOrDefault(MetaKeys.ProfileId, Profile.Id);

        public string Derivation => Meta.GetValueOrDefault(MetaKeys.Derivation, Derivations.Native);

        public bool FingerprintStale(CliOptions options, DomainConfig domain)
            => Staleness(options, Reader, Meta, domain).Stale;

        public void Dispose() => Db.Dispose();
    }

    /// <summary>
    /// Recomputes the fingerprint when the project is at hand (02 §7.6). Without <c>--project</c>
    /// the check cannot run, and that is reported rather than answered with a cheerful "fresh".
    /// </summary>
    private static (bool Stale, bool Checked, IReadOnlyList<string> Reasons) Staleness(
        CliOptions options,
        IndexReader reader,
        IReadOnlyDictionary<string, string> meta,
        DomainConfig domain)
    {
        if (options.Project is null) return (false, false, Array.Empty<string>());

        // Sources plus resources, the same set the build fingerprinted (02 §7.6).
        var resources = domain.Section<ResourcesSection>(domain.Resources, ConfigLoader.SectionOptions)
                        ?? new ResourcesSection();

        var ui = domain.Section<UiResourcesSection>(domain.Ui, ConfigLoader.SectionOptions)
                 ?? new UiResourcesSection();

        var files = reader.AllFiles()
            .Select(file => Path.Combine(options.Project, file.Path))
            .Concat(ResourceFiles.Enumerate(options.Project, resources, ui)
                .Select(file => Path.Combine(options.Project, file)));

        var current = Fingerprint.Compute(files, ConfigLoader.ResolveConfigDirectory(options.ConfigDirectory));
        var stored = meta.GetValueOrDefault(MetaKeys.Fingerprint);

        var differences = Fingerprint.Differences(Fingerprint.Value.TryParse(stored), current);
        return (differences.Count > 0, true, differences);
    }

    private static int Succeed(
        JsonNode? data,
        Stopwatch clock,
        CliOptions options,
        string profileId,
        string derivation,
        bool stale)
    {
        Write(
            new JsonObject
            {
                ["ok"] = true,
                ["data"] = data,
                ["meta"] = Meta(clock, profileId, derivation, stale)
            },
            options.Pretty);

        return ExitCodes.Ok;
    }

    private static int Fail(string code, string message, Stopwatch clock, JsonObject? details = null)
    {
        var error = new JsonObject
        {
            ["code"] = code,
            ["message"] = message
        };

        if (details is not null) error["details"] = details;

        Write(
            new JsonObject
            {
                ["ok"] = false,
                ["error"] = error,
                ["meta"] = Meta(clock, profileId: null, derivation: null, stale: false)
            },
            pretty: false);

        return ExitCodes.For(code);
    }

    private static JsonObject Meta(Stopwatch clock, string? profileId, string? derivation, bool stale)
    {
        var meta = new JsonObject
        {
            ["stale"] = stale,
            ["truncated"] = false,
            ["truncatedBy"] = new JsonArray(),
            ["elapsedMs"] = clock.ElapsedMilliseconds,
            ["irVersion"] = ScreenFlowVersion.IrSchema,
            ["rulesVersion"] = ScreenFlowVersion.Rules
        };

        if (profileId is not null) meta["profileId"] = profileId;
        if (derivation is not null) meta["derivation"] = derivation;

        return meta;
    }

    private static void Write(JsonObject envelope, bool pretty)
    {
        var options = pretty ? PrettyOptions : SerializerOptions;
        Console.Out.WriteLine(envelope.ToJsonString(options));
        Console.Out.Flush();
    }

    private static void TrySetUtf8()
    {
        try
        {
            Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
        catch (IOException)
        {
            // A redirected handle that refuses the change; the bytes are UTF-8 either way.
        }
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    private static readonly JsonSerializerOptions PrettyOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };
}

/// <summary>Error codes of 02 §11.3.</summary>
public static class ErrorCodes
{
    public const string NoCompilationInput = "E_NO_COMPILATION_INPUT";
    public const string ProfileNotFound = "E_PROFILE_NOT_FOUND";
    public const string IndexNotFound = "E_INDEX_NOT_FOUND";
    public const string InvalidInput = "E_INVALID_INPUT";
    public const string NotFound = "E_NOT_FOUND";
    public const string Ambiguous = "E_AMBIGUOUS";
    public const string LimitExceeded = "E_LIMIT_EXCEEDED";
    public const string IrSchema = "E_IR_SCHEMA";
    public const string IdCollision = "E_ID_COLLISION";
    public const string Internal = "E_INTERNAL";

    public static readonly IReadOnlyList<string> All = new[]
    {
        NoCompilationInput, ProfileNotFound, IndexNotFound, InvalidInput, NotFound,
        Ambiguous, LimitExceeded, IrSchema, IdCollision, Internal
    };
}

/// <summary>Exit codes of 02 §11.3.</summary>
public static class ExitCodes
{
    public const int Ok = 0;
    public const int InvalidInput = 2;
    public const int NotFound = 3;
    public const int BuildFailed = 4;
    public const int Internal = 5;

    public static int For(string errorCode) => errorCode switch
    {
        ErrorCodes.InvalidInput or ErrorCodes.ProfileNotFound or ErrorCodes.Ambiguous => InvalidInput,
        ErrorCodes.NotFound or ErrorCodes.IndexNotFound => NotFound,
        ErrorCodes.NoCompilationInput or ErrorCodes.IrSchema or ErrorCodes.IdCollision => BuildFailed,
        _ => Internal
    };
}

/// <summary>An error that maps straight onto the envelope of 02 §11.3.</summary>
public sealed class CliException : Exception
{
    public CliException(string code, string message, JsonObject? details = null)
        : base(message)
    {
        Code = code;
        Details = details;
    }

    public string Code { get; }

    public JsonObject? Details { get; }
}

/// <summary>The common options of 02 §11.1.</summary>
public sealed record CliOptions
{
    public string Command { get; init; } = string.Empty;
    public string? Project { get; init; }
    public string? Profile { get; init; }
    public string? Db { get; init; }
    public string? ConfigDirectory { get; init; }
    public string? Locale { get; init; }
    public string? Out { get; init; }
    public int Parallel { get; init; }
    public bool Pretty { get; init; }

    public string? Id { get; init; }
    public string? Terminal { get; init; }
    public string? Direction { get; init; }
    public int? Limit { get; init; }
    public string? From { get; init; }
    public string? To { get; init; }
    public string? TerminalFrom { get; init; }
    public string? TerminalTo { get; init; }
    public int? K { get; init; }
    public int? MaxHops { get; init; }
    public bool IncludePrefix { get; init; }
    public string? Detail { get; init; }
    public string? Category { get; init; }
    public string? Screen { get; init; }
    public string? Edge { get; init; }
    public string? Name { get; init; }
    public string? Kind { get; init; }
    public string? Code { get; init; }
    public string? Key { get; init; }
    public string? Collection { get; init; }
    public int? Context { get; init; }
    public string? File { get; init; }
    public int? FromLine { get; init; }
    public int? ToLine { get; init; }
    public string? KeyContains { get; init; }
    public string? TargetScreen { get; init; }

    public string RequireProject()
        => Project ?? throw new CliException(
            ErrorCodes.InvalidInput,
            $"{Command} requires --project <Unity project root>.");

    public string Require(string option)
    {
        var value = option switch
        {
            nameof(Id) => Id,
            nameof(From) => From,
            nameof(To) => To,
            nameof(Name) => Name,
            nameof(Key) => Key,
            nameof(Code) => Code,
            nameof(File) => File,
            _ => null
        };

        return value ?? throw new CliException(ErrorCodes.InvalidInput, $"{Command} requires --{option}.");
    }

    public static CliOptions Parse(string[] args)
    {
        var words = new List<string>();
        var options = new CliOptions();

        for (var i = 0; i < args.Length; i++)
        {
            var argument = args[i];

            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                words.Add(argument);
                continue;
            }

            switch (argument)
            {
                case "--pretty":
                    options = options with { Pretty = true };
                    break;
                case "--project":
                    options = options with { Project = Next(args, ref i, argument) };
                    break;
                case "--profile":
                    options = options with { Profile = Next(args, ref i, argument) };
                    break;
                case "--db":
                    options = options with { Db = Next(args, ref i, argument) };
                    break;
                case "--config-dir":
                    options = options with { ConfigDirectory = Next(args, ref i, argument) };
                    break;
                case "--locale":
                    options = options with { Locale = Next(args, ref i, argument) };
                    break;
                case "--out":
                    options = options with { Out = Next(args, ref i, argument) };
                    break;
                case "--parallel":
                    options = options with { Parallel = ParseCount(Next(args, ref i, argument), argument) };
                    break;
                case "--id":
                    options = options with { Id = Next(args, ref i, argument) };
                    break;
                case "--terminal":
                    options = options with { Terminal = Next(args, ref i, argument) };
                    break;
                case "--direction":
                    options = options with { Direction = Next(args, ref i, argument) };
                    break;
                case "--limit":
                    options = options with { Limit = ParseCount(Next(args, ref i, argument), argument) };
                    break;
                case "--from":
                    options = options with { From = Next(args, ref i, argument) };
                    break;
                case "--to":
                    options = options with { To = Next(args, ref i, argument) };
                    break;
                case "--terminal-from":
                    options = options with { TerminalFrom = Next(args, ref i, argument) };
                    break;
                case "--terminal-to":
                    options = options with { TerminalTo = Next(args, ref i, argument) };
                    break;
                case "--k":
                    options = options with { K = ParseCount(Next(args, ref i, argument), argument) };
                    break;
                case "--max-hops":
                    options = options with { MaxHops = ParseCount(Next(args, ref i, argument), argument) };
                    break;
                case "--include-prefix":
                    options = options with { IncludePrefix = true };
                    break;
                case "--detail":
                    options = options with { Detail = Next(args, ref i, argument) };
                    break;
                case "--category":
                    options = options with { Category = Next(args, ref i, argument) };
                    break;
                case "--screen":
                    options = options with { Screen = Next(args, ref i, argument) };
                    break;
                case "--edge":
                    options = options with { Edge = Next(args, ref i, argument) };
                    break;
                case "--name":
                    options = options with { Name = Next(args, ref i, argument) };
                    break;
                case "--kind":
                    options = options with { Kind = Next(args, ref i, argument) };
                    break;
                case "--code":
                    options = options with { Code = Next(args, ref i, argument) };
                    break;
                case "--key":
                    options = options with { Key = Next(args, ref i, argument) };
                    break;
                case "--collection":
                    options = options with { Collection = Next(args, ref i, argument) };
                    break;
                case "--context":
                    options = options with { Context = ParseNonNegative(Next(args, ref i, argument), argument) };
                    break;
                case "--file":
                    options = options with { File = Next(args, ref i, argument) };
                    break;
                case "--from-line":
                    options = options with { FromLine = ParseCount(Next(args, ref i, argument), argument) };
                    break;
                case "--to-line":
                    options = options with { ToLine = ParseCount(Next(args, ref i, argument), argument) };
                    break;
                case "--key-contains":
                    options = options with { KeyContains = Next(args, ref i, argument) };
                    break;
                case "--target-screen":
                    options = options with { TargetScreen = Next(args, ref i, argument) };
                    break;
                default:
                    throw new CliException(ErrorCodes.InvalidInput, $"Unknown option '{argument}'.");
            }
        }

        return options with { Command = string.Join(' ', words) };
    }

    private static string Next(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
            throw new CliException(ErrorCodes.InvalidInput, $"Option '{option}' needs a value.");

        return args[++index];
    }

    private static int ParseCount(string value, string option)
        => int.TryParse(value, out var parsed) && parsed > 0
            ? parsed
            : throw new CliException(ErrorCodes.InvalidInput, $"Option '{option}' needs a positive number.");

    private static int ParseNonNegative(string value, string option)
        => int.TryParse(value, out var parsed) && parsed >= 0
            ? parsed
            : throw new CliException(ErrorCodes.InvalidInput, $"Option '{option}' needs a non-negative number.");
}
