using ScreenFlow.Core.Ir;

namespace ScreenFlow.Core.Resolution;

/// <summary>
/// What resolving one value produced (02 §8.2).
/// <para>
/// <see cref="Complete"/> is the whole point of this type: a resolution that found two of three
/// possible values is not an answer, and saying so is what keeps a break link honest instead of
/// presenting a partial set as the full one.
/// </para>
/// </summary>
public sealed record ValueResolution
{
    /// <summary>Concrete values the expression can hold: creations, lambdas, method references.</summary>
    public IReadOnlyList<Value> Values { get; init; } = Array.Empty<Value>();

    /// <summary>Concrete type ids the expression can hold.</summary>
    public IReadOnlyList<string> Types { get; init; } = Array.Empty<string>();

    /// <summary>Strings the expression can be, for <see cref="ValueResolver.ResolveStrings"/>.</summary>
    public IReadOnlyList<string> Strings { get; init; } = Array.Empty<string>();

    /// <summary>False when any part of the value could not be resolved.</summary>
    public bool Complete { get; init; } = true;

    /// <summary>Callsites a propagated parameter value came from (02 §8.2).</summary>
    public IReadOnlyList<string> SourceCallsites { get; init; } = Array.Empty<string>();

    /// <summary>Limits that fired while resolving, named so a report can say why it stopped.</summary>
    public IReadOnlyList<string> Limits { get; init; } = Array.Empty<string>();

    /// <summary>
    /// True when the set is complete but which member applies is decided at run time — several DI
    /// registrations for one service, for instance (02 §8.2, §9.4). The graph turns this into a
    /// break link rather than picking one (02 §8.3, <c>MultipleDiRegistrations</c>).
    /// </summary>
    public bool Ambiguous { get; init; }

    public static readonly ValueResolution Unresolved = new() { Complete = false };

    public static readonly ValueResolution Nothing = new();

    public static ValueResolution OfTypes(params string[] types) => new() { Types = types };

    public static ValueResolution OfValues(params Value[] values) => new() { Values = values };

    public static ValueResolution OfStrings(IEnumerable<string> strings, bool complete = true)
        => new()
        {
            Strings = strings.Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList(),
            Complete = complete
        };

    public static ValueResolution LimitedBy(string limit) => new() { Complete = false, Limits = new[] { limit } };

    /// <summary>
    /// Union of two resolutions. Incompleteness is contagious: merging a complete answer with an
    /// incomplete one gives an incomplete one, because the union is then also only partly known.
    /// </summary>
    public ValueResolution Merge(ValueResolution other) => new()
    {
        Values = Values.Concat(other.Values).ToList(),
        Types = Union(Types, other.Types),
        Strings = Union(Strings, other.Strings),
        Complete = Complete && other.Complete,
        Ambiguous = Ambiguous || other.Ambiguous,
        SourceCallsites = Union(SourceCallsites, other.SourceCallsites),
        Limits = Union(Limits, other.Limits)
    };

    public bool IsEmpty => Values.Count == 0 && Types.Count == 0 && Strings.Count == 0;

    private static IReadOnlyList<string> Union(IReadOnlyList<string> left, IReadOnlyList<string> right)
        => left.Count == 0 && right.Count == 0
            ? Array.Empty<string>()
            : left.Concat(right).Distinct(StringComparer.Ordinal).OrderBy(v => v, StringComparer.Ordinal).ToList();
}

/// <summary>Limit names reported in <see cref="ValueResolution.Limits"/>.</summary>
public static class ResolutionLimits
{
    public const string Depth = "maxDepth";
    public const string ParamPropagation = "maxParamPropagationDepth";
    public const string Cycle = "cycle";
    public const string NoProvider = "noValueProvider";
}
