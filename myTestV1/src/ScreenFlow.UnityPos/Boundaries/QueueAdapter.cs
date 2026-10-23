using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ScreenFlow.Core.Config;
using ScreenFlow.Core.Ir;
using ScreenFlow.Core.Storage;

namespace ScreenFlow.UnityPos.Boundaries;

/// <summary>The <c>queue</c> section of <c>unitypos.json</c> (02 §4.2).</summary>
public sealed record QueueSection
{
    [JsonPropertyName("enqueueMethods")]
    public IReadOnlyList<AnchorSpec> EnqueueMethods { get; init; } = Array.Empty<AnchorSpec>();

    [JsonPropertyName("requestType")] public AnchorSpec? RequestType { get; init; }
    [JsonPropertyName("lambdaArgIndex")] public int LambdaArgIndex { get; init; }
}

/// <summary>
/// Work handed to a queue or to a deferred invoker (02 §9.8; 03 §19).
/// <para>
/// The lambda runs later, driven by the queue, so there is no call edge from the enqueue to the
/// work. Recording which lambda was queued is what lets a path continue into it while still
/// reporting that the step is deferred (02 §8.3, <c>DeferredExecution</c>).
/// </para>
/// </summary>
public sealed class QueueAdapter : IDomainAdapter
{
    public const string Feature = "queue";
    public const string EdgeKind = "enqueue";
    public const string EdgePrefix = "de_";

    public string Id => AdapterOrder.Queue;

    public void Run(DomainContext context)
    {
        var config = context.Section<QueueSection>(context.Domain.Queue, "queue");
        var deferred = context.Domain.DeferredInvokers.ValueKind == System.Text.Json.JsonValueKind.Array
            ? context.Section<List<string>>(context.Domain.DeferredInvokers, "deferredInvokers")
            : new List<string>();

        foreach (var anchor in config.EnqueueMethods)
        {
            var resolution = context.Anchors.Resolve(anchor);
            if (context.AnchorFailed(resolution, Id, Feature)) continue;

            foreach (var method in resolution.Symbols.Where(symbol => symbol.Kind == SymbolKinds.Method))
            foreach (var call in context.Reader.CallsitesTargeting(method.Id)
                         .Where(call => call.Reachable)
                         .OrderBy(call => call.Id, StringComparer.Ordinal))
            {
                Write(context, call, Lambdas(call, config), method.FqName);
            }
        }

        // A deferred invoker is matched by the full name of the method being called (02 §9.8).
        foreach (var name in deferred)
        foreach (var method in MethodsNamed(context, name))
        foreach (var call in context.Reader.CallsitesTargeting(method.Id)
                     .Where(call => call.Reachable)
                     .OrderBy(call => call.Id, StringComparer.Ordinal))
        {
            var lambdas = call.Args
                .Select(argument => argument.Value)
                .OfType<LambdaValue>()
                .Select(lambda => lambda.MethodId)
                .ToList();

            if (lambdas.Count > 0) Write(context, call, lambdas, name);
        }
    }

    private static void Write(
        DomainContext context,
        CallsiteRec call,
        IReadOnlyList<string> lambdas,
        string queue)
    {
        if (lambdas.Count == 0) return;

        context.Writer.Add(new DomainEdgeRow
        {
            Id = Ids.DerivedId(EdgePrefix, new { kind = EdgeKind, callsite = call.Id }),
            Kind = EdgeKind,
            CallsiteId = call.Id,
            FromMethodId = call.CallerId,
            Json = new JsonObject
            {
                ["lambdaIds"] = BoundaryWriter.Of(lambdas),
                ["queue"] = queue
            }.ToJsonString()
        });
    }

    /// <summary>
    /// The lambdas an enqueue carries: a request object is built around them (03 §19), so the
    /// lambda is inside the created request rather than an argument of the enqueue itself.
    /// </summary>
    private static IReadOnlyList<string> Lambdas(CallsiteRec call, QueueSection config)
    {
        var lambdas = new List<string>();

        foreach (var argument in call.Args)
        {
            switch (Unwrap(argument.Value))
            {
                case LambdaValue direct:
                    lambdas.Add(direct.MethodId);
                    break;

                case ObjectCreationValue request:
                {
                    var carried = request.Args.FirstOrDefault(a => a.ParamIndex == config.LambdaArgIndex);

                    if (Unwrap(carried?.Value) is LambdaValue lambda) lambdas.Add(lambda.MethodId);
                    break;
                }
            }
        }

        return lambdas.Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToList();
    }

    private static Value? Unwrap(Value? value)
        => value is LocalValue { Origin: not null } local ? Unwrap(local.Origin) : value;

    /// <summary>Methods addressed by their fully qualified name, as the invoker list writes them.</summary>
    private static IEnumerable<SymbolRec> MethodsNamed(DomainContext context, string fullName)
    {
        var simple = fullName[(fullName.LastIndexOf('.') + 1)..];
        var container = fullName[..Math.Max(0, fullName.LastIndexOf('.'))];

        return context.Reader.SymbolsByName(simple)
            .Where(symbol => symbol.Kind == SymbolKinds.Method)
            .Where(symbol => symbol.FqName.StartsWith(container + ".", StringComparison.Ordinal));
    }
}
