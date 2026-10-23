using System.Text.Json;
using System.Text.Json.Serialization;
using ScreenFlow.Core.Config;
using ScreenFlow.Core.Ir;
using ScreenFlow.Core.Storage;

namespace ScreenFlow.UnityPos.Screens;

/// <summary>The <c>enumerations</c> section of <c>unitypos.json</c> (02 §4.2).</summary>
public sealed record EnumerationsSection
{
    [JsonPropertyName("baseType")] public required AnchorSpec BaseType { get; init; }
    [JsonPropertyName("idArgIndex")] public int IdArgIndex { get; init; }
    [JsonPropertyName("nameArgIndex")] public int NameArgIndex { get; init; } = 1;
}

/// <summary>
/// Reads the <c>Enumeration</c> subclasses (02 §9.2; 03 §5).
/// <para>
/// These are the POS project's own enums — stage numbers, key codes, result codes — and they are
/// ordinary static fields, not a C# <c>enum</c>. Their values are what a key sequence table and an
/// error code are matched by, so the id and the name both have to be read out of the initialiser.
/// </para>
/// </summary>
public sealed class EnumerationsAdapter : IDomainAdapter
{
    public const string Feature = "enumerations";

    /// <summary>Emitted when a member's initialiser is not the documented shape.</summary>
    public const string MemberShapeCode = "W_ENUM_MEMBER_SHAPE";

    public string Id => AdapterOrder.Enumerations;

    public void Run(DomainContext context)
    {
        var config = context.Section<EnumerationsSection>(context.Domain.Enumerations, "enumerations");

        var anchor = context.Anchors.Resolve(config.BaseType);
        if (context.AnchorFailed(anchor, Id, Feature)) return;

        var baseType = anchor.Require();
        var members = new Dictionary<string, EnumMemberRow>(StringComparer.Ordinal);
        var providerMembers = new Dictionary<string, EnumerationMember>(StringComparer.Ordinal);

        foreach (var type in context.Anchors.SourceTypes
                     .Where(type => context.Resolution.IsSubtypeOf(type.Id, baseType.Id))
                     .OrderBy(type => type.Id, StringComparer.Ordinal))
        {
            // A partial type is one symbol with members from every file it is declared in, so the
            // members of StageNo.cs and StageNo.Card.cs arrive together (03 §5, E-05).
            foreach (var field in context.Reader.SymbolsInContainer(type.Id)
                         .Where(s => s.Kind == SymbolKinds.Field)
                         .Where(s => s.Modifiers.Contains(Modifiers.Static, StringComparer.Ordinal))
                         .OrderBy(s => s.Id, StringComparer.Ordinal))
            {
                if (field.Initializer is not ObjectCreationValue creation) continue;
                if (creation.TypeId is not null && creation.TypeId != type.Id) continue;

                var valueId = ConstantInt(creation, config.IdArgIndex);
                var valueName = ConstantString(creation, config.NameArgIndex);

                if (valueId is null && valueName is null)
                {
                    context.Diagnose(DiagRec.Warning(
                        MemberShapeCode,
                        $"[{AdapterOrder.Enumerations}] Neither the id nor the name argument of " +
                        $"'{field.FqName}' folds to a constant, so the member carries no value.",
                        field.File,
                        field.Span?.StartLine));
                }

                members[field.Id] = new EnumMemberRow
                {
                    TypeId = type.Id,
                    MemberId = field.Id,
                    Name = field.Name,
                    ValueId = valueId,
                    ValueName = valueName
                };

                providerMembers[field.Id] = new EnumerationMember
                {
                    TypeId = type.Id,
                    MemberId = field.Id,
                    Name = field.Name,
                    ValueId = valueId,
                    ValueName = valueName
                };
            }
        }

        foreach (var row in members.Values.OrderBy(row => row.MemberId, StringComparer.Ordinal))
            context.Writer.Add(row);

        context.Register(new EnumerationValueProvider(providerMembers));
    }

    private static int? ConstantInt(ObjectCreationValue creation, int paramIndex)
    {
        var argument = creation.Args.FirstOrDefault(a => a.ParamIndex == paramIndex);
        if (argument?.Value is not ConstValue { ConstantValue: not null } constant) return null;

        return constant.ConstantValue.GetValueKind() == JsonValueKind.Number
            ? constant.ConstantValue.GetValue<int>()
            : null;
    }

    private static string? ConstantString(ObjectCreationValue creation, int paramIndex)
    {
        var argument = creation.Args.FirstOrDefault(a => a.ParamIndex == paramIndex);
        var folded = argument?.Value.FoldedStrings() ?? Array.Empty<string>();

        return folded.Count == 1 ? folded[0] : null;
    }
}
