using ScreenFlow.Core.Ir;

namespace ScreenFlow.Core.Graph;

/// <summary>
/// The keys a receiver was entered with (02 §10.1, §10.3).
/// <para>
/// Only the branches those keys can reach are followed. Without this a receiver with twenty
/// <c>else if</c> arms would appear to lead everywhere at once (03 §10.5).
/// </para>
/// </summary>
public sealed record BranchFilter
{
    public required string Channel { get; init; }

    public IReadOnlyList<string> Keys { get; init; } = Array.Empty<string>();

    /// <summary>Part of the visit key, so one method entered with different keys is visited twice.</summary>
    public string Hash => Ids.Sha1Short(Channel + "|" + string.Join('', Keys));
}

/// <summary>
/// Where a traversal is (02 §10.1).
/// <para>
/// Immutable: each step produces a new state, so a branch cannot change what its siblings see —
/// which is what keeps two chains through the same method from contaminating each other.
/// </para>
/// </summary>
public sealed record TraversalState
{
    public required string MethodId { get; init; }

    /// <summary>The terminal whose code is running; a TCP hop changes it to the peer (02 §10.2).</summary>
    public required string Terminal { get; init; }

    public BranchFilter? BranchFilter { get; init; }

    public int Depth { get; init; }

    public IReadOnlyList<ChainStep> Chain { get; init; } = Array.Empty<ChainStep>();

    /// <summary>Run-time conditions the chain depends on, in the words of 02 §10.4.</summary>
    public IReadOnlyList<string> Conditions { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> BreakIds { get; init; } = Array.Empty<string>();

    /// <summary>True once the chain has passed something that runs later (02 §10.2).</summary>
    public bool Deferred { get; init; }

    /// <summary>True once anything on the chain stopped being certain (02 §10.4).</summary>
    public bool Candidate { get; init; }

    /// <summary>Loop guard key (02 §10.2): the method, the terminal and the keys it was entered with.</summary>
    public string VisitKey => string.Concat(MethodId, "|", Terminal, "|", BranchFilter?.Hash ?? string.Empty);

    public TraversalState Into(string methodId, ChainStep step, bool clearBranchFilter = true) => this with
    {
        MethodId = methodId,
        Depth = Depth + 1,
        Chain = Chain.Append(step).ToList(),
        BranchFilter = clearBranchFilter ? null : BranchFilter
    };

    public TraversalState With(ChainStep step) => this with { Chain = Chain.Append(step).ToList() };

    public TraversalState WithCondition(string condition)
        => Conditions.Contains(condition, StringComparer.Ordinal)
            ? this
            : this with { Conditions = Conditions.Append(condition).ToList() };

    public TraversalState WithBreak(string breakId) => this with
    {
        BreakIds = BreakIds.Contains(breakId, StringComparer.Ordinal)
            ? BreakIds
            : BreakIds.Append(breakId).ToList(),

        // A break is exactly the point where the chain stops being certain (02 §10.4).
        Candidate = true
    };

    public TraversalState AsCandidate() => Candidate ? this : this with { Candidate = true };

    public TraversalState AsDeferred() => Deferred ? this : this with { Deferred = true };
}
