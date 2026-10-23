using ScreenFlow.Core.Ir;

namespace ScreenFlow.Core.Graph;

/// <summary>One guard on a receiver's branch, reduced to the string tests it makes (02 §10.3).</summary>
public sealed record EventPathConstraint
{
    public bool Polarity { get; init; } = true;

    public string Mode { get; init; } = ConditionModes.All;

    public IReadOnlyList<StringTest> Tests { get; init; } = Array.Empty<StringTest>();

    public static EventPathConstraint Of(Condition condition, IReadOnlyList<StringTest> tests)
        => new() { Polarity = condition.Polarity, Mode = condition.Mode, Tests = tests };
}

/// <summary>
/// Which keys reach a branch, and whether all of them do (02 §10.3).
/// <para>
/// The distinction matters: a hop that only some of the sent event paths reach is a real hop, but
/// not a certain one, and it is recorded as a candidate rather than as a fact.
/// </para>
/// </summary>
public sealed record EventPathMatch
{
    public bool Matched { get; init; }

    /// <summary>True when every key satisfies the constraints, not only some of them.</summary>
    public bool AllKeys { get; init; }

    public IReadOnlyList<string> MatchingKeys { get; init; } = Array.Empty<string>();

    public static readonly EventPathMatch None = new();

    /// <summary>A callsite with no event path constraint is always processed (02 §10.3).</summary>
    public static EventPathMatch Unconstrained(IReadOnlyCollection<string> keys)
        => new() { Matched = true, AllKeys = true, MatchingKeys = keys.ToList() };
}

/// <summary>
/// Decides which branch of a receiver an event path reaches (02 §10.3).
/// <para>
/// Lives in the core because both the TCP adapter and the graph traversal need the same answer, and
/// the core cannot reference the domain adapters (02 §1.1). The names it works with — the indexer
/// key, the event paths — are passed in; none of them is written here.
/// </para>
/// </summary>
public static class EventPathMatcher
{
    /// <summary>Case-sensitive, as 02 §10.3 requires: these are protocol strings, not prose.</summary>
    public static bool Satisfies(string key, StringTest test) => test.Op switch
    {
        StringTestOps.EqualsOp => string.Equals(key, test.Literal, StringComparison.Ordinal),
        StringTestOps.SwitchLabel => string.Equals(key, test.Literal, StringComparison.Ordinal),
        StringTestOps.NotEquals => !string.Equals(key, test.Literal, StringComparison.Ordinal),
        StringTestOps.Contains => key.Contains(test.Literal, StringComparison.Ordinal),
        StringTestOps.StartsWith => key.StartsWith(test.Literal, StringComparison.Ordinal),
        StringTestOps.EndsWith => key.EndsWith(test.Literal, StringComparison.Ordinal),
        _ => false
    };

    /// <summary>The four polarity and mode combinations of 02 §10.3, for one key.</summary>
    public static bool Satisfies(string key, EventPathConstraint constraint)
    {
        if (constraint.Tests.Count == 0) return true;

        var all = constraint.Tests.All(test => Satisfies(key, test));
        var any = constraint.Tests.Any(test => Satisfies(key, test));

        return (constraint.Polarity, constraint.Mode) switch
        {
            (true, ConditionModes.All) => all,
            (true, ConditionModes.Any) => any,
            (false, ConditionModes.All) => !all,
            (false, ConditionModes.Any) => !any,
            _ => all
        };
    }

    public static EventPathMatch Match(IReadOnlyCollection<string> keys, EventPathConstraint constraint)
    {
        if (keys.Count == 0) return EventPathMatch.None;

        var matching = keys.Where(key => Satisfies(key, constraint)).ToList();

        return new EventPathMatch
        {
            Matched = matching.Count > 0,
            AllKeys = matching.Count == keys.Count,
            MatchingKeys = matching
        };
    }

    /// <summary>
    /// Every constraint has to be satisfied by at least one key (02 §10.3). The keys that satisfy
    /// all of them are the ones that actually reach the branch.
    /// </summary>
    public static EventPathMatch Match(
        IReadOnlyCollection<string> keys,
        IReadOnlyList<EventPathConstraint> constraints)
    {
        if (constraints.Count == 0) return EventPathMatch.Unconstrained(keys);
        if (keys.Count == 0) return EventPathMatch.None;

        var matching = keys
            .Where(key => constraints.All(constraint => Satisfies(key, constraint)))
            .ToList();

        return new EventPathMatch
        {
            Matched = matching.Count > 0,
            AllKeys = matching.Count == keys.Count,
            MatchingKeys = matching
        };
    }

    /// <summary>
    /// The event path constraints of a callsite's guards: the tests whose subject is the event path
    /// (02 §10.3). A guard that tests something else does not constrain the event path.
    /// </summary>
    public static IReadOnlyList<EventPathConstraint> ConstraintsOf(
        IEnumerable<Condition> conditions,
        string indexerKey)
    {
        var constraints = new List<EventPathConstraint>();

        foreach (var condition in conditions)
        {
            var tests = condition.Tests.Where(test => IsIndexerSubject(test.Subject, indexerKey)).ToList();
            if (tests.Count > 0) constraints.Add(EventPathConstraint.Of(condition, tests));
        }

        return constraints;
    }

    /// <summary>
    /// Whether a value reads <c>something[key]</c>, following a local to where it came from. That
    /// is the shape an event path is read in (03 §10.5).
    /// </summary>
    public static bool IsIndexerSubject(Value? value, string indexerKey) => value switch
    {
        null => false,
        ElementAccessValue element => element.Index.FoldedStrings().Contains(indexerKey, StringComparer.Ordinal),
        LocalValue { Origin: not null } local => IsIndexerSubject(local.Origin, indexerKey),
        _ => false
    };
}
