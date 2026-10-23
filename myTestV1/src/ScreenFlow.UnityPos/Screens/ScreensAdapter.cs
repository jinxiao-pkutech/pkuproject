using System.Text.Json.Serialization;
using ScreenFlow.Core.Config;
using ScreenFlow.Core.Ir;
using ScreenFlow.Core.Storage;

namespace ScreenFlow.UnityPos.Screens;

/// <summary>The <c>screens</c> section of <c>unitypos.json</c> (02 §4.2).</summary>
public sealed record ScreensSection
{
    [JsonPropertyName("screenIdType")] public required AnchorSpec ScreenIdType { get; init; }
    [JsonPropertyName("idArgIndex")] public int IdArgIndex { get; init; }
    [JsonPropertyName("nameArgIndex")] public int NameArgIndex { get; init; } = 1;
    [JsonPropertyName("isPopUpParamName")] public string IsPopUpParamName { get; init; } = "isPopUp";
    [JsonPropertyName("idProperty")] public string IdProperty { get; init; } = "Id";

    [JsonPropertyName("registries")]
    public IReadOnlyList<RegistrySpec> Registries { get; init; } = Array.Empty<RegistrySpec>();

    [JsonPropertyName("standbyScreens")]
    public IReadOnlyList<StandbySpec> StandbyScreens { get; init; } = Array.Empty<StandbySpec>();
}

/// <summary>One screen registry: a terminal's dictionary of screen id to view and presenter.</summary>
public sealed record RegistrySpec
{
    [JsonPropertyName("terminal")] public required string Terminal { get; init; }
    [JsonPropertyName("type")] public string? Type { get; init; }
    [JsonPropertyName("file")] public string? File { get; init; }
    [JsonPropertyName("fqName")] public string? FqName { get; init; }
    [JsonPropertyName("field")] public required string Field { get; init; }
    [JsonPropertyName("viewTupleIndex")] public int ViewTupleIndex { get; init; }
    [JsonPropertyName("presenterTupleIndex")] public int PresenterTupleIndex { get; init; } = 1;

    /// <summary>The registry type as an anchor, so it is addressed the same way as every other type.</summary>
    public AnchorSpec Anchor => new() { Type = Type, File = File, FqName = FqName };
}

public sealed record StandbySpec
{
    [JsonPropertyName("terminal")] public required string Terminal { get; init; }
    [JsonPropertyName("screenId")] public required string ScreenId { get; init; }
}

/// <summary>
/// Reads the screen table and the per-terminal registries (02 §9.1).
/// <para>
/// A screen declared in <c>ScreenID</c> but absent from every registry is written with
/// <c>registered = 0</c> rather than dropped: it is exactly the case a reader needs to be told about
/// (E-09), and silently omitting it would read as "there is no such screen".
/// </para>
/// </summary>
public sealed class ScreensAdapter : IDomainAdapter
{
    public const string Feature = "screens";

    /// <summary>Emitted when a screen's id argument does not fold to a constant.</summary>
    public const string IdNotConstantCode = "W_SCREEN_ID_NOT_CONSTANT";

    /// <summary>Emitted when a registry entry's key or value is not the documented shape.</summary>
    public const string RegistryShapeCode = "W_SCREEN_REGISTRY_SHAPE";

    /// <summary>Terminal of a screen that is declared but registered nowhere (02 §9.1).</summary>
    public const string NoTerminal = "";

    public string Id => AdapterOrder.Screens;

    public void Run(DomainContext context)
    {
        var config = context.Section<ScreensSection>(context.Domain.Screens, "screens");

        var anchor = context.Anchors.Resolve(config.ScreenIdType);
        if (context.AnchorFailed(anchor, Id, Feature)) return;

        var screenIdType = anchor.Require();
        var definitions = ReadDefinitions(context, config, screenIdType);

        var rows = new Dictionary<(string Terminal, string ScreenId), ScreenRow>();
        foreach (var registry in config.Registries)
            ReadRegistry(context, config, registry, definitions, rows);

        // Every declared screen appears, registered or not (02 §9.1 step 3).
        var registeredIds = rows.Keys.Select(key => key.ScreenId).ToHashSet(StringComparer.Ordinal);

        foreach (var definition in definitions.Values
                     .Where(d => !registeredIds.Contains(d.ScreenId))
                     .OrderBy(d => d.ScreenId, StringComparer.Ordinal))
        {
            rows[(NoTerminal, definition.ScreenId)] = new ScreenRow
            {
                Terminal = NoTerminal,
                ScreenId = definition.ScreenId,
                Name = definition.Name,
                IsPopup = definition.IsPopUp,
                Registered = false,
                ScreenFieldId = definition.FieldId,
                EvidenceId = context.Evidence(definition.File, definition.Span)
            };
        }

        foreach (var row in rows.OrderBy(pair => pair.Key.Terminal, StringComparer.Ordinal)
                     .ThenBy(pair => pair.Key.ScreenId, StringComparer.Ordinal)
                     .Select(pair => pair.Value))
        {
            context.Writer.Add(row);
        }

        context.Register(new ScreenIdValueProvider(
            screenIdType.Id,
            definitions,
            config.IdProperty,
            context.Reader,
            context.Fields));
    }

    /// <summary>
    /// The screen table: every <c>static readonly</c> field of <c>ScreenID</c> whose initialiser
    /// creates a <c>ScreenID</c> (02 §9.1 step 1; 03 §4).
    /// </summary>
    private static IReadOnlyDictionary<string, ScreenDefinition> ReadDefinitions(
        DomainContext context,
        ScreensSection config,
        SymbolRec screenIdType)
    {
        var definitions = new Dictionary<string, ScreenDefinition>(StringComparer.Ordinal);

        foreach (var field in context.Reader.SymbolsInContainer(screenIdType.Id)
                     .Where(s => s.Kind == SymbolKinds.Field)
                     .Where(s => s.Modifiers.Contains(Modifiers.Static, StringComparer.Ordinal))
                     .OrderBy(s => s.Id, StringComparer.Ordinal))
        {
            if (field.Initializer is not ObjectCreationValue creation) continue;
            if (creation.TypeId is not null && creation.TypeId != screenIdType.Id) continue;

            var id = ConstantString(creation, config.IdArgIndex);
            var certain = id is not null;

            if (id is null)
            {
                // The field name is the fallback the spec names, and the diagnostic says it was used
                // (02 §9.1): an id taken from the name is not the same fact as one read from source.
                context.Diagnose(DiagRec.Warning(
                    IdNotConstantCode,
                    $"[{AdapterOrder.Screens}] The id argument of '{field.FqName}' does not fold to a " +
                    "constant; the field name is used instead.",
                    field.File,
                    field.Span?.StartLine));

                id = field.Name;
            }

            definitions[field.Id] = new ScreenDefinition
            {
                FieldId = field.Id,
                FieldName = field.Name,
                ScreenId = id,
                Name = ConstantString(creation, config.NameArgIndex),
                IsPopUp = ConstantBool(creation, config.IsPopUpParamName) ?? false,
                File = field.File,
                Span = field.Span,
                IdIsCertain = certain
            };
        }

        return definitions;
    }

    /// <summary>
    /// One terminal's registry (02 §9.1 step 2): the dictionary initialiser's elements, plus any
    /// <c>Add</c> call made on the same field elsewhere.
    /// </summary>
    private static void ReadRegistry(
        DomainContext context,
        ScreensSection config,
        RegistrySpec registry,
        IReadOnlyDictionary<string, ScreenDefinition> definitions,
        Dictionary<(string Terminal, string ScreenId), ScreenRow> rows)
    {
        var anchor = context.Anchors.Resolve(registry.Anchor);
        if (context.AnchorFailed(anchor, AdapterOrder.Screens, Feature)) return;

        var registryType = anchor.Require();

        var field = context.Reader.SymbolsInContainer(registryType.Id)
            .FirstOrDefault(s => s.Kind == SymbolKinds.Field && s.Name == registry.Field);

        if (field is null)
        {
            context.Diagnose(DiagRec.Error(
                AnchorResolverCodes.Unresolved,
                $"[{AdapterOrder.Screens}] '{registryType.FqName}' has no field '{registry.Field}', " +
                "so the registry of terminal '" + registry.Terminal + "' cannot be read."));

            context.Disable(Feature);
            return;
        }

        foreach (var element in Elements(field, context))
            AddEntry(context, registry, definitions, rows, element, field);
    }

    /// <summary>
    /// The registry entries: the key/value pairs of the field's initialiser, plus every
    /// <c>Add(key, value)</c> call made on that same field (02 §9.1 step 2).
    /// </summary>
    private static IEnumerable<IReadOnlyList<Value>> Elements(SymbolRec field, DomainContext context)
    {
        if (field.Initializer is ObjectCreationValue creation)
        {
            foreach (var element in creation.Elements) yield return element;
        }

        foreach (var argument in AddCallArguments(field, context)) yield return argument;
    }

    /// <summary>
    /// Arguments of the <c>Add</c> calls made on this field. Found through the field's own type —
    /// the <c>Add</c> members of that type, then the callsites targeting them — rather than by
    /// scanning every callsite in the project.
    /// </summary>
    private static IEnumerable<IReadOnlyList<Value>> AddCallArguments(SymbolRec field, DomainContext context)
    {
        var collectionTypeId = field.TypeRef?.Id;
        if (collectionTypeId is null) yield break;

        var adders = context.Reader.SymbolsByName("Add")
            .Where(s => s.Kind == SymbolKinds.Method && s.ContainerId == collectionTypeId)
            .Select(s => s.Id)
            .ToList();

        foreach (var adder in adders)
        foreach (var callsite in context.Reader.CallsitesTargeting(adder))
        {
            if (callsite.Receiver is not StaticMemberValue receiver) continue;
            if (receiver.SymbolId != field.Id) continue;

            yield return callsite.Args.OrderBy(a => a.ParamIndex).Select(a => a.Value).ToList();
        }
    }

    private static void AddEntry(
        DomainContext context,
        RegistrySpec registry,
        IReadOnlyDictionary<string, ScreenDefinition> definitions,
        Dictionary<(string Terminal, string ScreenId), ScreenRow> rows,
        IReadOnlyList<Value> element,
        SymbolRec field)
    {
        if (element.Count < 2)
        {
            context.Diagnose(DiagRec.Warning(
                RegistryShapeCode,
                $"[{AdapterOrder.Screens}] A registry entry of '{field.FqName}' has {element.Count} " +
                "values; a dictionary entry is a key and a value.",
                field.File,
                field.Span?.StartLine));
            return;
        }

        if (element[0] is not StaticMemberValue key
            || key.SymbolId is null
            || !definitions.TryGetValue(key.SymbolId, out var definition))
        {
            context.Diagnose(DiagRec.Warning(
                RegistryShapeCode,
                $"[{AdapterOrder.Screens}] The key '{element[0].Text}' of a registry entry in " +
                $"'{field.FqName}' is not a known ScreenID field.",
                field.File,
                field.Span?.StartLine));
            return;
        }

        var types = TupleItems(element[1]);

        rows[(registry.Terminal, definition.ScreenId)] = new ScreenRow
        {
            Terminal = registry.Terminal,
            ScreenId = definition.ScreenId,
            Name = definition.Name,
            IsPopup = definition.IsPopUp,
            ViewTypeId = TypeAt(types, registry.ViewTupleIndex),
            PresenterTypeId = TypeAt(types, registry.PresenterTupleIndex),
            Registered = true,
            ScreenFieldId = definition.FieldId,
            EvidenceId = context.Evidence(definition.File, definition.Span)
        };
    }

    /// <summary>
    /// The items of a registry value: a tuple, or a <c>ValueTuple</c> creation written as
    /// <c>new(...)</c> (02 §9.1 step 2).
    /// </summary>
    private static IReadOnlyList<Value> TupleItems(Value value) => value switch
    {
        TupleValue tuple => tuple.Items,
        ObjectCreationValue creation => creation.Args.OrderBy(a => a.ParamIndex).Select(a => a.Value).ToList(),
        _ => Array.Empty<Value>()
    };

    private static string? TypeAt(IReadOnlyList<Value> items, int index)
        => index >= 0 && index < items.Count && items[index] is TypeOfValue typeOf ? typeOf.TypeId : null;

    private static string? ConstantString(ObjectCreationValue creation, int paramIndex)
    {
        var argument = creation.Args.FirstOrDefault(a => a.ParamIndex == paramIndex);
        var folded = argument?.Value.FoldedStrings() ?? Array.Empty<string>();

        // Exactly one folded value is a fact; several would be a screen with two ids.
        return folded.Count == 1 ? folded[0] : null;
    }

    private static bool? ConstantBool(ObjectCreationValue creation, string paramName)
    {
        var argument = creation.Args.FirstOrDefault(a => a.ParamName == paramName);
        if (argument?.Value is not ConstValue { ConstantValue: not null } constant) return null;

        return constant.ConstantValue.GetValueKind() switch
        {
            System.Text.Json.JsonValueKind.True => true,
            System.Text.Json.JsonValueKind.False => false,
            _ => null
        };
    }
}

/// <summary>The anchor diagnostic codes, so adapters do not repeat the literals.</summary>
internal static class AnchorResolverCodes
{
    public const string Unresolved = Anchors.AnchorResolver.UnresolvedCode;
    public const string Ambiguous = Anchors.AnchorResolver.AmbiguousCode;
}
