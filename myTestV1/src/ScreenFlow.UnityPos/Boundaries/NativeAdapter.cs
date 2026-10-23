using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ScreenFlow.Core.Ir;

namespace ScreenFlow.UnityPos.Boundaries;

/// <summary>The <c>native</c> section of <c>unitypos.json</c> (02 §4.2).</summary>
public sealed record NativeSection
{
    [JsonPropertyName("javaTypes")]
    public IReadOnlyList<string> JavaTypes { get; init; } = Array.Empty<string>();

    [JsonPropertyName("dllImportAttribute")]
    public string DllImportAttribute { get; init; } = "System.Runtime.InteropServices.DllImportAttribute";
}

/// <summary>
/// Calls that leave C# for native code (02 §9.8; 03 §20).
/// <para>
/// An <c>AndroidJavaObject</c> call names its target with two strings — the Java class and the
/// method — and neither is a symbol the index can follow. The boundary records the pair so a
/// reader knows what to look for on the Android side.
/// </para>
/// </summary>
public sealed class NativeAdapter : IDomainAdapter
{
    public const string Feature = "native";

    /// <summary>The call methods of a Java object wrapper (03 §20).</summary>
    private static readonly string[] JavaCallMethods = { "Call", "CallStatic", "Get", "Set", "GetStatic", "SetStatic" };

    public string Id => AdapterOrder.Native;

    public void Run(DomainContext context)
    {
        var config = context.Section<NativeSection>(context.Domain.Native, "native");
        if (config.JavaTypes.Count == 0) return;

        var byCreation = new Dictionary<string, string>(StringComparer.Ordinal);

        // Every creation of a Java wrapper is itself a boundary, and it names the class the later
        // calls go to (03 §20).
        foreach (var method in SourceMethods(context))
        foreach (var creation in context.Reader.CreationsIn(method.Id)
                     .Where(creation => creation.Reachable)
                     .OrderBy(creation => creation.Id, StringComparer.Ordinal))
        {
            var value = creation.ObjectCreation;
            var typeName = value?.Type?.FqName;
            if (value is null || typeName is null) continue;
            if (!config.JavaTypes.Contains(typeName, StringComparer.Ordinal)) continue;

            var className = Constant(context, value.Args.FirstOrDefault(a => a.ParamIndex == 0)?.Value);
            if (className is null) continue;

            byCreation[creation.Id] = className;

            BoundaryWriter.Write(
                context,
                BoundaryCategories.NativeInterop,
                ContractKinds.Native,
                className,
                new JsonObject { ["javaType"] = typeName, ["via"] = "creation" },
                null);
        }

        foreach (var method in SourceMethods(context))
        foreach (var call in context.Reader.CallsitesIn(method.Id)
                     .Where(call => call.Reachable)
                     .OrderBy(call => call.Id, StringComparer.Ordinal))
        {
            var target = call.TargetId is null ? null : context.Reader.Symbol(call.TargetId);
            if (target is null) continue;

            var receiverType = call.Receiver?.Type?.FqName;
            if (receiverType is null || !config.JavaTypes.Contains(receiverType, StringComparer.Ordinal)) continue;
            if (!JavaCallMethods.Contains(target.Name, StringComparer.Ordinal)) continue;

            var className = ClassOf(context, call.Receiver, config) ?? receiverType;
            var member = Constant(context, call.Args.FirstOrDefault(a => a.ParamIndex == 0)?.Value);

            BoundaryWriter.Write(
                context,
                BoundaryCategories.NativeInterop,
                ContractKinds.Native,
                member is null ? className : className + "." + member,
                new JsonObject
                {
                    ["javaType"] = receiverType,
                    ["method"] = target.Name,
                    ["via"] = "call"
                },
                call);
        }

        WriteDllImports(context, config);
    }

    /// <summary>A <c>DllImport</c> method is a boundary wherever it is called (02 §9.8).</summary>
    private static void WriteDllImports(DomainContext context, NativeSection config)
    {
        foreach (var method in SourceMethods(context)
                     .Where(method => method.Modifiers.Contains(Modifiers.Extern, StringComparer.Ordinal)))
        {
            var attribute = method.Attributes.FirstOrDefault(a =>
                a.TypeFqName == config.DllImportAttribute
                || a.TypeFqName + "Attribute" == config.DllImportAttribute);

            if (attribute is null) continue;

            var library = attribute.Args.FirstOrDefault()?.FoldedStrings().FirstOrDefault() ?? "(unknown)";

            foreach (var call in context.Reader.CallsitesTargeting(method.Id).Where(call => call.Reachable))
            {
                BoundaryWriter.Write(
                    context,
                    BoundaryCategories.NativeInterop,
                    ContractKinds.Native,
                    library + "." + method.Name,
                    new JsonObject { ["via"] = "dllImport", ["library"] = library },
                    call);
            }
        }
    }

    /// <summary>The Java class a receiver was created with, through the local that holds it.</summary>
    private static string? ClassOf(DomainContext context, Value? receiver, NativeSection config)
    {
        var creation = receiver switch
        {
            ObjectCreationValue value => value,
            LocalValue { Origin: ObjectCreationValue value } => value,
            _ => null
        };

        return creation is null
            ? null
            : Constant(context, creation.Args.FirstOrDefault(a => a.ParamIndex == 0)?.Value);
    }

    private static string? Constant(DomainContext context, Value? value)
    {
        if (value is null) return null;

        var folded = context.Values.ResolveStrings(value, context.Resolve()).Strings;
        return folded.Count == 1 ? folded[0] : null;
    }

    /// <summary>Every method declared in source, which is where a native call can be.</summary>
    internal static IEnumerable<SymbolRec> SourceMethods(DomainContext context)
        => context.Reader.SymbolsByKind(SymbolKinds.Method)
            .Where(symbol => symbol.IsSource)
            .OrderBy(symbol => symbol.Id, StringComparer.Ordinal);
}
