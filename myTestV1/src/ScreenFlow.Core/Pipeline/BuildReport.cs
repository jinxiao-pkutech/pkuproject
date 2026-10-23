using System.Text.Json.Serialization;
using ScreenFlow.Core.Ir;

namespace ScreenFlow.Core.Pipeline;

/// <summary>
/// What one build produced: the <c>data</c> of <c>index build</c> (02 §11.2).
/// <para>
/// The diagnostic summary and the disabled-feature list are part of the answer, not a log: a build
/// that could not resolve an anchor still succeeds, and the caller has to be told which questions
/// the index can no longer answer (02 §4.3).
/// </para>
/// </summary>
public sealed record BuildReport
{
    [JsonPropertyName("profileId")] public required string ProfileId { get; init; }
    [JsonPropertyName("derivation")] public required string Derivation { get; init; }
    [JsonPropertyName("platformDetected")] public string? PlatformDetected { get; init; }
    [JsonPropertyName("unityDeclared")] public string? UnityDeclared { get; init; }
    [JsonPropertyName("unityCache")] public string? UnityCache { get; init; }

    /// <summary>Project-root relative where possible; absolute when the db lives elsewhere.</summary>
    [JsonPropertyName("db")] public required string DbPath { get; init; }

    [JsonPropertyName("counts")] public required IReadOnlyDictionary<string, long> Counts { get; init; }

    /// <summary>Diagnostic counts by code (02 §11.2).</summary>
    [JsonPropertyName("diagnostics")] public required IReadOnlyDictionary<string, int> Diagnostics { get; init; }

    [JsonPropertyName("diagnosticsBySeverity")]
    public required IReadOnlyDictionary<string, int> DiagnosticsBySeverity { get; init; }

    /// <summary>Features switched off because an anchor could not be resolved (02 §4.3).</summary>
    [JsonPropertyName("disabledFeatures")]
    public IReadOnlyList<string> DisabledFeatures { get; init; } = Array.Empty<string>();

    [JsonPropertyName("fingerprint")] public required string Fingerprint { get; init; }
    [JsonPropertyName("elapsedMs")] public required long ElapsedMs { get; init; }

    /// <summary>Phases that ran, in order, for a build that is inspected after the fact.</summary>
    [JsonPropertyName("phases")] public IReadOnlyList<string> Phases { get; init; } = Array.Empty<string>();

    [JsonIgnore] public int ErrorCount => DiagnosticsBySeverity.TryGetValue(Severities.Error, out var n) ? n : 0;

    [JsonIgnore]
    public int WarningCount => DiagnosticsBySeverity.TryGetValue(Severities.Warning, out var n) ? n : 0;

    public long CountOf(string table) => Counts.TryGetValue(table, out var count) ? count : 0;
}
