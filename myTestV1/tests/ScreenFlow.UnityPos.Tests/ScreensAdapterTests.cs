using ScreenFlow.Core.Ir;
using ScreenFlow.UnityPos.Screens;
using Xunit;
using Xunit.Abstractions;

namespace ScreenFlow.UnityPos.Tests;

/// <summary>
/// The screens, enumerations and navigation adapters (02 §9.1, §9.2, §9.6).
/// Covers E-09, E-10 and E-11.
/// </summary>
public sealed class ScreensAdapterTests
{
    private readonly ITestOutputHelper _output;

    public ScreensAdapterTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// E-09: every screen the <c>ScreenID</c> table declares appears, and the three that no registry
    /// mentions are marked unregistered rather than left out — "not registered" is the answer a
    /// reader needs, and an absent row would read as "no such screen".
    /// </summary>
    [Fact]
    [Trait("Task", "T12")]
    public void Screens_Registered()
    {
        var index = DomainIndex.Editor;
        var screens = index.Screens();

        foreach (var screen in screens)
            _output.WriteLine($"{screen.Terminal,-8} {screen.ScreenId} registered={screen.Registered} popup={screen.IsPopup}");

        // The five registered screens, on the terminal their registry belongs to (03 §6).
        Assert.Equal(
            new[]
            {
                ("employee", "SSAW0001"),
                ("employee", "SSAW0089"),
                ("employee", "SSAW0281"),
                ("customer", "SSAW0903"),
                ("customer", "SSAW0907")
            }.OrderBy(s => s.Item1, StringComparer.Ordinal).ThenBy(s => s.Item2, StringComparer.Ordinal),
            screens.Where(s => s.Registered)
                .Select(s => (s.Terminal, s.ScreenId))
                .OrderBy(s => s.Terminal, StringComparer.Ordinal)
                .ThenBy(s => s.ScreenId, StringComparer.Ordinal));

        // Declared but in no registry (E-09): kept with no terminal and registered = false.
        var unregistered = screens.Where(s => !s.Registered).ToList();
        Assert.Equal(
            new[] { "SCEW0001", "SSAW0002", "SSAW1113" },
            unregistered.Select(s => s.ScreenId).OrderBy(id => id, StringComparer.Ordinal));

        Assert.All(unregistered, s => Assert.Equal(ScreensAdapter.NoTerminal, s.Terminal));

        // The name and the popup flag come from the initialiser arguments (02 §9.1).
        var ssaw0903 = screens.Single(s => s.ScreenId == "SSAW0903");
        Assert.Equal("Edy現金チャージ額指定（客面残高不足）", ssaw0903.Name);
        Assert.True(ssaw0903.IsPopup);
        Assert.Equal("customer", ssaw0903.Terminal);

        var ssaw0001 = screens.Single(s => s.ScreenId == "SSAW0001");
        Assert.False(ssaw0001.IsPopup);

        // The registry entry names the view and the presenter type (03 §6).
        var view = index.Symbol(
            SymbolKinds.Type,
            "Assets.SceneCustomer.Reception.EdyCharge.View.Script.EdyCharge_SSAW0903_View");

        var presenter = index.Symbol(
            SymbolKinds.Type,
            "Assets.SceneCustomer.Reception.EdyCharge.Presenter.Script.EdyCharge_SSAW0903_Presenter");

        Assert.Equal(view.Id, ssaw0903.ViewTypeId);
        Assert.Equal(presenter.Id, ssaw0903.PresenterTypeId);

        // Every screen cites the ScreenID field it was declared by, with evidence.
        Assert.All(screens, s => Assert.NotNull(s.ScreenFieldId));
        Assert.All(screens, s => Assert.NotNull(s.EvidenceId));

        // Nothing was disabled, so the table is complete (02 §4.2).
        Assert.Empty(index.Report.DisabledFeatures);
    }

    /// <summary>
    /// E-10: SSAW0002 is referenced three times in the fixture — inside <c>#if TeamUI</c>, in a
    /// comment, and after a <c>return</c> — and none of them is a transition. An edge to it would be
    /// a screen path that does not exist.
    /// </summary>
    [Fact]
    [Trait("Task", "T12")]
    public void Navigation_ExcludedSources_NoEdgeToSSAW0002()
    {
        var index = DomainIndex.Editor;
        var edges = index.Edges(NavigationAdapter.NavigateKind);

        Assert.NotEmpty(edges);
        Assert.DoesNotContain(edges, edge => DomainIndex.ScreenIdsOf(edge).Contains("SSAW0002"));

        // The screen itself is still known — it is declared, just never navigated to (E-09).
        Assert.Contains(index.Screens(), s => s.ScreenId == "SSAW0002");

        // The unreachable call is in the index as a fact; it simply produces no edge (02 §5.5).
        var legacy = index.Symbol(
            SymbolKinds.Method,
            "Assets.ScenePosRegister.Reception.EdyCharge.Presenter.Script.EdyCharge_SSAW0281_Presenter.Legacy()");

        var unreachable = index.Reader.CallsitesIn(legacy.Id).Where(c => !c.Reachable).ToList();
        Assert.NotEmpty(unreachable);
        Assert.All(unreachable, c => Assert.DoesNotContain(edges, edge => edge.CallsiteId == c.Id));

        // The transitions that do exist are there.
        var reached = edges.SelectMany(DomainIndex.ScreenIdsOf).Distinct().OrderBy(id => id, StringComparer.Ordinal);
        Assert.Contains("SSAW0089", reached);
        Assert.Contains("SSAW0001", reached);
    }

    /// <summary>
    /// E-11: SCEW0001 is navigated to only from inside a <c>#if UNITY_EDITOR</c> block, so the edge
    /// exists under the editor profile and not under the Android one. The answer depends on the
    /// profile, and both answers are correct for their profile (03 §21).
    /// </summary>
    [Fact]
    [Trait("Task", "T12")]
    public void Navigation_UnityEditorBlock_ProfileDependent()
    {
        var editorTargets = DomainIndex.Editor.Edges(NavigationAdapter.NavigateKind)
            .SelectMany(DomainIndex.ScreenIdsOf)
            .ToList();

        var androidTargets = DomainIndex.Android.Edges(NavigationAdapter.NavigateKind)
            .SelectMany(DomainIndex.ScreenIdsOf)
            .ToList();

        Assert.Contains("SCEW0001", editorTargets);
        Assert.DoesNotContain("SCEW0001", androidTargets);

        // Everything else is the same, so the difference really is the guarded block.
        Assert.Contains("SSAW0089", androidTargets);
        Assert.Contains("SSAW0001", androidTargets);

        // The screen is declared in both profiles; only the transition differs.
        Assert.Contains(DomainIndex.Android.Screens(), s => s.ScreenId == "SCEW0001");
    }

    /// <summary>
    /// 02 §9.6: a screen argument that is resolved by reflection at run time cannot be named, and
    /// the edge says so with the text it could not resolve — the customer-side TCP presenter looks
    /// its target up through <c>typeof(ScreenID).GetField</c> (03 §10.3).
    /// </summary>
    [Fact]
    [Trait("Task", "T12")]
    public void Navigation_ReflectionTarget_IsRecordedAsUnresolved()
    {
        var edges = DomainIndex.Editor.Edges(NavigationAdapter.NavigateKind);

        var unresolved = edges.Where(edge => DomainIndex.ScreenIdsOf(edge).Count == 0).ToList();
        Assert.NotEmpty(unresolved);

        foreach (var edge in unresolved)
        {
            var json = System.Text.Json.Nodes.JsonNode.Parse(edge.Json)!;
            Assert.False(json["complete"]!.GetValue<bool>());
            Assert.Contains("GetField", json["unresolvedText"]!.GetValue<string>(), StringComparison.Ordinal);
        }

        // Each one is reported, so a build log shows how many targets stayed unknown.
        Assert.Equal(
            unresolved.Count,
            DomainIndex.Editor.Reader.Diagnostics()
                .Count(d => d.Code == NavigationAdapter.UnresolvedScreenCode));
    }

    /// <summary>
    /// 02 §9.2 and 03 §5: the POS enumerations are static fields, not C# enums, and
    /// <c>StageNo</c> is declared in two files — one symbol, so one member set (E-05).
    /// </summary>
    [Fact]
    [Trait("Task", "T12")]
    public void Enumerations_StageAndPsCodeMembers()
    {
        var index = DomainIndex.Editor;

        var stageNo = index.Symbol(SymbolKinds.Type, "TEC.SEJPOS.Model.Apl.KeySeq.StageNo");
        var members = index.Reader.AllSymbols()
            .Where(s => s.Kind == SymbolKinds.Field && s.ContainerId == stageNo.Id)
            .ToList();

        // Members from both partial declarations (03 §5).
        var files = members.Select(m => m.File).Distinct().ToList();
        Assert.True(files.Count >= 2, $"expected members from both partial files, found {string.Join(", ", files)}");

        using var command = index.Db.Command(
            "SELECT name, value_id, value_name FROM enum_members WHERE type_id = $type ORDER BY name;");
        command.Parameters.AddWithValue("$type", stageNo.Id);

        var rows = new List<(string Name, int? ValueId, string? ValueName)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                rows.Add((
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetInt32(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2)));
            }
        }

        foreach (var row in rows) _output.WriteLine($"{row.Name} = {row.ValueId} / {row.ValueName}");

        Assert.Contains(("CSP_NML_EDY_PAY_CHECK", (int?)4, (string?)"CSP_NML_EDY_PAY_CHECK"), rows);
        Assert.Contains(rows, row => row.Name == "CSP_NML_EDY_CHARGE_SELECT");

        // The card stage lives in the second partial file and is collected all the same.
        Assert.Contains(rows, row => row.Name.Contains("CARDCOMM", StringComparison.Ordinal));

        // PsCode is another Enumeration subclass, with its own members (03 §11.1).
        var psCode = index.Symbol(SymbolKinds.Type, "TEC.SEJPOS.Model.Apl.KeySeq.PsCode");
        var psRows = PsRows(index, psCode.Id);

        Assert.Contains(psRows, row => row.Name == "PS_CEDY_CHARGEOTHER" && row.ValueName == "PS_CEDY_CHARGEOTHER");
        Assert.All(psRows, row => Assert.NotNull(row.ValueId));
    }

    private static List<(string Name, int? ValueId, string? ValueName)> PsRows(DomainIndex index, string typeId)
    {
        using var command = index.Db.Command(
            "SELECT name, value_id, value_name FROM enum_members WHERE type_id = $type ORDER BY name;");
        command.Parameters.AddWithValue("$type", typeId);

        var rows = new List<(string Name, int? ValueId, string? ValueName)>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            rows.Add((
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        return rows;
    }

    /// <summary>
    /// 02 §4.2: an anchor that does not resolve disables the feature that depends on it, and the
    /// build report lists it. The alternative — falling back to a global search by simple name — is
    /// forbidden, because it would anchor the adapter to whatever type happens to share the name.
    /// </summary>
    [Fact]
    [Trait("Task", "T12")]
    public void Anchor_Unresolved_DisablesFeature()
    {
        var configDirectory = DomainIndex.ConfigCopyWith(domain =>
            domain["screens"]!["screenIdType"]!["file"] = "Assets/Nowhere/ScreenID.cs");

        using var index = DomainIndex.Build("fixture", configDirectory);

        Assert.Contains(ScreensAdapter.Feature, index.Report.DisabledFeatures);
        Assert.Contains(
            index.Reader.Diagnostics(),
            d => d.Code == Anchors.AnchorResolver.UnresolvedCode && d.Message.Contains("screens", StringComparison.Ordinal));

        // No screens, and no navigation either: the edges cannot be named without the screen table.
        Assert.Empty(index.Screens());
        Assert.Contains(NavigationAdapter.Feature, index.Report.DisabledFeatures);
        Assert.Empty(index.Edges(NavigationAdapter.NavigateKind));

        // The rest of the index is unaffected — the build still succeeded (02 §4.2).
        Assert.True(index.Report.CountOf("symbols") > 0);
        Assert.True(index.Report.CountOf("enum_members") > 0);

        // meta.disabledFeatures carries it to every later query (02 §7.4).
        var disabled = index.Reader.Meta()[Core.Storage.MetaKeys.DisabledFeatures];
        Assert.Contains(ScreensAdapter.Feature, disabled, StringComparison.Ordinal);
    }

    /// <summary>
    /// 02 §2: the adapters run in the fixed order, because each one reads what the previous wrote.
    /// </summary>
    [Fact]
    [Trait("Task", "T12")]
    public void Domain_AdaptersRunInTheFixedOrder()
    {
        var domain = new UnityPosDomain();

        // The resource readers come first — they are phase P6, and every adapter after them reads
        // what they loaded (02 §2).
        Assert.Equal(
            new[]
            {
                AdapterOrder.Resources, AdapterOrder.Screens, AdapterOrder.Enumerations,
                AdapterOrder.UiBindings, AdapterOrder.Di, AdapterOrder.KeySeq,
                AdapterOrder.Navigation, AdapterOrder.Tcp, AdapterOrder.Tcp + ":receivers",
                AdapterOrder.Device, AdapterOrder.Api, AdapterOrder.Native, AdapterOrder.Sql,
                AdapterOrder.Queue, AdapterOrder.Reflection, AdapterOrder.Entries
            },
            domain.Adapters.Select(a => a.Id));

        Assert.Equal("P6", domain.Adapters.Single(a => a.Id == AdapterOrder.Resources).Phase);
        Assert.All(
            domain.Adapters.Where(a => a.Id != AdapterOrder.Resources),
            adapter => Assert.Equal(UnityPosDomain.AdapterPhase, adapter.Phase));

        // Registration order does not matter; the stage sorts by 02 §2.
        var shuffled = new UnityPosDomain(new IDomainAdapter[]
        {
            new NavigationAdapter(),
            new EnumerationsAdapter(),
            new ScreensAdapter(),
            new ScreenFlow.UnityPos.Tcp.TcpReceiverAdapter(),
            new ScreenFlow.UnityPos.KeySeq.KeySeqAdapter(),
            new ScreenFlow.UnityPos.Di.DiAdapter(),
            new ScreenFlow.UnityPos.Entries.EntriesAdapter(),
            new ScreenFlow.UnityPos.Boundaries.ReflectionAdapter(),
            new ScreenFlow.UnityPos.Boundaries.QueueAdapter(),
            new ScreenFlow.UnityPos.Boundaries.SqlAdapter(),
            new ScreenFlow.UnityPos.Boundaries.NativeAdapter(),
            new ScreenFlow.UnityPos.Boundaries.ApiAdapter(),
            new ScreenFlow.UnityPos.Boundaries.DeviceAdapter(),
            new ScreenFlow.UnityPos.Ui.ControlsAdapter(),
            new ScreenFlow.UnityPos.Tcp.TcpSendAdapter(),
            new ScreenFlow.UnityPos.Resources.ResourcesAdapter()
        });

        Assert.Equal(domain.Adapters.Select(a => a.Id), shuffled.Adapters.Select(a => a.Id));
        Assert.Equal(UnityPosDomain.AdapterPhase, domain.Phase);
    }
}
