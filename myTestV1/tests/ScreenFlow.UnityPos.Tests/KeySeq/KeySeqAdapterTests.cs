using System.Text.Json.Nodes;
using ScreenFlow.Core.Ir;
using ScreenFlow.UnityPos.KeySeq;
using Xunit;
using Xunit.Abstractions;

namespace ScreenFlow.UnityPos.Tests.KeySeq;

/// <summary>
/// The key sequence adapter (02 §9.5; 03 §11). Covers E-12, E-13 and E-14.
/// </summary>
public sealed class KeySeqAdapterTests
{
    private const string EdyModel = "Assets.ScenePosRegister.Common.Model.Script.EdyCharge_Model";
    private const string KeySeqRequest = "TEC.SEJPOS.Model.Apl.KeySeq.KeySeqRequest";

    private readonly ITestOutputHelper _output;

    public KeySeqAdapterTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// E-12: the fixture registers three table rows across three tables, one of which is empty
    /// (03 §11.1). The commented-out row is not a row (05 §3.3).
    /// </summary>
    [Fact]
    [Trait("Task", "T16")]
    public void KeySeq_Transitions_Exact()
    {
        var index = DomainIndex.Editor;
        var transitions = Transitions(index);

        foreach (var transition in transitions)
            _output.WriteLine($"{transition.Stage} / {transition.PsCode} -> {transition.Handler} ({transition.NextStage})");

        Assert.Equal(3, transitions.Count);

        var chargeSelect = transitions.Single(t => t.Stage == "CSP_NML_EDY_CHARGE_SELECT");
        Assert.Equal("PS_CEDY_CHARGEOTHER", chargeSelect.PsCode);
        Assert.Equal("CSP_NML_EDY_CHARGE_INPUT", chargeSelect.NextStage);
        Assert.Equal(
            index.Symbol(SymbolKinds.Method, EdyModel + $".GaEntryChargeOther(ref {KeySeqRequest})").Id,
            chargeSelect.Handler);

        // The commented-out entry of that same table would have been a second row (05 §3.3).
        var selectRows = transitions.Where(t => t.Stage == "CSP_NML_EDY_CHARGE_SELECT").ToList();
        Assert.Single(selectRows);
        Assert.DoesNotContain(transitions, t => t.HandlerName?.Contains("GaEntryOld", StringComparison.Ordinal) ?? false);

        var payCheck = transitions.Where(t => t.Stage == "CSP_NML_EDY_PAY_CHECK").ToList();
        Assert.Equal(2, payCheck.Count);
        Assert.Equal(
            new[] { "PS_CEDY_CHARGEOTHER", "PS_CSTD" },
            payCheck.Select(t => t.PsCode).OrderBy(code => code, StringComparer.Ordinal));

        // The empty table registered no rows, and that is not an error (03 §11.1).
        Assert.DoesNotContain(transitions, t => t.Stage == "CSP_NML_EDY_SHORTTEND_BZ");

        // Every row names the handler it runs.
        Assert.All(transitions, t => Assert.NotNull(t.Handler));
        Assert.All(transitions, t => Assert.NotNull(t.EvidenceId));
    }

    /// <summary>
    /// E-13: one key code appears in two stages, so running it dispatches to either depending on
    /// the stage at run time (03 §11). Both rows are the answer; choosing one would be a guess.
    /// </summary>
    [Fact]
    [Trait("Task", "T16")]
    public void KeySeq_Dispatch_Candidates()
    {
        var index = DomainIndex.Editor;
        var dispatches = Dispatches(index);

        var chargeOther = dispatches.Single(dispatch => dispatch.PsCode == "PS_CEDY_CHARGEOTHER");
        Assert.Equal(2, chargeOther.Transitions.Count);
        Assert.True(chargeOther.DomainHandled);

        // The two rows it can reach are the two stages that bind that key code.
        var reachable = Transitions(index)
            .Where(transition => chargeOther.Transitions.Contains(transition.Id))
            .ToList();

        Assert.Equal(
            new[] { "CSP_NML_EDY_CHARGE_SELECT", "CSP_NML_EDY_PAY_CHECK" },
            reachable.Select(t => t.Stage).OrderBy(stage => stage, StringComparer.Ordinal));

        // A key code bound in one stage only dispatches to one row.
        var standard = dispatches.Single(dispatch => dispatch.PsCode == "PS_CSTD");
        Assert.Single(standard.Transitions);
    }

    /// <summary>
    /// E-14: the key code can arrive inside an event request the caller built elsewhere. It is then
    /// not statically known, and the dispatch says so rather than matching every row.
    /// </summary>
    [Fact]
    [Trait("Task", "T16")]
    public void KeySeq_Dispatch_Unresolved()
    {
        var index = DomainIndex.Editor;
        var dispatches = Dispatches(index);

        var unresolved = dispatches.Single(dispatch => dispatch.PsCode is null);
        Assert.Empty(unresolved.Transitions);

        // It is still recorded as a dispatch: the call happens, only its target is unknown.
        Assert.True(unresolved.DomainHandled);

        var caller = index.Reader.Symbol(unresolved.FromMethodId!);
        Assert.NotNull(caller);
        Assert.Equal("OnClickBtnOther", caller!.Name);

        Assert.Contains(
            index.Reader.Diagnostics(),
            diagnostic => diagnostic.Code == KeySeqAdapter.UnresolvedPsCodeCode);

        // Three execution points in the fixture: two resolved, one not.
        Assert.Equal(3, dispatches.Count);
        Assert.Equal(2, dispatches.Count(dispatch => dispatch.PsCode is not null));
    }

    private sealed record TransitionRow(
        string Id,
        string Stage,
        string PsCode,
        string? Handler,
        string? HandlerName,
        string? NextStage,
        string? EvidenceId);

    private static IReadOnlyList<TransitionRow> Transitions(DomainIndex index)
    {
        using var command = index.Db.Command(
            """
            SELECT t.id, t.stage, t.ps_code, t.handler_method_id,
                   (SELECT fq_name FROM symbols WHERE id = t.handler_method_id), t.next_stage, t.evidence_id
            FROM keyseq_transitions t ORDER BY t.stage, t.ps_code;
            """);

        var rows = new List<TransitionRow>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            rows.Add(new TransitionRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return rows;
    }

    private sealed record DispatchEdge(
        string? FromMethodId,
        string? PsCode,
        IReadOnlyList<string> Transitions,
        bool DomainHandled);

    private static IReadOnlyList<DispatchEdge> Dispatches(DomainIndex index)
        => index.Reader.DomainEdges(KeySeqAdapter.EdgeKind)
            .Select(edge =>
            {
                var json = JsonNode.Parse(edge.Json)!;

                return new DispatchEdge(
                    edge.FromMethodId,
                    json["psCode"]?.GetValue<string>(),
                    json["transitions"]!.AsArray().Select(node => node!.GetValue<string>()).ToList(),
                    json["domainHandled"]!.GetValue<bool>());
            })
            .ToList();
}
