using System.Text.Json.Serialization;
using ScreenFlow.Core.Ir;

namespace ScreenFlow.Core.Graph;

/// <summary>One end of a screen edge: a screen on a terminal (02 §10.4).</summary>
public sealed record ScreenRef
{
    [JsonPropertyName("terminal")] public required string Terminal { get; init; }
    [JsonPropertyName("screenId")] public required string ScreenId { get; init; }

    public override string ToString() => Terminal + "/" + ScreenId;
}

/// <summary>What started a chain (02 §10.4).</summary>
public sealed record EdgeTrigger
{
    [JsonPropertyName("kind")] public required string Kind { get; init; }
    [JsonPropertyName("controlId")] public string? ControlId { get; init; }
    [JsonPropertyName("entryMethodId")] public string? EntryMethodId { get; init; }
}

/// <summary>
/// One way to get from one screen to another, with the chain that does it (02 §10.4).
/// <para>
/// A dangling edge — <see cref="To"/> null, <see cref="EndsAtBreak"/> set — is as much a result as
/// a complete one: it says the operation leads somewhere the source does not name, and why.
/// </para>
/// </summary>
public sealed record ScreenEdge
{
    [JsonPropertyName("edgeId")] public required string EdgeId { get; init; }
    [JsonPropertyName("from")] public required ScreenRef From { get; init; }

    /// <summary>Null for a dangling edge (02 §10.4).</summary>
    [JsonPropertyName("to")] public ScreenRef? To { get; init; }

    [JsonPropertyName("trigger")] public required EdgeTrigger Trigger { get; init; }
    [JsonPropertyName("confidence")] public required string Confidence { get; init; }

    /// <summary>Conditions the whole chain runs under, in the words of 02 §10.4.</summary>
    [JsonPropertyName("conditions")] public IReadOnlyList<string> Conditions { get; init; } = Array.Empty<string>();

    [JsonPropertyName("chain")] public IReadOnlyList<ChainStep> Chain { get; init; } = Array.Empty<ChainStep>();
    [JsonPropertyName("breakIds")] public IReadOnlyList<string> BreakIds { get; init; } = Array.Empty<string>();
    [JsonPropertyName("endsAtBreak")] public string? EndsAtBreak { get; init; }
    [JsonPropertyName("cost")] public required double Cost { get; init; }
    [JsonPropertyName("truncated")] public bool Truncated { get; init; }

    /// <summary>Edge id prefix (02 §5.6: a derived id is a prefix plus a hash).</summary>
    public const string Prefix = "se_";

    [JsonIgnore] public bool IsDangling => To is null;

    /// <summary>
    /// The cost formula of 02 §10.4, verbatim. Used to rank chains, so it has to be the same
    /// everywhere: the path search ranks by the same number the graph stored.
    /// </summary>
    public static double CostOf(bool candidate, bool deferred, int breaks, bool dangling)
        => 1.0
           + (candidate ? 0.5 : 0.0)
           + (deferred ? 0.2 : 0.0)
           + 0.75 * breaks
           + (dangling ? 2.0 : 0.0);

    /// <summary>
    /// The identity of an edge: the two ends, what triggered it, and the chain that gets there.
    /// Two chains between the same screens are two edges, which is what 02 §10.4 ranks and keeps.
    /// </summary>
    public static string IdOf(
        ScreenRef from,
        ScreenRef? to,
        EdgeTrigger trigger,
        IEnumerable<string> chainMethodIds,
        string? endsAtBreak)
        => Ids.DerivedId(Prefix, new
        {
            from = from.ToString(),
            to = to?.ToString(),
            trigger = trigger.Kind,
            control = trigger.ControlId,
            entry = trigger.EntryMethodId,
            chain = chainMethodIds.ToList(),
            endsAtBreak
        });
}
