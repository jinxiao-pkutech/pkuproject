using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScreenFlow.Core.Config;

/// <summary>A terminal and the terminal it is paired with over TCP (02 §4.2).</summary>
public sealed record TerminalConfig
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("peer")] public required string Peer { get; init; }
}

/// <summary>Traversal limits (02 §4.2). Every limit that fires is reported, never hidden.</summary>
public sealed record TraversalConfig
{
    [JsonPropertyName("maxChainDepth")] public required int MaxChainDepth { get; init; }
    [JsonPropertyName("maxCandidatesPerDispatch")] public required int MaxCandidatesPerDispatch { get; init; }
    [JsonPropertyName("maxChainsPerScreenEdge")] public required int MaxChainsPerScreenEdge { get; init; }
    [JsonPropertyName("maxParamPropagationDepth")] public required int MaxParamPropagationDepth { get; init; }

    /// <summary>Namespaces the traversal does not descend into: framework code, not app logic.</summary>
    [JsonPropertyName("doNotDescendNamespaces")] public IReadOnlyList<string> DoNotDescendNamespaces { get; init; } = Array.Empty<string>();
}

/// <summary>
/// The retained change-marker pattern (02 §4.2). Markers are reporting metadata only: comment text
/// is never a parsing fact (CLAUDE.md §4).
/// </summary>
public sealed record ChangeMarkersConfig
{
    [JsonPropertyName("pattern")] public required string Pattern { get; init; }
}

/// <summary>
/// <c>config/unitypos.json</c>.
/// <para>
/// The core parses only <see cref="Terminals"/>, <see cref="Traversal"/> and
/// <see cref="ChangeMarkers"/>. Every other section is held as a raw <see cref="JsonElement"/> and
/// interpreted by <c>ScreenFlow.UnityPos</c>, so adding a domain anchor does not change the core
/// (02 §1.1). The sections are still declared here by name so that a typo in a top-level key is
/// rejected at load time instead of silently disabling an adapter.
/// </para>
/// </summary>
public sealed record DomainConfig
{
    [JsonPropertyName("schemaVersion")] public required string SchemaVersion { get; init; }

    [JsonPropertyName("terminals")] public IReadOnlyList<TerminalConfig> Terminals { get; init; } = Array.Empty<TerminalConfig>();
    [JsonPropertyName("traversal")] public required TraversalConfig Traversal { get; init; }
    [JsonPropertyName("changeMarkers")] public required ChangeMarkersConfig ChangeMarkers { get; init; }

    [JsonPropertyName("screens")] public JsonElement Screens { get; init; }
    [JsonPropertyName("enumerations")] public JsonElement Enumerations { get; init; }
    [JsonPropertyName("navigation")] public JsonElement Navigation { get; init; }
    [JsonPropertyName("tcp")] public JsonElement Tcp { get; init; }
    [JsonPropertyName("keyseq")] public JsonElement Keyseq { get; init; }
    [JsonPropertyName("di")] public JsonElement Di { get; init; }
    [JsonPropertyName("device")] public JsonElement Device { get; init; }
    [JsonPropertyName("api")] public JsonElement Api { get; init; }
    [JsonPropertyName("native")] public JsonElement Native { get; init; }
    [JsonPropertyName("sql")] public JsonElement Sql { get; init; }
    [JsonPropertyName("queue")] public JsonElement Queue { get; init; }
    [JsonPropertyName("deferredInvokers")] public JsonElement DeferredInvokers { get; init; }
    [JsonPropertyName("ui")] public JsonElement Ui { get; init; }
    [JsonPropertyName("resources")] public JsonElement Resources { get; init; }

    /// <summary>The peer terminal of <paramref name="terminalId"/>, or null when it is not declared.</summary>
    public string? PeerOf(string terminalId)
        => Terminals.FirstOrDefault(t => string.Equals(t.Id, terminalId, StringComparison.Ordinal))?.Peer;

    public bool HasTerminal(string terminalId)
        => Terminals.Any(t => string.Equals(t.Id, terminalId, StringComparison.Ordinal));

    /// <summary>
    /// Reads a domain section as a typed value. Used by the domain adapters, which own the shape of
    /// their own section; the core never looks inside.
    /// </summary>
    public T? Section<T>(JsonElement section, JsonSerializerOptions options)
        => section.ValueKind == JsonValueKind.Undefined ? default : section.Deserialize<T>(options);
}
