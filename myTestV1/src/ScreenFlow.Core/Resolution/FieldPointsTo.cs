using System.Collections.Concurrent;
using ScreenFlow.Core.Ir;
using ScreenFlow.Core.Storage;

namespace ScreenFlow.Core.Resolution;

/// <summary>
/// What a field or property can point at (02 §8.2, field points-to).
/// <para>
/// This is the step that makes an interface call resolvable: a view holds its presenter in
/// <c>eventHandler</c>, the presenter assigns itself there once, and only by collecting that
/// assignment does <c>eventHandler.OnClickBtnOther()</c> have a known target (03 §9).
/// </para>
/// <para>
/// Every assignment to the field is collected — never the first or the most likely one. Assignments
/// made through a receiver of an unrelated type are the one thing filtered out, because those
/// belong to a different object.
/// </para>
/// </summary>
public sealed class FieldPointsTo
{
    private readonly IndexReader _reader;
    private readonly ResolutionIndex _index;

    private readonly ConcurrentDictionary<string, IReadOnlyList<AssignRec>> _assignments =
        new(StringComparer.Ordinal);

    public FieldPointsTo(IndexReader reader, ResolutionIndex index)
    {
        _reader = reader;
        _index = index;
    }

    /// <summary>Every assignment whose target is this symbol, ordered by id. Cached.</summary>
    public IReadOnlyList<AssignRec> AssignmentsTo(string symbolId)
        => _assignments.GetOrAdd(
            symbolId,
            id => _reader.AssignsTargeting(id)
                .OrderBy(a => a.Id, StringComparer.Ordinal)
                .ToList());

    /// <summary>
    /// The values a field can hold: its initialiser plus every assignment made through a receiver
    /// whose static type is the same as, or related by inheritance to,
    /// <paramref name="receiverTypeId"/> (02 §8.2).
    /// </summary>
    public FieldTargets TargetsOf(string symbolId, string? receiverTypeId)
    {
        var values = new List<Value>();
        var kept = new List<AssignRec>();

        var symbol = _index.Symbol(symbolId);
        if (symbol?.Initializer is not null) values.Add(symbol.Initializer);

        foreach (var assignment in AssignmentsTo(symbolId))
        {
            if (!ReceiverMatches(assignment, receiverTypeId)) continue;

            kept.Add(assignment);
            values.Add(assignment.Value);
        }

        return new FieldTargets(values, kept, symbol);
    }

    /// <summary>
    /// Whether an assignment was made through a compatible receiver. An assignment with no
    /// receiver — a static field, or one written as a bare name inside its own type — always
    /// counts: there is no other object it could belong to.
    /// </summary>
    private bool ReceiverMatches(AssignRec assignment, string? receiverTypeId)
    {
        if (receiverTypeId is null) return true;

        var assignedThrough = ReceiverTypeOf(assignment.Target);
        if (assignedThrough is null) return true;

        return _index.IsRelated(assignedThrough, receiverTypeId);
    }

    public static string? ReceiverTypeOf(Value target) => target switch
    {
        MemberAccessValue member => member.Receiver.Type?.Id,
        _ => null
    };
}

/// <summary>The values a field can hold, with the assignments they came from (02 §8.2).</summary>
public sealed record FieldTargets(
    IReadOnlyList<Value> Values,
    IReadOnlyList<AssignRec> Assignments,
    SymbolRec? Symbol)
{
    public bool IsEmpty => Values.Count == 0;
}
