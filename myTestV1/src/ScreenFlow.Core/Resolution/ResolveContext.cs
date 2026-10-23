using ScreenFlow.Core.Config;

namespace ScreenFlow.Core.Resolution;

/// <summary>
/// The limits and surroundings one resolution runs under (02 §8.2).
/// <para>
/// Every limit here is reported when it fires rather than silently bounding the answer: a chain cut
/// at the depth limit is a break link with a stated reason (02 §8.3, <c>TruncatedByDepthLimit</c>),
/// not an absence of edges.
/// </para>
/// </summary>
public sealed record ResolveContext
{
    /// <summary>Whether a parameter may be followed back to its callers (02 §8.2).</summary>
    public bool AllowParamPropagation { get; init; } = true;

    public int MaxDepth { get; init; } = 40;

    public int MaxParamPropagationDepth { get; init; } = 2;

    public int Depth { get; init; }

    public int ParamDepth { get; init; }

    /// <summary>The type <c>this</c> refers to at the site being resolved.</summary>
    public string? CurrentTypeId { get; init; }

    /// <summary>Providers for the values the core cannot resolve on its own (02 §8.2).</summary>
    public IReadOnlyList<IValueProvider> Providers { get; init; } = Array.Empty<IValueProvider>();

    /// <summary>
    /// Keys already being resolved. Shared down the recursion on purpose: a field assigned from
    /// itself, or two fields assigned from each other, is a cycle the resolver has to stop at.
    /// </summary>
    public HashSet<string> Visited { get; init; } = new(StringComparer.Ordinal);

    public bool TooDeep => Depth >= MaxDepth;

    public bool ParamLimitReached => ParamDepth >= MaxParamPropagationDepth;

    public ResolveContext Deeper() => this with { Depth = Depth + 1 };

    public ResolveContext IntoCaller() => this with { Depth = Depth + 1, ParamDepth = ParamDepth + 1 };

    /// <summary>True when the key was not being resolved yet, and claims it.</summary>
    public bool Enter(string key) => Visited.Add(key);

    public void Leave(string key) => Visited.Remove(key);

    public static ResolveContext FromConfig(
        TraversalConfig traversal,
        IReadOnlyList<IValueProvider>? providers = null,
        string? currentTypeId = null)
        => new()
        {
            MaxDepth = traversal.MaxChainDepth,
            MaxParamPropagationDepth = traversal.MaxParamPropagationDepth,
            Providers = providers ?? Array.Empty<IValueProvider>(),
            CurrentTypeId = currentTypeId
        };
}
