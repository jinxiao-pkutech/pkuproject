using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ScreenFlow.Core.Config;
using ScreenFlow.Core.Ir;
using ScreenFlow.Core.Storage;

namespace ScreenFlow.UnityPos.Boundaries;

/// <summary>Break categories (02 §8.3). A boundary is recorded under the one that explains it.</summary>
public static class BoundaryCategories
{
    public const string ExternalHardware = "ExternalHardware";
    public const string ExternalMicroservice = "ExternalMicroservice";
    public const string TerminalTcpCurrentScreen = "TerminalTcpCurrentScreen";
    public const string NativeInterop = "NativeInterop";
    public const string DatabaseMediated = "DatabaseMediated";
    public const string DiAmbiguous = "DiAmbiguous";
    public const string DeferredExecution = "DeferredExecution";
    public const string KeySeqStageDependent = "KeySeqStageDependent";
    public const string Unknown = "Unknown";

    public static readonly IReadOnlyList<string> All = new[]
    {
        ExternalHardware, ExternalMicroservice, TerminalTcpCurrentScreen, NativeInterop,
        DatabaseMediated, DiAmbiguous, DeferredExecution, KeySeqStageDependent, Unknown
    };
}

/// <summary>Contract kinds of a break record (02 §8.3).</summary>
public static class ContractKinds
{
    public const string DeviceInterface = "deviceInterface";
    public const string DeviceInput = "deviceInput";
    public const string Api = "api";
    public const string TcpEvent = "tcpEvent";
    public const string Native = "native";
    public const string Table = "table";
    public const string Service = "service";
    public const string Queue = "queue";
    public const string KeySeq = "keySeq";
    public const string Unknown = "unknown";
}

/// <summary>
/// Writes a boundary and the edge that reaches it (02 §9.8).
/// <para>
/// One place for both, so that every boundary a traversal can hit is reachable through an edge and
/// no boundary exists that nothing points at.
/// </para>
/// </summary>
internal static class BoundaryWriter
{
    public const string BoundaryPrefix = "bd_";
    public const string EdgePrefix = "de_";
    public const string EdgeKind = "boundary";

    public static string Write(
        DomainContext context,
        string category,
        string contractKind,
        string key,
        JsonObject contract,
        CallsiteRec? call)
    {
        var id = Ids.DerivedId(BoundaryPrefix, new { category, contractKind, key });

        contract["kind"] = contractKind;
        contract["key"] = key;

        context.Writer.Add(new BoundaryRow
        {
            Id = id,
            Category = category,
            ContractKind = contractKind,
            ContractKey = key,
            Json = contract.ToJsonString()
        });

        if (call is not null)
        {
            var edge = new JsonObject
            {
                ["boundaryId"] = id,
                ["category"] = category,
                ["contractKind"] = contractKind,
                ["key"] = key
            };

            context.Writer.Add(new DomainEdgeRow
            {
                Id = Ids.DerivedId(EdgePrefix, new { kind = EdgeKind, callsite = call.Id, boundary = id }),
                Kind = EdgeKind,
                CallsiteId = call.Id,
                FromMethodId = call.CallerId,
                Json = edge.ToJsonString()
            });
        }

        return id;
    }

    /// <summary>The type a call produces, with <c>Task</c> and <c>UniTask</c> unwrapped (02 §9.8).</summary>
    public static string? ResponseType(TypeRef? returnType)
    {
        if (returnType is null) return null;

        var name = returnType.FqName;
        var isAwaitable = name.StartsWith("System.Threading.Tasks.Task", StringComparison.Ordinal)
                          || name.StartsWith("Cysharp.Threading.Tasks.UniTask", StringComparison.Ordinal);

        if (!isAwaitable) return name;

        return returnType.Args.Count == 1 ? returnType.Args[0].FqName : name;
    }

    public static JsonArray Of(IEnumerable<string> values)
        => new(values.Select(value => (JsonNode)value!).ToArray());
}

/// <summary>The <c>device</c> section of <c>unitypos.json</c> (02 §4.2).</summary>
public sealed record DeviceSection
{
    [JsonPropertyName("commandInterfaces")] public required AnchorSpec CommandInterfaces { get; init; }

    [JsonPropertyName("callbackMethods")]
    public IReadOnlyList<string> CallbackMethods { get; init; } = Array.Empty<string>();

    [JsonPropertyName("receiveEventSetters")]
    public IReadOnlyList<string> ReceiveEventSetters { get; init; } = Array.Empty<string>();

    [JsonPropertyName("topicMethods")]
    public IReadOnlyList<string> TopicMethods { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Hardware command boundaries (02 §9.8; 03 §17).
/// <para>
/// A device command leaves the project: the request goes to an external box over HTTP and the
/// answer comes back later over MQTT, with no call edge between them. The boundary records what
/// was asked, on which topic the answer arrives, and which methods can receive it — so a reader
/// gets the resume points instead of a chain that simply stops.
/// </para>
/// </summary>
public sealed class DeviceAdapter : IDomainAdapter
{
    public const string Feature = "device";

    public string Id => AdapterOrder.Device;

    public void Run(DomainContext context)
    {
        var config = context.Section<DeviceSection>(context.Domain.Device, "device");

        var interfaces = context.Anchors.Resolve(config.CommandInterfaces);
        if (context.AnchorFailed(interfaces, Id, Feature)) return;

        var callbacks = Callbacks(context, config);

        foreach (var contract in interfaces.Symbols.OrderBy(symbol => symbol.Id, StringComparer.Ordinal))
        foreach (var member in context.Reader.SymbolsInContainer(contract.Id)
                     .Where(symbol => symbol.Kind == SymbolKinds.Method)
                     .OrderBy(symbol => symbol.Id, StringComparer.Ordinal))
        {
            // The implementations are what carry the topic and the callbacks (03 §17).
            var implementations = context.Resolution.ImplementationsOf(member.Id)
                .Select(id => context.Reader.Symbol(id))
                .OfType<SymbolRec>()
                .ToList();

            var implementingTypes = implementations
                .Select(implementation => implementation.ContainerId)
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var key = contract.Name + "." + member.Name;

            var json = new JsonObject
            {
                ["requestTypes"] = BoundaryWriter.Of(member.Parameters.Select(p => p.Type.FqName)),
                ["responseType"] = BoundaryWriter.ResponseType(member.ReturnType),
                ["topicTemplate"] = implementingTypes
                    .Select(type => Topic(context, config, type))
                    .FirstOrDefault(topic => topic is not null),
                ["adapters"] = BoundaryWriter.Of(implementingTypes),
                ["callbacks"] = BoundaryWriter.Of(
                    implementingTypes.SelectMany(type => CallbacksOf(context, config, type))
                        .Concat(callbacks)
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(id => id, StringComparer.Ordinal))
            };

            var calls = context.Reader.CallsitesTargeting(member.Id)
                .Concat(implementations.SelectMany(i => context.Reader.CallsitesTargeting(i.Id)))
                .Where(call => call.Reachable)
                .OrderBy(call => call.Id, StringComparer.Ordinal)
                .ToList();

            if (calls.Count == 0) continue;

            foreach (var call in calls)
                BoundaryWriter.Write(context, BoundaryCategories.ExternalHardware, ContractKinds.DeviceInterface,
                    key, (JsonObject)json.DeepClone(), call);
        }
    }

    /// <summary>The topic an implementation publishes on, from its topic method's value (02 §9.8).</summary>
    private static string? Topic(DomainContext context, DeviceSection config, string typeId)
    {
        foreach (var name in config.TopicMethods)
        foreach (var method in context.Reader.SymbolsInContainer(typeId)
                     .Where(symbol => symbol.Kind == SymbolKinds.Method && symbol.Name == name))
        {
            switch (method.Initializer)
            {
                case TemplateValue template:
                    return template.Template;
                case ConstValue constant when constant.FoldedStrings().Count == 1:
                    return constant.FoldedStrings()[0];
            }
        }

        return null;
    }

    private static IEnumerable<string> CallbacksOf(DomainContext context, DeviceSection config, string typeId)
        => context.Reader.SymbolsInContainer(typeId)
            .Where(symbol => symbol.Kind == SymbolKinds.Method)
            .Where(symbol => config.CallbackMethods.Contains(symbol.Name, StringComparer.Ordinal))
            .Select(symbol => symbol.Id);

    /// <summary>
    /// Methods handed to a receive-event setter anywhere in the project (02 §9.8). That is how the
    /// screen side registers for the asynchronous answer (03 §17).
    /// </summary>
    private static IReadOnlyList<string> Callbacks(DomainContext context, DeviceSection config)
    {
        var callbacks = new List<string>();

        foreach (var name in config.ReceiveEventSetters)
        foreach (var setter in context.Reader.SymbolsByName(name)
                     .Where(symbol => symbol.Kind == SymbolKinds.Method))
        foreach (var call in context.Reader.CallsitesTargeting(setter.Id))
        foreach (var argument in call.Args)
        {
            switch (argument.Value)
            {
                case MethodRefValue method:
                    callbacks.Add(method.MethodId);
                    break;
                case LambdaValue lambda:
                    callbacks.Add(lambda.MethodId);
                    break;
            }
        }

        return callbacks.Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToList();
    }
}
