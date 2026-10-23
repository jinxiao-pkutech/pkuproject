using ScreenFlow.Core.Ir;
using ScreenFlow.Core.Resolution;

namespace ScreenFlow.UnityPos.Screens;

/// <summary>One <c>Enumeration</c> member and the values its initialiser gave it (03 §5).</summary>
public sealed record EnumerationMember
{
    public required string TypeId { get; init; }
    public required string MemberId { get; init; }
    public required string Name { get; init; }
    public int? ValueId { get; init; }
    public string? ValueName { get; init; }
}

/// <summary>
/// Resolves <c>SomeEnumeration.MEMBER.Id</c> and <c>.Name</c> (02 §9.2).
/// <para>
/// The core would otherwise see only a static field followed by a property, and the value behind it
/// is what pairs an error code with its message (03 §13.3) and a key code with its table entry
/// (03 §11.1).
/// </para>
/// </summary>
public sealed class EnumerationValueProvider : IValueProvider
{
    public const string ProviderName = "enumeration";
    public const string IdProperty = "Id";
    public const string NameProperty = "Name";

    public EnumerationValueProvider(IReadOnlyDictionary<string, EnumerationMember> members)
        => Members = members;

    public string Name => ProviderName;

    /// <summary>Members by the id of the field that declares them.</summary>
    public IReadOnlyDictionary<string, EnumerationMember> Members { get; }

    public ValueResolution? TryResolve(Value value, ResolveContext context)
    {
        if (value is not StaticMemberValue member || member.SymbolId is null) return null;
        if (!Members.TryGetValue(member.SymbolId, out var found)) return null;
        if (member.Path.Count != 1) return null;

        if (string.Equals(member.Path[0], IdProperty, StringComparison.Ordinal))
        {
            // The id is a number; it is reported as the string it compares as, because every value
            // the resolver returns is matched against literals from source.
            return found.ValueId is null
                ? ValueResolution.Unresolved
                : ValueResolution.OfStrings(new[] { found.ValueId.Value.ToString() });
        }

        if (string.Equals(member.Path[0], NameProperty, StringComparison.Ordinal))
        {
            return found.ValueName is null
                ? ValueResolution.Unresolved
                : ValueResolution.OfStrings(new[] { found.ValueName });
        }

        return null;
    }

    /// <summary>The member a value denotes, or null when it is not an enumeration member.</summary>
    public EnumerationMember? MemberOf(Value value)
        => value is StaticMemberValue { SymbolId: not null } member
           && Members.TryGetValue(member.SymbolId, out var found)
            ? found
            : null;
}
