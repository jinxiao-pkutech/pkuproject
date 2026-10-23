using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ScreenFlow.Core.Config;

/// <summary>
/// A configuration file is malformed or internally inconsistent. The CLI maps this onto
/// <c>E_INVALID_INPUT</c> (02 §11.3).
/// </summary>
public sealed class ConfigException : Exception
{
    public ConfigException(string message, IReadOnlyList<string> problems, Exception? inner = null)
        : base(Format(message, problems), inner)
        => Problems = problems;

    public ConfigException(string message, Exception? inner = null)
        : this(message, Array.Empty<string>(), inner) { }

    public IReadOnlyList<string> Problems { get; }

    private static string Format(string message, IReadOnlyList<string> problems)
        => problems.Count == 0
            ? message
            : message + Environment.NewLine + string.Join(Environment.NewLine, problems.Select(p => "  - " + p));
}

/// <summary>
/// Loads and validates <c>config/profiles.json</c> and <c>config/unitypos.json</c> (02 §4).
/// <para>
/// Unknown fields are rejected rather than ignored: a mistyped anchor key would otherwise read as
/// "anchor absent", which silently disables an adapter instead of failing the build.
/// </para>
/// </summary>
public static class ConfigLoader
{
    public const string ProfilesFileName = "profiles.json";
    public const string DomainFileName = "unitypos.json";

    private static readonly JsonSerializerOptions StrictOptions = CreateStrictOptions();

    /// <summary>
    /// Options the domain adapters use to read their own sections. Same conventions as the loader,
    /// but tolerant of unmapped members, because a section's shape is owned by its adapter and the
    /// core must not force every key to be modelled.
    /// </summary>
    public static JsonSerializerOptions SectionOptions { get; } = CreateSectionOptions();

    private static JsonSerializerOptions CreateStrictOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            NumberHandling = JsonNumberHandling.Strict
        };
        // Frozen so the shared instance cannot be mutated after first use; the flag installs
        // the reflection-based resolver, which MakeReadOnly otherwise demands up front.
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    private static JsonSerializerOptions CreateSectionOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            NumberHandling = JsonNumberHandling.Strict
        };
        // Frozen so the shared instance cannot be mutated after first use; the flag installs
        // the reflection-based resolver, which MakeReadOnly otherwise demands up front.
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    public static ProfilesFile LoadProfiles(string path)
    {
        var file = Deserialize<ProfilesFile>(path, "profiles");
        var problems = ValidateProfiles(file);
        if (problems.Count > 0)
            throw new ConfigException($"{path} is not a valid profiles file.", problems);
        return file;
    }

    public static DomainConfig LoadDomain(string path)
    {
        var config = Deserialize<DomainConfig>(path, "domain");
        var problems = ValidateDomain(config);
        if (problems.Count > 0)
            throw new ConfigException($"{path} is not a valid domain configuration.", problems);
        return config;
    }

    private static T Deserialize<T>(string path, string what)
    {
        if (!File.Exists(path))
            throw new ConfigException($"The {what} configuration file was not found at {path}.");

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (IOException ex)
        {
            throw new ConfigException($"The {what} configuration file at {path} could not be read.", ex);
        }

        try
        {
            return JsonSerializer.Deserialize<T>(text, StrictOptions)
                   ?? throw new ConfigException($"The {what} configuration file at {path} is empty.");
        }
        catch (JsonException ex)
        {
            throw new ConfigException($"The {what} configuration file at {path} is not valid: {ex.Message}", ex);
        }
    }

    // ---------------------------------------------------------------- validation

    internal static IReadOnlyList<string> ValidateProfiles(ProfilesFile file)
    {
        var problems = new List<string>();

        if (file.Profiles.Count == 0) problems.Add("'profiles' is empty.");

        foreach (var duplicate in file.Profiles
                     .GroupBy(p => p.Id, StringComparer.Ordinal)
                     .Where(g => g.Count() > 1)
                     .Select(g => g.Key)
                     .OrderBy(id => id, StringComparer.Ordinal))
        {
            problems.Add($"profile id '{duplicate}' is declared more than once.");
        }

        foreach (var profile in file.Profiles)
        {
            if (profile.Id.Length == 0) problems.Add("a profile has an empty id.");
            if (profile.Platform.Length == 0) problems.Add($"profile '{profile.Id}': 'platform' is empty.");

            if (!RspConfig.Platforms.All.Contains(profile.Rsp.SelectPlatform, StringComparer.Ordinal))
            {
                problems.Add(
                    $"profile '{profile.Id}': rsp.selectPlatform is '{profile.Rsp.SelectPlatform}', " +
                    $"expected one of {string.Join(", ", RspConfig.Platforms.All)}.");
            }

            if (profile.Rsp.Root.Length == 0) problems.Add($"profile '{profile.Id}': 'rsp.root' is empty.");

            // Only a fixture may substitute the host runtime's reference assemblies; on a real
            // project a missing reference has to stay a diagnostic (02 §4.1).
            if (profile.AllowHostFrameworkReferences && !profile.IsFixture)
            {
                problems.Add(
                    $"profile '{profile.Id}': allowHostFrameworkReferences is only permitted for profiles whose " +
                    $"id starts with '{ProfileConfig.FixtureIdPrefix}'.");
            }
        }

        return problems;
    }

    internal static IReadOnlyList<string> ValidateDomain(DomainConfig config)
    {
        var problems = new List<string>();

        if (config.Terminals.Count == 0) problems.Add("'terminals' is empty.");

        foreach (var duplicate in config.Terminals
                     .GroupBy(t => t.Id, StringComparer.Ordinal)
                     .Where(g => g.Count() > 1)
                     .Select(g => g.Key)
                     .OrderBy(id => id, StringComparer.Ordinal))
        {
            problems.Add($"terminal id '{duplicate}' is declared more than once.");
        }

        foreach (var terminal in config.Terminals)
        {
            if (terminal.Id.Length == 0) { problems.Add("a terminal has an empty id."); continue; }

            if (string.Equals(terminal.Id, terminal.Peer, StringComparison.Ordinal))
            {
                problems.Add($"terminal '{terminal.Id}' is its own peer.");
                continue;
            }

            var peer = config.Terminals.FirstOrDefault(t => string.Equals(t.Id, terminal.Peer, StringComparison.Ordinal));
            if (peer is null)
            {
                problems.Add($"terminal '{terminal.Id}' names peer '{terminal.Peer}', which is not declared.");
                continue;
            }

            // The pairing has to be mutual, or a cross-terminal hop could be followed one way only.
            if (!string.Equals(peer.Peer, terminal.Id, StringComparison.Ordinal))
            {
                problems.Add(
                    $"terminal pairing is not mutual: '{terminal.Id}' points at '{terminal.Peer}', " +
                    $"but '{peer.Id}' points at '{peer.Peer}'.");
            }
        }

        var traversal = config.Traversal;
        AddIfNotPositive(problems, "traversal.maxChainDepth", traversal.MaxChainDepth);
        AddIfNotPositive(problems, "traversal.maxCandidatesPerDispatch", traversal.MaxCandidatesPerDispatch);
        AddIfNotPositive(problems, "traversal.maxChainsPerScreenEdge", traversal.MaxChainsPerScreenEdge);
        AddIfNotPositive(problems, "traversal.maxParamPropagationDepth", traversal.MaxParamPropagationDepth);

        if (config.ChangeMarkers.Pattern.Length == 0)
        {
            problems.Add("changeMarkers.pattern is empty.");
        }
        else
        {
            try
            {
                _ = new Regex(config.ChangeMarkers.Pattern);
            }
            catch (ArgumentException ex)
            {
                problems.Add($"changeMarkers.pattern is not a valid regular expression: {ex.Message}");
            }
        }

        return problems;
    }

    private static void AddIfNotPositive(List<string> problems, string name, int value)
    {
        if (value <= 0) problems.Add($"{name} must be greater than zero, found {value}.");
    }

    // ---------------------------------------------------------------- directory resolution

    /// <summary>
    /// Finds the configuration directory (T01 §4): <c>--config-dir</c>, then
    /// <c>&lt;executable directory&gt;/config</c>, then the documented development-time relative
    /// path, then <c>./config</c>.
    /// <para>
    /// A candidate counts only when it actually holds <c>profiles.json</c>, so an empty
    /// <c>config</c> directory beside the executable cannot shadow a populated one.
    /// </para>
    /// </summary>
    public static string ResolveConfigDirectory(string? fromCommandLine, string? baseDirectory = null)
    {
        var probed = new List<string>();
        foreach (var candidate in ConfigDirectoryCandidates(fromCommandLine, baseDirectory))
        {
            probed.Add(candidate);
            if (File.Exists(Path.Combine(candidate, ProfilesFileName))) return candidate;
        }

        if (fromCommandLine is not null)
        {
            throw new ConfigException(
                $"--config-dir '{fromCommandLine}' does not contain {ProfilesFileName}.");
        }

        throw new ConfigException(
            $"No configuration directory containing {ProfilesFileName} was found.",
            probed.Select(p => "probed: " + p).ToList());
    }

    /// <summary>The probe order, exposed so a diagnostic can show exactly what was tried.</summary>
    public static IReadOnlyList<string> ConfigDirectoryCandidates(string? fromCommandLine, string? baseDirectory = null)
    {
        var candidates = new List<string>();
        var exeDirectory = Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory);

        if (!string.IsNullOrWhiteSpace(fromCommandLine))
        {
            candidates.Add(Path.GetFullPath(fromCommandLine));
            return candidates;
        }

        candidates.Add(Path.Combine(exeDirectory, "config"));
        candidates.Add(Path.GetFullPath(Path.Combine(exeDirectory, "..", "..", "..", "..", "config")));

        // Documented order ends here. The relative probe above lands on <repo>/src/config for the
        // standard bin/<configuration>/<framework> output layout, so it does not find the repository
        // configuration during development; walking up for a directory that actually holds
        // profiles.json covers that without overriding any documented probe.
        for (var directory = new DirectoryInfo(exeDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "config");
            if (!candidates.Contains(candidate, StringComparer.OrdinalIgnoreCase)) candidates.Add(candidate);
        }

        candidates.Add(Path.GetFullPath("config"));
        return candidates;
    }

    /// <summary>Loads both files from one directory.</summary>
    public static (ProfilesFile Profiles, DomainConfig Domain) LoadAll(string configDirectory)
        => (LoadProfiles(Path.Combine(configDirectory, ProfilesFileName)),
            LoadDomain(Path.Combine(configDirectory, DomainFileName)));
}
