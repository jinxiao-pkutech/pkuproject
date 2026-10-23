using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ScreenFlow.Core.Ir;
using ScreenFlow.Core.Tests.ConfigTests;
using Xunit;

namespace ScreenFlow.Core.Tests.IrTests;

/// <summary>
/// Keeps <c>schema/ir-1.schema.json</c> and the C# model in step. The schema is the published
/// contract for extractor authors while <see cref="IrValidator"/> is what actually gates the index,
/// so a drift between them would mean a record that passes review and fails the build, or worse the
/// other way round.
/// </summary>
public sealed class IrSchemaTests
{
    private static readonly JsonNode Schema = LoadSchema();

    private static JsonNode LoadSchema()
    {
        var path = Path.Combine(RepoLayout.RepoRoot, "schema", "ir-1.schema.json");
        Assert.True(File.Exists(path), $"schema not found at {path}");
        return JsonNode.Parse(File.ReadAllText(path))
               ?? throw new InvalidOperationException("schema is empty");
    }

    private static IReadOnlyList<string> SchemaEnum(string definition)
    {
        var node = Schema["$defs"]?[definition]?["enum"] as JsonArray;
        Assert.NotNull(node);
        return node.Where(v => v is not null).Select(v => v!.GetValue<string>()).ToArray();
    }

    private static IReadOnlyList<string> SchemaRequired(string definition)
    {
        var node = Schema["$defs"]?[definition]?["required"] as JsonArray;
        Assert.NotNull(node);
        return node.Where(v => v is not null).Select(v => v!.GetValue<string>()).ToArray();
    }

    private static IReadOnlyList<string> SchemaProperties(string definition)
    {
        var node = Schema["$defs"]?[definition]?["properties"] as JsonObject;
        Assert.NotNull(node);
        return node.Select(p => p.Key).ToArray();
    }

    [Fact]
    [Trait("Task", "T02")]
    public void Ir_Schema_EnumsMatchModel()
    {
        var expected = new (string Definition, IReadOnlyList<string> Model)[]
        {
            ("recordKind", IrRecordKinds.All),
            ("symbolKind", SymbolKinds.All),
            ("typeKind", TypeKinds.All),
            ("modifier", Modifiers.All),
            ("encoding", Encodings.All),
            ("derivation", Derivations.All),
            ("severity", Severities.All),
            ("markerDir", MarkerDirections.All),
            ("refKind", RefKinds.All),
            ("valueKind", ValueKinds.All),
            ("conditionKind", ConditionKinds.All),
            ("conditionMode", ConditionModes.Values),
            ("stringTestOp", StringTestOps.All)
        };

        foreach (var (definition, model) in expected)
        {
            // Compared as sets: the schema's listing order is documentation, not contract.
            Assert.Equal(
                model.OrderBy(v => v, StringComparer.Ordinal).ToArray(),
                SchemaEnum(definition).OrderBy(v => v, StringComparer.Ordinal).ToArray());
        }
    }

    [Fact]
    [Trait("Task", "T02")]
    public void Ir_Schema_RequiredFieldsMatchModel()
    {
        foreach (var (definition, type) in RecordTypes())
        {
            var modelRequired = JsonNamesOf(type, requiredOnly: true);
            Assert.Equal(
                modelRequired.OrderBy(n => n, StringComparer.Ordinal).ToArray(),
                SchemaRequired(definition).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        }
    }

    [Fact]
    [Trait("Task", "T02")]
    public void Ir_Schema_PropertiesMatchModel()
    {
        // The record definitions declare additionalProperties:false, so a property the model emits
        // but the schema omits would make a legitimate record fail validation for an external author.
        foreach (var (definition, type) in RecordTypes())
        {
            Assert.Equal(
                JsonNamesOf(type, requiredOnly: false).OrderBy(n => n, StringComparer.Ordinal).ToArray(),
                SchemaProperties(definition).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        }
    }

    [Fact]
    [Trait("Task", "T02")]
    public void Ir_Schema_IdPatternsMatchIdFormat()
    {
        var patterns = new[]
        {
            ("symbolId", Ids.SymbolPrefix),
            ("callsiteId", Ids.CallsitePrefix),
            ("assignId", Ids.AssignPrefix),
            ("creationId", Ids.CreationPrefix)
        };

        foreach (var (definition, prefix) in patterns)
        {
            var pattern = Schema["$defs"]?[definition]?["pattern"]?.GetValue<string>();
            Assert.NotNull(pattern);
            Assert.Equal($"^{prefix}[0-9a-f]{{{Ids.HashLength}}}$", pattern);
        }

        // The declared pattern must accept a real id.
        var real = Ids.SymbolId("fixture", "Assembly-CSharp", "T:Fixture.Sample");
        var symbolPattern = Schema["$defs"]!["symbolId"]!["pattern"]!.GetValue<string>();
        Assert.Matches(symbolPattern, real);
    }

    [Fact]
    [Trait("Task", "T02")]
    public void Ir_Schema_ValueNestingLimitIsDocumented()
    {
        var description = Schema["$defs"]?["value"]?["description"]?.GetValue<string>();
        Assert.NotNull(description);
        Assert.Contains(IrValidator.MaxValueDepth.ToString(), description, StringComparison.Ordinal);

        var maxItems = Schema["$defs"]?["value"]?["properties"]?["values"]?["maxItems"]?.GetValue<int>();
        Assert.Equal(StringsValue.MaxValues, maxItems);

        var maxLength = Schema["$defs"]?["value"]?["properties"]?["text"]?["maxLength"]?.GetValue<int>();
        Assert.Equal(Value.MaxTextLength, maxLength);
    }

    private static IEnumerable<(string Definition, Type Type)> RecordTypes()
    {
        yield return (IrRecordKinds.Meta, typeof(MetaRec));
        yield return (IrRecordKinds.File, typeof(FileRec));
        yield return (IrRecordKinds.Symbol, typeof(SymbolRec));
        yield return (IrRecordKinds.Impl, typeof(ImplRec));
        yield return (IrRecordKinds.Callsite, typeof(CallsiteRec));
        yield return (IrRecordKinds.Assign, typeof(AssignRec));
        yield return (IrRecordKinds.Creation, typeof(CreationRec));
        yield return (IrRecordKinds.Cfg, typeof(CfgRec));
        yield return (IrRecordKinds.Marker, typeof(MarkerRec));
        yield return (IrRecordKinds.Diag, typeof(DiagRec));
    }

    /// <summary>
    /// The JSON names the model serialises for a record. <paramref name="requiredOnly"/> selects the
    /// properties declared with the C# <c>required</c> modifier, which the compiler marks with
    /// <see cref="RequiredMemberAttribute"/> — so the schema's required list is checked against the
    /// model's own notion of required rather than a hand-kept second list.
    /// </summary>
    private static IReadOnlyList<string> JsonNamesOf(Type type, bool requiredOnly)
        => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetCustomAttribute<JsonIgnoreAttribute>() is null)
            .Where(p => !requiredOnly || p.GetCustomAttribute<RequiredMemberAttribute>() is not null)
            .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
                         ?? JsonNamingPolicy.CamelCase.ConvertName(p.Name))
            .ToArray();
}
