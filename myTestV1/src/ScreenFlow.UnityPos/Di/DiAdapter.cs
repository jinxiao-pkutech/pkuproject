using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ScreenFlow.Core.Config;
using ScreenFlow.Core.Ir;
using ScreenFlow.Core.Storage;

namespace ScreenFlow.UnityPos.Di;

/// <summary>The <c>di</c> section of <c>unitypos.json</c> (02 §4.2).</summary>
public sealed record DiSection
{
    [JsonPropertyName("locatorType")] public required AnchorSpec LocatorType { get; init; }

    [JsonPropertyName("getMethods")]
    public IReadOnlyList<string> GetMethods { get; init; } = Array.Empty<string>();

    [JsonPropertyName("getAllMethods")]
    public IReadOnlyList<string> GetAllMethods { get; init; } = Array.Empty<string>();

    [JsonPropertyName("registrationMethods")]
    public IReadOnlyList<string> RegistrationMethods { get; init; } = Array.Empty<string>();

    [JsonPropertyName("factoryResolveMethods")]
    public IReadOnlyList<string> FactoryResolveMethods { get; init; } = Array.Empty<string>();
}

/// <summary>One service registration (02 §9.4).</summary>
public sealed record DiRegistration
{
    public required string ServiceTypeId { get; init; }
    public string? ImplTypeId { get; init; }
    public required string Lifetime { get; init; }
    public required string Via { get; init; }
    public string? EvidenceId { get; init; }
    public required string CallsiteId { get; init; }
}

/// <summary>
/// Service registrations and resolutions (02 §9.4; 03 §12).
/// <para>
/// The POS wires its hardware controllers through a service locator, and one interface is
/// registered twice on purpose (03 §12, E-15). Which implementation a call gets then depends on
/// registration order at run time — so both are kept, the resolution is marked ambiguous, and T18
/// turns that into a break link rather than a guessed hop.
/// </para>
/// </summary>
public sealed class DiAdapter : IDomainAdapter
{
    public const string Feature = "di";
    public const string EdgeKind = "serviceResolve";
    public const string EdgePrefix = "de_";
    public const string RegistrationPrefix = "di_";

    public const string ViaDirect = "direct";
    public const string ViaSelf = "self";
    public const string ViaFactoryAlias = "factoryAlias";
    public const string ViaFactoryCreation = "factoryCreation";

    /// <summary>Emitted when a registration's implementation type cannot be named.</summary>
    public const string UnresolvedImplCode = "I_DI_IMPL_UNRESOLVED";

    public string Id => AdapterOrder.Di;

    public void Run(DomainContext context)
    {
        var config = context.Section<DiSection>(context.Domain.Di, "di");

        var anchor = context.Anchors.Resolve(config.LocatorType);
        if (context.AnchorFailed(anchor, Id, Feature)) return;

        var locator = anchor.Require();
        var registrations = Registrations(context, config, locator);

        foreach (var registration in registrations
                     .OrderBy(r => r.ServiceTypeId, StringComparer.Ordinal)
                     .ThenBy(r => r.ImplTypeId, StringComparer.Ordinal))
        {
            context.Writer.Add(new DiRegistrationRow
            {
                Id = Ids.DerivedId(RegistrationPrefix, new
                {
                    service = registration.ServiceTypeId,
                    impl = registration.ImplTypeId ?? string.Empty,
                    callsite = registration.CallsiteId
                }),
                ServiceTypeId = registration.ServiceTypeId,
                ImplTypeId = registration.ImplTypeId,
                Lifetime = registration.Lifetime,
                Via = registration.Via,
                EvidenceId = registration.EvidenceId
            });
        }

        var provider = new DiValueProvider(config, registrations, context.Reader);
        provider.LocatorTypeIds.Add(locator.Id);
        context.Register(provider);

        WriteResolutions(context, config, provider);
    }

    // ------------------------------------------------------------------ registrations

    /// <summary>
    /// Every registration made inside the locator type, lambdas included (02 §9.4). Only there:
    /// a registration elsewhere would be a different container, and guessing is not allowed.
    /// </summary>
    private static IReadOnlyList<DiRegistration> Registrations(
        DomainContext context,
        DiSection config,
        SymbolRec locator)
    {
        var registrations = new List<DiRegistration>();

        foreach (var method in MethodsOf(context, locator))
        foreach (var call in context.Reader.CallsitesIn(method.Id)
                     .OrderBy(call => call.Id, StringComparer.Ordinal))
        {
            var target = call.TargetId is null ? null : context.Reader.Symbol(call.TargetId);
            if (target is null) continue;
            if (!config.RegistrationMethods.Contains(target.Name, StringComparer.Ordinal)) continue;

            var lifetime = target.Name;
            var evidence = context.EvidenceOf(call);
            var typeArgs = call.TypeArgs;

            switch (typeArgs.Count)
            {
                case 2 when typeArgs[0].Id is not null:
                    registrations.Add(new DiRegistration
                    {
                        ServiceTypeId = typeArgs[0].Id!,
                        ImplTypeId = typeArgs[1].Id,
                        Lifetime = lifetime,
                        Via = ViaDirect,
                        EvidenceId = evidence,
                        CallsiteId = call.Id
                    });
                    break;

                case 1 when typeArgs[0].Id is not null:
                {
                    var factory = call.Args.FirstOrDefault(argument => argument.Value is LambdaValue);

                    if (factory is null)
                    {
                        // AddSingleton<T>() registers the concrete type as itself (02 §9.4).
                        registrations.Add(new DiRegistration
                        {
                            ServiceTypeId = typeArgs[0].Id!,
                            ImplTypeId = typeArgs[0].Id,
                            Lifetime = lifetime,
                            Via = ViaSelf,
                            EvidenceId = evidence,
                            CallsiteId = call.Id
                        });

                        break;
                    }

                    var (implId, via) = FromFactory(context, config, (LambdaValue)factory.Value);

                    if (implId is null)
                    {
                        context.Diagnose(DiagRec.Info(
                            UnresolvedImplCode,
                            $"[{Feature}] The factory registered for " +
                            $"'{typeArgs[0].FqName}' does not name an implementation type.",
                            context.Reader.Symbol(call.CallerId)?.File,
                            call.Span.StartLine));
                    }

                    registrations.Add(new DiRegistration
                    {
                        ServiceTypeId = typeArgs[0].Id!,
                        ImplTypeId = implId,
                        Lifetime = lifetime,
                        Via = via,
                        EvidenceId = evidence,
                        CallsiteId = call.Id
                    });

                    break;
                }
            }
        }

        return registrations;
    }

    /// <summary>
    /// The implementation a factory lambda produces: either the type it resolves from the provider
    /// (an alias) or the type it creates (02 §9.4).
    /// </summary>
    private static (string? ImplId, string Via) FromFactory(
        DomainContext context,
        DiSection config,
        LambdaValue factory)
    {
        foreach (var call in context.Reader.CallsitesIn(factory.MethodId)
                     .OrderBy(call => call.Id, StringComparer.Ordinal))
        {
            var target = call.TargetId is null ? null : context.Reader.Symbol(call.TargetId);
            if (target is null) continue;
            if (!config.FactoryResolveMethods.Contains(target.Name, StringComparer.Ordinal)) continue;

            var resolved = call.TypeArgs.FirstOrDefault()?.Id;
            if (resolved is not null) return (resolved, ViaFactoryAlias);
        }

        foreach (var creation in context.Reader.CreationsIn(factory.MethodId))
        {
            var created = creation.ObjectCreation?.TypeId;
            if (created is not null) return (created, ViaFactoryCreation);
        }

        return (null, ViaFactoryAlias);
    }

    /// <summary>The locator's own methods and every lambda inside them (02 §9.4).</summary>
    private static IReadOnlyList<SymbolRec> MethodsOf(DomainContext context, SymbolRec locator)
    {
        var methods = context.Reader.SymbolsInContainer(locator.Id)
            .Where(symbol => symbol.Kind == SymbolKinds.Method)
            .ToList();

        var queue = new Queue<SymbolRec>(methods);
        var all = methods.ToDictionary(method => method.Id, StringComparer.Ordinal);

        while (queue.Count > 0)
        {
            foreach (var nested in context.Reader.SymbolsInContainer(queue.Dequeue().Id)
                         .Where(symbol => symbol.Kind == SymbolKinds.Method))
            {
                if (all.TryAdd(nested.Id, nested)) queue.Enqueue(nested);
            }
        }

        return all.Values.OrderBy(method => method.Id, StringComparer.Ordinal).ToList();
    }

    // ------------------------------------------------------------------ resolutions

    /// <summary>
    /// One edge per service lookup (02 §9.4), with what it resolved to. A lookup with several
    /// registrations is recorded as ambiguous, which is the fact: the code does not say which one.
    /// </summary>
    private static void WriteResolutions(DomainContext context, DiSection config, DiValueProvider provider)
    {
        foreach (var name in config.GetMethods.Concat(config.GetAllMethods).Distinct(StringComparer.Ordinal))
        foreach (var method in context.Reader.SymbolsByName(name)
                     .Where(symbol => symbol.Kind == SymbolKinds.Method))
        {
            if (!provider.IsLocatorMethod(method)) continue;

            foreach (var call in context.Reader.CallsitesTargeting(method.Id)
                         .Where(call => call.Reachable)
                         .OrderBy(call => call.Id, StringComparer.Ordinal))
            {
                var service = call.TypeArgs.FirstOrDefault();
                var impls = service?.Id is null
                    ? Array.Empty<string>()
                    : provider.ImplementationsOf(service.Id);

                var json = new JsonObject
                {
                    ["serviceTypeId"] = service?.Id,
                    ["serviceType"] = service?.FqName,
                    ["impls"] = new JsonArray(impls.Select(impl => (JsonNode)impl!).ToArray()),
                    ["ambiguous"] = impls.Count > 1,
                    ["all"] = config.GetAllMethods.Contains(method.Name, StringComparer.Ordinal)
                };

                context.Writer.Add(new DomainEdgeRow
                {
                    Id = Ids.DerivedId(EdgePrefix, new { kind = EdgeKind, callsite = call.Id }),
                    Kind = EdgeKind,
                    CallsiteId = call.Id,
                    FromMethodId = call.CallerId,
                    Json = json.ToJsonString()
                });
            }
        }
    }
}
