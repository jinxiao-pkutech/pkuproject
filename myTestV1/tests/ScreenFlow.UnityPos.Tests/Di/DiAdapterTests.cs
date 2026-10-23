using System.Text.Json.Nodes;
using ScreenFlow.Core.Config;
using ScreenFlow.Core.Ir;
using ScreenFlow.Core.Resolution;
using ScreenFlow.UnityPos.Di;
using Xunit;
using Xunit.Abstractions;

namespace ScreenFlow.UnityPos.Tests.Di;

/// <summary>
/// The DI adapter and its value provider (02 §9.4; 03 §12). Covers E-15.
/// </summary>
public sealed class DiAdapterTests
{
    private const string DeviceBroker = "TEC.SEJPOS.Model.DeviceBroker";

    private readonly ITestOutputHelper _output;

    public DiAdapterTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// E-15: eight registrations, one service registered twice. Both implementations are kept —
    /// which one a lookup returns depends on registration order at run time (03 §12).
    /// </summary>
    [Fact]
    [Trait("Task", "T16")]
    public void Di_Registrations()
    {
        var index = DomainIndex.Editor;
        var registrations = Registrations(index);

        foreach (var registration in registrations)
            _output.WriteLine($"{registration.Service} -> {registration.Impl} ({registration.Via}, {registration.Lifetime})");

        Assert.Equal(8, registrations.Count);

        // A concrete type registered on its own is registered as itself (02 §9.4).
        var controller = registrations.Single(r => r.Service == DeviceBroker + ".Impl.EValueRwController");
        Assert.Equal(DeviceBroker + ".Impl.EValueRwController", controller.Impl);
        Assert.Equal(DiAdapter.ViaSelf, controller.Via);

        // A factory that resolves another registration is an alias (02 §9.4).
        var command = registrations.Single(r => r.Service == DeviceBroker + ".IEValueRwCommand");
        Assert.Equal(DeviceBroker + ".Impl.EValueRwController", command.Impl);
        Assert.Equal(DiAdapter.ViaFactoryAlias, command.Via);

        // Two implementations for one service (E-15): both recorded, neither preferred.
        var ambiguous = registrations.Where(r => r.Service == DeviceBroker + ".IDeviceBrokerMessageEvent").ToList();
        Assert.Equal(2, ambiguous.Count);
        Assert.Equal(
            new[] { DeviceBroker + ".Impl.EValueRwController", DeviceBroker + ".Impl.ScannerController" },
            ambiguous.Select(r => r.Impl).OrderBy(impl => impl, StringComparer.Ordinal));

        // A two-argument registration names service and implementation directly.
        var api = registrations.Single(r => r.Service == "Assets.Common.Script.API.IApiConnector");
        Assert.Equal("Assets.Common.Script.API.ApiConnector", api.Impl);
        Assert.Equal(DiAdapter.ViaDirect, api.Via);

        Assert.All(registrations, r => Assert.Equal("AddSingleton", r.Lifetime));

        // The lookup of the doubly registered service is marked ambiguous (02 §9.4).
        var resolutions = Resolutions(index);
        var byService = resolutions.Where(r => r.ServiceType == DeviceBroker + ".IDeviceBrokerMessageEvent").ToList();

        Assert.NotEmpty(byService);
        Assert.All(byService, resolution => Assert.True(resolution.Ambiguous));
        Assert.All(byService, resolution => Assert.Equal(2, resolution.Impls.Count));

        // And a singly registered one is not.
        Assert.All(
            resolutions.Where(r => r.ServiceType == DeviceBroker + ".IEValueRwCommand"),
            resolution => Assert.False(resolution.Ambiguous));
    }

    /// <summary>
    /// 02 §8.1 and §9.4: with the DI provider in hand, a call on a service interface narrows to the
    /// registered implementation — that is what keeps a hardware call chain going (03 §17).
    /// </summary>
    [Fact]
    [Trait("Task", "T16")]
    public void Di_ProviderNarrowsReceiver()
    {
        var index = DomainIndex.Editor;
        var (provider, traversal) = Provider(index);

        var contract = index.Symbol(
            SymbolKinds.Method,
            DeviceBroker + ".IEValueRwCommand.ReadValue(string, long, string)");

        var implementation = index.Symbol(
            SymbolKinds.Method,
            DeviceBroker + ".Impl.EValueRwController.ReadValue(string, long, string)");

        var calls = index.Reader.CallsitesTargeting(contract.Id);
        var call = Assert.Single(calls);

        var resolution = ResolutionIndex.Build(index.Reader);
        var fields = new FieldPointsTo(index.Reader, resolution);
        var values = new ValueResolver(index.Reader, resolution, fields);
        var dispatcher = new Dispatcher(index.Reader, resolution, values, traversal);

        var controller = index.Symbol(SymbolKinds.Type, DeviceBroker + ".Impl.EValueRwController");

        // The receiver is ServiceLocator.GetService<IEValueRwCommand>(): without the provider the
        // core cannot say what it returns, and with it the concrete type is known (02 §8.2).
        Assert.NotNull(call.Receiver);
        Assert.Empty(values.ResolveConcrete(call.Receiver!, ResolveContext.FromConfig(traversal)).Types);

        var withProvider = ResolveContext.FromConfig(traversal, new IValueProvider[] { provider });
        var receiverTypes = values.ResolveConcrete(call.Receiver!, withProvider);

        Assert.Equal(new[] { controller.Id }, receiverTypes.Types);
        Assert.True(receiverTypes.Complete);
        Assert.False(receiverTypes.Ambiguous);

        // The call therefore dispatches to that implementation, certainly.
        var narrowed = dispatcher.Dispatch(call, withProvider);

        Assert.Equal(new[] { implementation.Id }, narrowed.Candidates);
        Assert.Equal(DispatchCertainty.Certain, narrowed.Certainty);

        // The ambiguous service resolves to both implementations and says it is ambiguous, which
        // is what T18 turns into a MultipleDiRegistrations break (02 §8.3).
        var ambiguousCall = index.Reader.CallsitesTargeting(
                index.Symbol(SymbolKinds.Method, DeviceBroker + ".IDeviceBrokerMessageEvent.GetTopic()").Id)
            .FirstOrDefault();

        if (ambiguousCall?.Receiver is not null)
        {
            var resolved = values.ResolveConcrete(ambiguousCall.Receiver, withProvider);
            Assert.True(resolved.Ambiguous);
            Assert.Equal(2, resolved.Types.Count);
        }

        // The ambiguous service stays ambiguous: both implementations are returned (E-15).
        var ambiguous = provider.ImplementationsOf(
            index.Symbol(SymbolKinds.Type, DeviceBroker + ".IDeviceBrokerMessageEvent").Id);

        Assert.Equal(2, ambiguous.Count);
    }

    // ------------------------------------------------------------------ helpers

    private static (DiValueProvider Provider, TraversalConfig Traversal) Provider(DomainIndex index)
    {
        var (_, domain) = ConfigLoader.LoadAll(DomainIndex.ConfigDirectory);
        var config = domain.Section<DiSection>(domain.Di, ConfigLoader.SectionOptions)!;

        var registrations = Registrations(index)
            .Select(row => new DiRegistration
            {
                ServiceTypeId = row.ServiceTypeId,
                ImplTypeId = row.ImplTypeId,
                Lifetime = row.Lifetime ?? "AddSingleton",
                Via = row.Via ?? DiAdapter.ViaDirect,
                CallsiteId = string.Empty
            })
            .ToList();

        var provider = new DiValueProvider(config, registrations, index.Reader);
        provider.LocatorTypeIds.Add(index.Symbol(SymbolKinds.Type, "TEC.SEJPOS.Model.ServiceLocator").Id);

        return (provider, domain.Traversal);
    }

    private sealed record RegistrationRow(
        string ServiceTypeId,
        string? ImplTypeId,
        string? Service,
        string? Impl,
        string? Lifetime,
        string? Via);

    private static IReadOnlyList<RegistrationRow> Registrations(DomainIndex index)
    {
        using var command = index.Db.Command(
            """
            SELECT r.service_type_id, r.impl_type_id,
                   (SELECT fq_name FROM symbols WHERE id = r.service_type_id),
                   (SELECT fq_name FROM symbols WHERE id = r.impl_type_id),
                   r.lifetime, r.via
            FROM di_registrations r ORDER BY 3, 4;
            """);

        var rows = new List<RegistrationRow>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            rows.Add(new RegistrationRow(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return rows;
    }

    private sealed record ResolutionRow(string? ServiceType, IReadOnlyList<string> Impls, bool Ambiguous);

    private static IReadOnlyList<ResolutionRow> Resolutions(DomainIndex index)
        => index.Reader.DomainEdges(DiAdapter.EdgeKind)
            .Select(edge =>
            {
                var json = JsonNode.Parse(edge.Json)!;

                return new ResolutionRow(
                    json["serviceType"]?.GetValue<string>(),
                    json["impls"]!.AsArray().Select(node => node!.GetValue<string>()).ToList(),
                    json["ambiguous"]!.GetValue<bool>());
            })
            .ToList();
}
