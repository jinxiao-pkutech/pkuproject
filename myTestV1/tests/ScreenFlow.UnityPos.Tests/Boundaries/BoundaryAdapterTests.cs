using System.Text.Json.Nodes;
using ScreenFlow.Core.Ir;
using ScreenFlow.UnityPos.Boundaries;
using ScreenFlow.UnityPos.Entries;
using Xunit;
using Xunit.Abstractions;

namespace ScreenFlow.UnityPos.Tests.Boundaries;

/// <summary>
/// The boundary adapters and the screen entries (02 §9.8, §9.10). Covers E-24 and E-25.
/// </summary>
public sealed class BoundaryAdapterTests
{
    private const string DeviceBroker = "TEC.SEJPOS.Model.DeviceBroker";

    private readonly ITestOutputHelper _output;

    public BoundaryAdapterTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// E-24: a hardware command is a boundary with the response type, the MQTT topic the answer
    /// arrives on, and the methods that can receive it (03 §17). Those are the resume points — the
    /// request and the answer are not connected by any call.
    /// </summary>
    [Fact]
    [Trait("Task", "T17")]
    public void Boundary_Hardware()
    {
        var index = DomainIndex.Editor;
        var hardware = Boundaries(index, BoundaryCategories.ExternalHardware);

        foreach (var boundary in hardware)
            _output.WriteLine($"{boundary.Key}: {boundary.Json}");

        Assert.Contains(hardware, boundary => boundary.Key == DeviceBroker.Split('.').Last() + ".ReadValue"
                                              || boundary.Key == "IEValueRwCommand.ReadValue");

        var read = hardware.Single(boundary => boundary.Key == "IEValueRwCommand.ReadValue");
        var add = hardware.Single(boundary => boundary.Key == "IEValueRwCommand.AddValue");

        // The response type is the awaited one, not Task<T> (02 §9.8).
        Assert.Equal(DeviceBroker + ".EvRwHttpResponse", read.Json["responseType"]!.GetValue<string>());
        Assert.Equal(DeviceBroker + ".EvRwHttpResponse", add.Json["responseType"]!.GetValue<string>());

        // The topic comes from the implementation's topic method (03 §17).
        Assert.Equal(
            "shared-bus-{*}-service-ngp-device-evrw-events",
            read.Json["topicTemplate"]!.GetValue<string>());

        // Both ways back are listed: the controller's own callback and the presenter that
        // registered for the event (E-24).
        var callbacks = read.Json["callbacks"]!.AsArray()
            .Select(node => index.Reader.Symbol(node!.GetValue<string>())?.FqName)
            .OfType<string>()
            .ToList();

        Assert.Contains(DeviceBroker + ".Impl.EValueRwController.OnMessageReceived(" +
                        "MQTTnet.Client.MqttApplicationMessageReceivedEventArgs)", callbacks);

        Assert.Contains(
            "Assets.ScenePosRegister.Payment.Presenter.Script.Payment_SSAW0089_Presenter.OnEdyResponse(" +
            DeviceBroker + ".EvRwMqttResponse)",
            callbacks);

        // The request types are the command's own parameters.
        Assert.Equal(
            new[] { "string", "long", "string" },
            read.Json["requestTypes"]!.AsArray().Select(node => node!.GetValue<string>()));

        // And an edge points at the boundary from the call that crosses it.
        var edges = index.Reader.DomainEdges("boundary");
        Assert.Contains(edges, edge => edge.Json.Contains("IEValueRwCommand.ReadValue", StringComparison.Ordinal));
    }

    /// <summary>
    /// E-25: the other boundary kinds. Each one ends a chain for a different reason, and the key
    /// is what a reader has to look for outside the project.
    /// </summary>
    [Fact]
    [Trait("Task", "T17")]
    public void Boundary_Others()
    {
        var index = DomainIndex.Editor;

        // The microservice is named by the api key the request was built with (03 §18).
        var api = Boundaries(index, BoundaryCategories.ExternalMicroservice);
        var charge = Assert.Single(api);
        Assert.Equal("EdyCharge", charge.Key);
        Assert.False(charge.Json["mockAvailable"]!.GetValue<bool>());
        Assert.Equal("Assets.Common.Script.API.ApiResponse", charge.Json["responseType"]!.GetValue<string>());

        // The native call names the Java class and method (03 §20).
        var native = Boundaries(index, BoundaryCategories.NativeInterop);
        Assert.Contains(native, boundary => boundary.Key == "jp.sej.pos.Beeper.beep");

        // The table is the thing two unrelated methods share, and the writer is the resume point.
        var database = Boundaries(index, BoundaryCategories.DatabaseMediated);
        var table = Assert.Single(database);
        Assert.Equal("item_master", table.Key);

        var writers = table.Json["writers"]!.AsArray()
            .Select(node => index.Reader.Symbol(node!.GetValue<string>())?.FqName)
            .OfType<string>()
            .ToList();

        Assert.Contains(
            "Assets.ScenePosRegister.Standby.Presenter.Script.ItemWriter.Save()",
            writers);

        // Exactly one reflection site: the screen factory and the TCP presenter are explained by
        // their own adapters and are not reported again (02 §9.8).
        var reflection = Boundaries(index, BoundaryCategories.DiAmbiguous)
            .Where(boundary => boundary.Json["reason"]?.GetValue<string>() == "ReflectionConstruction")
            .ToList();

        var single = Assert.Single(reflection);
        Assert.Equal("Activator.CreateInstance", single.Key);
        Assert.Contains("OnClickBtnDebug", single.Json["method"]!.GetValue<string>(), StringComparison.Ordinal);

        // The queue defers work rather than calling it (03 §19).
        var enqueues = index.Reader.DomainEdges(QueueAdapter.EdgeKind);
        Assert.NotEmpty(enqueues);

        foreach (var edge in enqueues)
        {
            var lambdas = JsonNode.Parse(edge.Json)!["lambdaIds"]!.AsArray();
            Assert.NotEmpty(lambdas);

            foreach (var lambda in lambdas)
                Assert.True(index.Reader.Symbol(lambda!.GetValue<string>())?.IsLambda);
        }
    }

    /// <summary>
    /// 02 §9.10: a screen is entered by a click, by being displayed, or by a scan. The fixture's
    /// SSAW0903 has four buttons and SSAW0001 runs <c>OnDisplay</c>.
    /// </summary>
    [Fact]
    [Trait("Task", "T17")]
    public void Entries_ClickAutoScan()
    {
        var index = DomainIndex.Editor;
        var entries = Entries(index);

        foreach (var entry in entries)
            _output.WriteLine($"{entry.Terminal}/{entry.ScreenId} {entry.Trigger} {entry.ControlId}");

        var clicks = entries
            .Where(entry => entry.ScreenId == "SSAW0903" && entry.Trigger == EntryTriggers.Click)
            .ToList();

        Assert.Equal(
            new[] { "btnClose", "selectCharge_1000", "selectCharge_2000", "selectCharge_Other" },
            clicks.Select(entry => entry.ControlId).OrderBy(id => id, StringComparer.Ordinal));

        // Every click entry names the handler that runs, which is where a path starts.
        Assert.All(clicks, entry => Assert.NotNull(index.Reader.Symbol(entry.MethodId)));

        var auto = entries.Single(entry => entry.ScreenId == "SSAW0001" && entry.Trigger == EntryTriggers.Auto);
        Assert.Equal("OnDisplay", index.Reader.Symbol(auto.MethodId)!.Name);
        Assert.Null(auto.ControlId);

        // The fixture has no scan entry, and none is invented (03 §17 covers the scanner, but no
        // presenter in MiniPos registers for it).
        Assert.DoesNotContain(entries, entry => entry.Trigger == EntryTriggers.Scan);

        // Entries exist only for registered screens (02 §9.10).
        var registered = index.Screens().Where(screen => screen.Registered).Select(s => s.ScreenId).ToHashSet();
        Assert.All(entries, entry => Assert.Contains(entry.ScreenId, registered));
    }

    private sealed record BoundaryRecord(string Category, string Key, JsonObject Json);

    private static IReadOnlyList<BoundaryRecord> Boundaries(DomainIndex index, string category)
    {
        using var command = index.Db.Command(
            "SELECT category, contract_key, json FROM boundaries WHERE category = $category ORDER BY contract_key;");

        command.Parameters.AddWithValue("$category", category);

        var rows = new List<BoundaryRecord>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            rows.Add(new BoundaryRecord(
                reader.GetString(0),
                reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                (JsonObject)JsonNode.Parse(reader.GetString(2))!));
        }

        return rows;
    }

    private sealed record EntryRecord(
        string Terminal,
        string ScreenId,
        string Trigger,
        string? ControlId,
        string MethodId);

    private static IReadOnlyList<EntryRecord> Entries(DomainIndex index)
    {
        using var command = index.Db.Command(
            "SELECT terminal, screen_id, trigger, control_id, method_id FROM entries ORDER BY terminal, screen_id, trigger, control_id;");

        var rows = new List<EntryRecord>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            rows.Add(new EntryRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4)));
        }

        return rows;
    }
}
