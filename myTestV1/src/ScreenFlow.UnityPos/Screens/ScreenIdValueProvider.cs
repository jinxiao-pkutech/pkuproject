using ScreenFlow.Core.Ir;
using ScreenFlow.Core.Resolution;
using ScreenFlow.Core.Storage;

namespace ScreenFlow.UnityPos.Screens;

/// <summary>Which screens a value can denote, and whether that is the whole set (02 §9.6).</summary>
public sealed record ScreenIdSet
{
    public IReadOnlyList<string> ScreenIds { get; init; } = Array.Empty<string>();

    /// <summary>False when some path through the value could not be resolved to a screen.</summary>
    public bool Complete { get; init; } = true;

    /// <summary>The source text that could not be resolved, for the break link's reason.</summary>
    public string? UnresolvedText { get; init; }

    public static readonly ScreenIdSet Nothing = new();

    public static ScreenIdSet Unresolved(string text) => new() { Complete = false, UnresolvedText = text };

    public ScreenIdSet Merge(ScreenIdSet other) => new()
    {
        ScreenIds = ScreenIds.Concat(other.ScreenIds)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList(),
        Complete = Complete && other.Complete,
        UnresolvedText = UnresolvedText ?? other.UnresolvedText
    };
}

/// <summary>
/// Teaches the core what a <c>ScreenID</c> is (02 §8.2, §9.1).
/// <para>
/// <c>ScreenID.SSAW0907.Id</c> is a screen id string, and that is POS knowledge: the core sees only
/// a static field with a property path. The same knowledge answers the other direction for
/// navigation — which screen a <c>ScreenID</c> argument denotes, even when it arrives through a
/// parameter or a field (03 §4, §8).
/// </para>
/// </summary>
public sealed class ScreenIdValueProvider : IValueProvider
{
    public const string ProviderName = "screenId";

    private readonly IndexReader _reader;
    private readonly FieldPointsTo _fields;
    private readonly string _idProperty;
    private readonly string _nameProperty;

    public ScreenIdValueProvider(
        string screenIdTypeId,
        IReadOnlyDictionary<string, ScreenDefinition> definitions,
        string idProperty,
        IndexReader reader,
        FieldPointsTo fields,
        string nameProperty = "Name")
    {
        ScreenIdTypeId = screenIdTypeId;
        Definitions = definitions;
        _idProperty = idProperty;
        _nameProperty = nameProperty;
        _reader = reader;
        _fields = fields;
    }

    public string Name => ProviderName;

    public string ScreenIdTypeId { get; }

    /// <summary>Screen definitions by the id of the <c>ScreenID</c> field that declares them.</summary>
    public IReadOnlyDictionary<string, ScreenDefinition> Definitions { get; }

    public ValueResolution? TryResolve(Value value, ResolveContext context)
    {
        switch (value)
        {
            case StaticMemberValue member when member.SymbolId is not null
                                               && Definitions.TryGetValue(member.SymbolId, out var definition)
                                               && member.Path.Count == 1:
            {
                if (string.Equals(member.Path[0], _idProperty, StringComparison.Ordinal))
                    return ValueResolution.OfStrings(new[] { definition.ScreenId });

                return string.Equals(member.Path[0], _nameProperty, StringComparison.Ordinal)
                       && definition.Name is not null
                    ? ValueResolution.OfStrings(new[] { definition.Name })
                    : null;
            }

            // screenID.Id where screenID arrived as a parameter or a local (02 §9.1 step 4, T12 §4):
            // the receiver has to be followed to the ScreenID fields it can hold.
            case MemberAccessValue access when access.SymbolId is not null:
            {
                var property = _reader.Symbol(access.SymbolId);
                if (property is null) return null;

                var wantsId = string.Equals(property.Name, _idProperty, StringComparison.Ordinal);
                var wantsName = string.Equals(property.Name, _nameProperty, StringComparison.Ordinal);
                if (!wantsId && !wantsName) return null;

                var screens = ScreenIdsOf(access.Receiver, context);
                if (screens.ScreenIds.Count == 0) return null;

                if (wantsId) return ValueResolution.OfStrings(screens.ScreenIds, screens.Complete);

                var names = screens.ScreenIds
                    .Select(id => Definitions.Values.FirstOrDefault(d => d.ScreenId == id)?.Name)
                    .OfType<string>()
                    .ToList();

                return names.Count == 0 ? null : ValueResolution.OfStrings(names, screens.Complete);
            }

            default:
                return null;
        }
    }

    /// <summary>
    /// The screens a value can denote (02 §9.6). Follows a local to its origin, a field to
    /// everything assigned to it and a parameter back to its callers — and reports the text it could
    /// not resolve rather than returning an empty set as if it were an answer.
    /// </summary>
    public ScreenIdSet ScreenIdsOf(Value value, ResolveContext context)
    {
        if (context.TooDeep) return ScreenIdSet.Unresolved(value.Text);

        switch (value)
        {
            case StaticMemberValue member when member.SymbolId is not null
                                               && Definitions.TryGetValue(member.SymbolId, out var definition):
            {
                // Either the field itself or its id property names the same screen.
                var addressesScreen = member.Path.Count == 0
                                      || (member.Path.Count == 1
                                          && string.Equals(member.Path[0], _idProperty, StringComparison.Ordinal));

                return addressesScreen
                    ? new ScreenIdSet { ScreenIds = new[] { definition.ScreenId } }
                    : ScreenIdSet.Unresolved(member.Text);
            }

            case LocalValue local:
                return local.Origin is null
                    ? ScreenIdSet.Unresolved(local.Text)
                    : ScreenIdsOf(local.Origin, context.Deeper());

            case ConditionalValue conditional:
                return ScreenIdsOf(conditional.WhenTrue, context.Deeper())
                    .Merge(ScreenIdsOf(conditional.WhenFalse, context.Deeper()));

            case MemberAccessValue member when member.SymbolId is not null:
            {
                var key = "screen-field:" + member.SymbolId;
                if (!context.Enter(key)) return ScreenIdSet.Unresolved(member.Text);

                try
                {
                    var targets = _fields.TargetsOf(member.SymbolId, FieldPointsTo.ReceiverTypeOf(member));
                    if (targets.IsEmpty) return ScreenIdSet.Unresolved(member.Text);

                    var result = ScreenIdSet.Nothing;
                    foreach (var assigned in targets.Values)
                        result = result.Merge(ScreenIdsOf(assigned, context.Deeper()));

                    return result;
                }
                finally
                {
                    context.Leave(key);
                }
            }

            case ParamValue parameter:
                return FromCallers(parameter, context);

            default:
                return ScreenIdSet.Unresolved(value.Text);
        }
    }

    private ScreenIdSet FromCallers(ParamValue parameter, ResolveContext context)
    {
        if (!context.AllowParamPropagation || context.ParamLimitReached)
            return ScreenIdSet.Unresolved(parameter.Text);

        var key = "screen-param:" + parameter.MethodId + ":" + parameter.Index;
        if (!context.Enter(key)) return ScreenIdSet.Unresolved(parameter.Text);

        try
        {
            var callsites = _reader.CallsitesTargeting(parameter.MethodId);
            if (callsites.Count == 0) return ScreenIdSet.Unresolved(parameter.Text);

            var result = ScreenIdSet.Nothing;
            var found = false;

            foreach (var callsite in callsites.OrderBy(c => c.Id, StringComparer.Ordinal))
            {
                var argument = callsite.Args.FirstOrDefault(a => a.ParamIndex == parameter.Index);
                if (argument is null) continue;

                found = true;
                result = result.Merge(ScreenIdsOf(argument.Value, context.IntoCaller()));
            }

            return found ? result : ScreenIdSet.Unresolved(parameter.Text);
        }
        finally
        {
            context.Leave(key);
        }
    }
}

/// <summary>One screen as the <c>ScreenID</c> table declares it (02 §9.1; 03 §4).</summary>
public sealed record ScreenDefinition
{
    public required string FieldId { get; init; }
    public required string FieldName { get; init; }
    public required string ScreenId { get; init; }
    public string? Name { get; init; }
    public bool IsPopUp { get; init; }
    public string? File { get; init; }
    public Span? Span { get; init; }

    /// <summary>True when the id came from the folded argument rather than from the field name.</summary>
    public bool IdIsCertain { get; init; } = true;
}
