namespace ScreenFlow.CSharp.Compilation;

/// <summary>Platform a response file was produced for (02 §6.1).</summary>
public static class RspPlatforms
{
    /// <summary>Defines contain <c>UNITY_ANDROID</c> and not <c>UNITY_EDITOR</c>.</summary>
    public const string Android = "android";

    /// <summary>Defines contain <c>UNITY_EDITOR</c>.</summary>
    public const string Editor = "editor";

    /// <summary>Neither marker is present, so the platform cannot be told from the defines.</summary>
    public const string Unknown = "unknown";

    public static readonly IReadOnlyList<string> All = new[] { Android, Editor, Unknown };
}

/// <summary>
/// One compilation unit as the Unity cache describes it: one response file, one assembly
/// (02 §6.1).
/// <para>
/// Paths are absolute here, because they are used to open files. They are made project-root
/// relative only when a fact about them is written into the index (02 §11.3).
/// </para>
/// </summary>
public sealed record AssemblyInput
{
    /// <summary>Assembly name: the <c>-out:</c> file name without its extension.</summary>
    public required string Name { get; init; }

    public required string OutPath { get; init; }

    public IReadOnlyList<string> Sources { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> References { get; init; } = Array.Empty<string>();

    /// <summary>Preprocessor symbols after the profile's overrides, sorted and de-duplicated.</summary>
    public IReadOnlyList<string> Defines { get; init; } = Array.Empty<string>();

    public string? LangVersion { get; init; }

    public bool AllowUnsafe { get; init; }

    /// <summary>The <c>-nullable:</c> value as written, or null when the option was absent.</summary>
    public string? Nullable { get; init; }

    /// <summary>Platform inferred from the defines, never from the directory name (03 §2).</summary>
    public required string Platform { get; init; }

    public required string RspPath { get; init; }

    /// <summary>
    /// Options the parser recognised as options but does not act on. Kept rather than dropped so a
    /// response file that carries something unexpected is visible instead of silently simplified.
    /// </summary>
    public IReadOnlyList<string> IgnoredOptions { get; init; } = Array.Empty<string>();

    /// <summary>Unity version inferred from a <c>UNITY_yyyy_m_p</c> define, e.g. 2022.3.68.</summary>
    public string? UnityVersionFromDefines { get; init; }

    /// <summary>
    /// Whether IR is extracted from this assembly (02 §4.1, <c>assemblies.include</c>). An assembly
    /// that is not extracted still takes part in compilation as a reference, so its symbols can be
    /// bound and named — it just contributes no facts of its own.
    /// </summary>
    public bool ExtractIr { get; init; } = true;
}
