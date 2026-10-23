using System.Text.Json.Serialization;

namespace ScreenFlow.Core.Config;

/// <summary>Which response files a profile selects (02 §4.1).</summary>
public sealed record RspConfig
{
    [JsonPropertyName("root")] public required string Root { get; init; }

    /// <summary>
    /// <c>android</c> selects response files whose defines contain <c>UNITY_ANDROID</c> and not
    /// <c>UNITY_EDITOR</c>; <c>editor</c> those containing <c>UNITY_EDITOR</c>; <c>any</c> all.
    /// </summary>
    [JsonPropertyName("selectPlatform")] public required string SelectPlatform { get; init; }

    /// <summary>Name fragments that mark a response file as not a compilation input.</summary>
    [JsonPropertyName("excludeNamePatterns")] public IReadOnlyList<string> ExcludeNamePatterns { get; init; } = Array.Empty<string>();

    public static class Platforms
    {
        public const string Android = "android";
        public const string Editor = "editor";
        public const string Any = "any";

        public static readonly IReadOnlyList<string> All = new[] { Android, Editor, Any };
    }
}

/// <summary>
/// Preprocessor symbol overrides (02 §4.1). <c>add</c> applies only when the snapshot is
/// <c>derived</c>, i.e. the cache was produced for a different platform than the profile declares.
/// </summary>
public sealed record DefinesConfig
{
    [JsonPropertyName("add")] public IReadOnlyList<string> Add { get; init; } = Array.Empty<string>();
    [JsonPropertyName("remove")] public IReadOnlyList<string> Remove { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Which assemblies IR is extracted from (02 §4.1). Assemblies outside <c>include</c> still take
/// part in compilation as references; they just produce no IR of their own.
/// </summary>
public sealed record AssembliesConfig
{
    [JsonPropertyName("include")] public IReadOnlyList<string> Include { get; init; } = Array.Empty<string>();
    [JsonPropertyName("exclude")] public IReadOnlyList<string> Exclude { get; init; } = Array.Empty<string>();
}

/// <summary>One analysis profile (02 §4.1).</summary>
public sealed record ProfileConfig
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("platform")] public required string Platform { get; init; }
    [JsonPropertyName("rsp")] public required RspConfig Rsp { get; init; }
    [JsonPropertyName("defines")] public DefinesConfig Defines { get; init; } = new();
    [JsonPropertyName("assemblies")] public AssembliesConfig Assemblies { get; init; } = new();

    /// <summary>
    /// Permission to fall back to the host runtime's reference assemblies when the response file
    /// references no BCL. Only a fixture profile may set this: on a real project a missing
    /// reference must surface as a diagnostic rather than be papered over (02 §4.1).
    /// </summary>
    [JsonPropertyName("allowHostFrameworkReferences")] public bool AllowHostFrameworkReferences { get; init; }

    /// <summary>Profiles whose id starts with this may enable host framework references.</summary>
    public const string FixtureIdPrefix = "fixture";

    public bool IsFixture => Id.StartsWith(FixtureIdPrefix, StringComparison.Ordinal);
}

/// <summary><c>config/profiles.json</c> as a whole.</summary>
public sealed record ProfilesFile
{
    [JsonPropertyName("schemaVersion")] public required string SchemaVersion { get; init; }
    [JsonPropertyName("profiles")] public IReadOnlyList<ProfileConfig> Profiles { get; init; } = Array.Empty<ProfileConfig>();

    /// <summary>The default profile when <c>--profile</c> is not given (02 §11.1).</summary>
    public const string DefaultProfileId = "android-onsite";

    public ProfileConfig? Find(string id)
        => Profiles.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));
}
