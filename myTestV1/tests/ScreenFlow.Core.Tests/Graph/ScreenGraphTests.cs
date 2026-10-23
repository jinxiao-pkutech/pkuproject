using System.Text.Json;
using System.Text.Json.Nodes;
using ScreenFlow.Core.Breaks;
using ScreenFlow.Core.Graph;
using ScreenFlow.Core.Ir;
using ScreenFlow.Core.Storage;
using ScreenFlow.Core.Tests.Resolution;
using Xunit;
using Xunit.Abstractions;

namespace ScreenFlow.Core.Tests.Graph;

/// <summary>
/// The screen graph and its break links (02 §10, §8.3).
/// Covers E-06, E-26, E-27, E-28, E-29, E-30, E-36 and E-37.
/// </summary>
public sealed class ScreenGraphTests
{
    private readonly ITestOutputHelper _output;

    public ScreenGraphTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// E-26: pressing "other amount" on the customer screen reaches the amount-entry screen two
    /// ways — through the payment screen and through the charge-selection screen — because either
    /// one can be the employee-side screen that receives the event (03 §10.3, §13.2).
    /// </summary>
    [Fact]
    [Trait("Task", "T18")]
    public void Graph_0903To0907_TwoChains()
    {
        var edges = Edges(FixtureIndex.WithGraph)
            .Where(edge => edge.From.ToString() == "customer/SSAW0903"
                           && edge.To?.ToString() == "customer/SSAW0907"
                           && edge.Trigger.ControlId == "selectCharge_Other")
            .ToList();

        foreach (var edge in edges)
            _output.WriteLine($"cost={edge.Cost} {string.Join(" | ", edge.Conditions)}");

        Assert.Equal(2, edges.Count);
        Assert.All(edges, edge => Assert.Equal(Confidences.Candidate, edge.Confidence));

        // One chain goes through the payment screen, under its own guard (E-26).
        var viaPayment = edges.Single(edge => edge.Conditions.Contains("员工面当前画面 ∈ {SSAW0089}"));
        Assert.Contains(
            "res.ResultCode.Id == ResultStatusCode.GA_NO_ERROR.Id",
            viaPayment.Conditions);

        // The other goes through the charge selection screen.
        var viaCharge = edges.Single(edge => edge.Conditions.Contains("员工面当前画面 ∈ {SSAW0281}"));

        // The stage condition belongs to a key sequence branch, not to these two chains (E-26).
        Assert.All(
            edges,
            edge => Assert.DoesNotContain(edge.Conditions, condition => condition.StartsWith("当前 Stage", StringComparison.Ordinal)));

        // Both chains really cross to the employee terminal and back.
        foreach (var edge in new[] { viaPayment, viaCharge })
        {
            Assert.Contains(edge.Chain, step => step.Kind == ChainStepKinds.SocketSend);
            Assert.Contains(edge.Chain, step => step.Kind == ChainStepKinds.SocketReceive);
            Assert.Contains(edge.Chain, step => step.Terminal == "employee");
            Assert.Contains(edge.Chain, step => step.Kind == ChainStepKinds.RemoteDisplay);
        }
    }

    /// <summary>
    /// E-27: those chains stop being certain for named reasons — the receiving screen is run-time
    /// state, the work is queued, and the key sequence handler depends on the stage.
    /// </summary>
    [Fact]
    [Trait("Task", "T18")]
    public void Graph_0903To0907_Breaks()
    {
        var index = FixtureIndex.WithGraph;
        var breaks = Breaks(index);

        var edges = Edges(index)
            .Where(edge => edge.From.ToString() == "customer/SSAW0903" && edge.To?.ToString() == "customer/SSAW0907")
            .ToList();

        var all = edges.SelectMany(edge => edge.BreakIds).Distinct(StringComparer.Ordinal).ToList();
        var reached = all.Select(id => breaks[id]).ToList();

        foreach (var record in reached)
            _output.WriteLine($"{record.Category}/{record.Reason} resume={record.ResumeConfidence}: {record.Why}");

        // The peer's receiving screen is decided at run time, and both candidates are named.
        var tcp = reached.Single(record => record.Category == BreakCategories.TerminalTcpCurrentScreen);
        Assert.Equal(ResumeConfidences.Multiple, tcp.ResumeConfidence);
        Assert.Equal(2, tcp.ResumeCount);

        // The queued work runs later.
        Assert.Contains(reached, record => record.Category == BreakCategories.DeferredExecution);

        // And the payment chain passes the key sequence, whose handler depends on the stage.
        var viaPayment = edges.Single(edge => edge.Conditions.Contains("员工面当前画面 ∈ {SSAW0089}"));
        var paymentBreaks = viaPayment.BreakIds.Select(id => breaks[id]).ToList();

        Assert.Contains(paymentBreaks, record => record.Category == BreakCategories.KeySeqStageDependent);

        // Every break explains itself with one of the templates of 02 §8.3.
        Assert.All(reached, record => Assert.False(string.IsNullOrWhiteSpace(record.Why)));
    }

    /// <summary>
    /// E-28: one press drives the other terminal's screen and this terminal's screen, and the
    /// local navigation is plain enough to be certain.
    /// </summary>
    [Fact]
    [Trait("Task", "T18")]
    public void Graph_RemoteDisplayAndLocal()
    {
        var index = FixtureIndex.WithGraph;
        var edges = Edges(index);
        var breaks = Breaks(index);

        var remote = edges.Single(edge => edge.From.ToString() == "employee/SSAW0089"
                                          && edge.To?.ToString() == "customer/SSAW0903"
                                          && edge.Trigger.ControlId == "btnEdy");

        Assert.Contains(remote.Chain, step => step.Kind == ChainStepKinds.RemoteDisplay);

        // The same press first asks the hardware for the balance (03 §17), and that is a break.
        var edyEdges = edges.Where(edge => edge.From.ToString() == "employee/SSAW0089" && edge.Trigger.ControlId == "btnEdy");
        var hardware = edyEdges
            .SelectMany(edge => edge.BreakIds)
            .Select(id => breaks[id])
            .Where(record => record.Category == BreakCategories.ExternalHardware)
            .ToList();

        Assert.NotEmpty(hardware);
        Assert.Contains(hardware, record => record.Why.Contains("IOBOX", StringComparison.Ordinal));

        // A plain local navigation has nothing uncertain about it (02 §10.4).
        var local = edges.Single(edge => edge.From.ToString() == "employee/SSAW0001"
                                         && edge.To?.ToString() == "employee/SSAW0089"
                                         && edge.Trigger.ControlId == "btnPay");

        Assert.Equal(Confidences.Certain, local.Confidence);
        Assert.Empty(local.BreakIds);
        Assert.Equal(1.0, local.Cost);
    }

    /// <summary>
    /// E-29: a send nothing answers is a dangling edge — the operation leads somewhere, and that
    /// somewhere turns out not to exist in this project.
    /// </summary>
    [Fact]
    [Trait("Task", "T18")]
    public void Graph_DanglingUnmatchedTcp()
    {
        var index = FixtureIndex.WithGraph;
        var breaks = Breaks(index);

        var dangling = Edges(index)
            .Single(edge => edge.From.ToString() == "employee/SSAW0089" && edge.Trigger.ControlId == "btnTest");

        Assert.Null(dangling.To);
        Assert.NotNull(dangling.EndsAtBreak);
        Assert.Equal(Confidences.Candidate, dangling.Confidence);

        var record = breaks[dangling.EndsAtBreak!];
        Assert.Equal(BreakCategories.TerminalTcpCurrentScreen, record.Category);
        Assert.Equal(BreakReasons.StringKeyedDispatchUnmatched, record.Reason);
        Assert.Equal(ResumeConfidences.None, record.ResumeConfidence);
        Assert.Contains("PosRegister.Nobody.Handles", record.Why, StringComparison.Ordinal);

        // A dangling edge costs more than one that arrives (02 §10.4).
        Assert.True(dangling.Cost > 2.0);
    }

    /// <summary>
    /// E-30: an unresolvable key code stops that branch, not the whole chain — the same method's
    /// later send still reaches the next screen.
    /// </summary>
    [Fact]
    [Trait("Task", "T18")]
    public void Graph_UnresolvedKeySeq_Recorded()
    {
        var index = FixtureIndex.WithGraph;
        var breaks = Breaks(index);
        var edges = Edges(index).Where(edge => edge.From.ToString() == "employee/SSAW0281").ToList();

        // The key sequence call with an unknown key code is recorded as its own break.
        var unresolved = breaks.Values.Single(record =>
            record.Category == BreakCategories.KeySeqStageDependent
            && record.Reason == BreakReasons.UnresolvedConstantArgument);

        Assert.Equal(ResumeConfidences.None, unresolved.ResumeConfidence);
        Assert.Contains("事件码无法静态确定", unresolved.Why, StringComparison.Ordinal);

        // And the wrapper's send in the same handler still produces the edge to SSAW0907.
        Assert.Contains(edges, edge => edge.To?.ToString() == "customer/SSAW0907");
    }

    /// <summary>
    /// E-06: two types share the simple name <c>KsPosa</c>, and only the one the code actually
    /// constructs is on the chain. Matching by short name would put both there (CLAUDE.md §4).
    /// </summary>
    [Fact]
    [Trait("Task", "T18")]
    public void Symbols_DuplicateSimpleNames_NotMixed()
    {
        var index = FixtureIndex.WithGraph;

        var types = index.Reader.SymbolsByName("KsPosa")
            .Where(symbol => symbol.Kind == SymbolKinds.Type && symbol.IsSource)
            .ToList();

        Assert.Equal(2, types.Count);
        Assert.Equal(2, types.Select(type => type.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(types, type => type.Namespace == "Assets.ScenePosRegister.Misc");
        Assert.Contains(types, type => type.Namespace == "Assets.ScenePosRegister.Misc2");

        var fromStandby = Edges(index).Where(edge => edge.From.ToString() == "employee/SSAW0001").ToList();
        var reached = fromStandby.Where(edge => edge.To is not null).Select(edge => edge.To!.ToString()).Distinct().ToList();

        _output.WriteLine(string.Join(", ", reached));

        // Misc.KsPosa leads to SSAW0089; Misc2.KsPosa leads to SSAW0281 and is never constructed
        // from the standby screen, so that edge must not exist.
        Assert.Contains("employee/SSAW0089", reached);
        Assert.DoesNotContain("employee/SSAW0281", reached);
    }

    /// <summary>
    /// E-36: every fact a report shows can be opened in the source. An evidence id that points at
    /// nothing would be worse than none at all.
    /// </summary>
    [Fact]
    [Trait("Task", "T18")]
    public void Evidence_AllFactsHaveEvidence()
    {
        var index = FixtureIndex.WithGraph;

        foreach (var edge in Edges(index))
        {
            Assert.NotEmpty(edge.Chain);
            Assert.All(edge.Chain, step => Assert.NotNull(step.EvidenceId));
            Assert.All(edge.Chain, step => Assert.NotNull(index.Reader.Evidence(step.EvidenceId!)));
        }

        foreach (var record in Breaks(index).Values)
        {
            Assert.NotNull(record.EvidenceId);
            Assert.NotNull(index.Reader.Evidence(record.EvidenceId!));
        }

        // Controls and constraints carry evidence too (02 §7.3).
        foreach (var screen in index.Reader.Screens().Where(screen => screen.Registered))
        {
            foreach (var control in index.Reader.Controls(screen.Terminal, screen.ScreenId))
            {
                Assert.NotNull(control.EvidenceId);
                Assert.NotNull(index.Reader.Evidence(control.EvidenceId!));
            }
        }
    }

    /// <summary>
    /// E-37: the invariant of 02 §10.4. The builder enforces it, so this proves the enforcement is
    /// real rather than assumed.
    /// </summary>
    [Fact]
    [Trait("Task", "T18")]
    public void Invariant_BreakImpliesCandidate()
    {
        var edges = Edges(FixtureIndex.WithGraph);

        Assert.NotEmpty(edges);
        Assert.All(
            edges.Where(edge => edge.BreakIds.Count > 0),
            edge => Assert.Equal(Confidences.Candidate, edge.Confidence));

        // And a step inside a chain that passed a break is not certain either.
        foreach (var edge in edges.Where(edge => edge.Confidence == Confidences.Candidate))
            Assert.Contains(edge.Chain, step => step.Confidence == Confidences.Candidate || edge.BreakIds.Count > 0);

        // A dangling edge always names the break it ends at.
        Assert.All(edges.Where(edge => edge.To is null), edge => Assert.NotNull(edge.EndsAtBreak));
    }

    /// <summary>
    /// 02 §8.3: the explanations come from the template table and nowhere else, so every one of
    /// them can be rebuilt from its category, reason and fields.
    /// </summary>
    [Fact]
    [Trait("Task", "T18")]
    public void Why_UsesTemplatesOnly()
    {
        var breaks = Breaks(FixtureIndex.WithGraph).Values.ToList();
        Assert.NotEmpty(breaks);

        foreach (var record in breaks)
        {
            var rebuilt = WhyTemplates.Of(record.Category, record.Reason, new WhyTemplates.Fields
            {
                Key = record.ContractKey,
                Topic = record.TopicTemplate,
                Count = record.Count,
                Text = record.Text
            });

            Assert.Equal(rebuilt, record.Why);
        }

        // An unlisted combination has no sentence, and the builder refuses to invent one.
        Assert.False(WhyTemplates.Has(BreakCategories.Unknown, "NoSuchReason"));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => WhyTemplates.Of(BreakCategories.Unknown, "NoSuchReason", new WhyTemplates.Fields()));
    }

    // ------------------------------------------------------------------ reading

    private static IReadOnlyList<ScreenEdge> Edges(FixtureIndex index)
        => index.Reader.AllScreenEdges()
            .Select(row => JsonSerializer.Deserialize<ScreenEdge>(row.Json, IrJson.Options)!)
            .ToList();

    private sealed record BreakView(
        string Id,
        string Category,
        string Reason,
        string Why,
        string ResumeConfidence,
        int ResumeCount,
        string? ContractKey,
        string? TopicTemplate,
        string? EvidenceId,
        int? Count,
        string? Text);

    private static IReadOnlyDictionary<string, BreakView> Breaks(FixtureIndex index)
    {
        var views = new Dictionary<string, BreakView>(StringComparer.Ordinal);

        foreach (var row in index.Reader.Breaks())
        {
            var json = JsonNode.Parse(row.Json)!;
            var resume = json["resume"]!;
            var contract = json["contract"]!;
            var candidates = resume["candidates"]!.AsArray();

            // The counts the templates use: resume candidates for a dispatch, writers for a table.
            var count = candidates.Count;

            views[row.Id] = new BreakView(
                row.Id,
                row.Category,
                row.Reason,
                json["why"]!.GetValue<string>(),
                resume["confidence"]!.GetValue<string>(),
                candidates.Count,
                contract["key"]?.GetValue<string>(),
                contract["topicTemplate"]?.GetValue<string>(),
                json["at"]!["evidenceId"]?.GetValue<string>(),
                count,
                TextOf(json));
        }

        return views;
    }

    /// <summary>The text a template was given, recovered from the sentence it produced.</summary>
    private static string? TextOf(JsonNode json)
    {
        var why = json["why"]!.GetValue<string>();
        var start = why.IndexOf('（');
        var end = why.LastIndexOf('）');

        return start >= 0 && end > start ? why[(start + 1)..end] : null;
    }
}
