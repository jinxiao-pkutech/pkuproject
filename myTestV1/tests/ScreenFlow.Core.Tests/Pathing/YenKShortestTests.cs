using ScreenFlow.Core.Graph;
using ScreenFlow.Core.Pathing;
using Xunit;

namespace ScreenFlow.Core.Tests.Pathing;

/// <summary>
/// The path algorithms on synthetic graphs (02 §11.5-2, §11.5-3).
/// <para>
/// Hand-built graphs, not the fixture: what is under test here is the algorithm — K ordering,
/// tie-breaking, looplessness — and a fixture would only prove that the POS data happens to be easy.
/// </para>
/// </summary>
public sealed class YenKShortestTests
{
    [Fact]
    [Trait("Task", "T19")]
    public void Yen_DiamondGraph_ReturnsKInOrder()
    {
        var graph = ScreenGraph.FromEdges(new[]
        {
            Edge("se_01", "A", "B", 1.0),
            Edge("se_02", "A", "C", 2.0),
            Edge("se_03", "B", "D", 1.0),
            Edge("se_04", "C", "D", 1.0)
        });

        var paths = YenKShortest.Run(graph, "t/A", "t/D", k: 5);

        Assert.Equal(2, paths.Count);
        Assert.Equal(new[] { "t/A", "t/B", "t/D" }, paths[0].Nodes);
        Assert.Equal(2.0, paths[0].Cost);
        Assert.Equal(new[] { "t/A", "t/C", "t/D" }, paths[1].Nodes);
        Assert.Equal(3.0, paths[1].Cost);
    }

    [Fact]
    [Trait("Task", "T19")]
    public void Yen_TieBreakDeterministic()
    {
        var edges = new[]
        {
            Edge("se_bb", "A", "B", 1.0),
            Edge("se_aa", "A", "C", 1.0),
            Edge("se_dd", "B", "D", 1.0),
            Edge("se_cc", "C", "D", 1.0)
        };

        // Both routes cost 2.0. The order must come from the edge ids, not from the order the rows
        // happened to arrive in, or two builds of the same project would answer differently.
        var forward = YenKShortest.Run(ScreenGraph.FromEdges(edges), "t/A", "t/D", k: 5);
        var reversed = YenKShortest.Run(ScreenGraph.FromEdges(Enumerable.Reverse(edges)), "t/A", "t/D", k: 5);

        Assert.Equal(new[] { "t/A", "t/C", "t/D" }, forward[0].Nodes);
        Assert.Equal(forward.Select(path => path.Signature), reversed.Select(path => path.Signature));
    }

    [Fact]
    [Trait("Task", "T19")]
    public void Yen_LooplessWithCycles()
    {
        var graph = ScreenGraph.FromEdges(new[]
        {
            Edge("se_01", "A", "B", 1.0),
            Edge("se_02", "B", "C", 1.0),
            Edge("se_03", "C", "A", 1.0),
            Edge("se_04", "B", "D", 5.0),
            Edge("se_05", "C", "D", 1.0)
        });

        var paths = YenKShortest.Run(graph, "t/A", "t/D", k: 10);

        Assert.NotEmpty(paths);
        foreach (var path in paths)
        {
            Assert.Equal(path.Nodes.Count, path.Nodes.Distinct().Count());
        }

        Assert.Equal(new[] { "t/A", "t/B", "t/C", "t/D" }, paths[0].Nodes);
    }

    [Fact]
    [Trait("Task", "T19")]
    public void Yen_ParallelEdges_CollapseToCheapest()
    {
        var graph = ScreenGraph.FromEdges(new[]
        {
            Edge("se_expensive", "A", "B", 4.0),
            Edge("se_cheap", "A", "B", 1.5)
        });

        var collapsed = Assert.Single(graph.Out("t/A"));

        Assert.Equal(1.5, collapsed.Cost);
        Assert.Equal("se_cheap", collapsed.MinEdgeId);
        Assert.Equal(new[] { "se_cheap", "se_expensive" }, collapsed.Parallel.Select(edge => edge.EdgeId));
    }

    [Fact]
    [Trait("Task", "T19")]
    public void Yen_Unreachable_ReturnsEmpty()
    {
        var graph = ScreenGraph.FromEdges(new[]
        {
            Edge("se_01", "A", "B", 1.0),
            Edge("se_02", "C", "D", 1.0)
        });

        Assert.Empty(YenKShortest.Run(graph, "t/A", "t/D", k: 5));
    }

    [Fact]
    [Trait("Task", "T19")]
    public void Yen_MaxHops_StopsSearch()
    {
        var graph = ScreenGraph.FromEdges(new[]
        {
            Edge("se_01", "A", "B", 1.0),
            Edge("se_02", "B", "C", 1.0),
            Edge("se_03", "C", "D", 1.0)
        });

        Assert.Empty(YenKShortest.Run(graph, "t/A", "t/D", k: 5, maxHops: 2));
        Assert.Single(YenKShortest.Run(graph, "t/A", "t/D", k: 5, maxHops: 3));
    }

    [Fact]
    [Trait("Task", "T19")]
    public void Partial_DanglingEdge_RankedByCost()
    {
        var graph = ScreenGraph.FromEdges(new[]
        {
            Edge("se_01", "A", "B", 1.0),
            Dangling("se_far", "B", 3.0, "b_far"),
            Dangling("se_near", "A", 3.5, "b_near")
        });

        var traces = PartialPathFinder.Run(graph, "t/A", k: 5, maxHops: 25);

        Assert.Equal(2, traces.Count);
        Assert.Equal("se_near", traces[0].Dangling.EdgeId);
        Assert.Equal(3.5, traces[0].Cost);
        Assert.Equal("se_far", traces[1].Dangling.EdgeId);
        Assert.Equal(4.0, traces[1].Cost);
    }

    private static ScreenEdge Edge(string id, string from, string to, double cost) => new()
    {
        EdgeId = id,
        From = Ref(from),
        To = Ref(to),
        Trigger = new EdgeTrigger { Kind = "click" },
        Confidence = Confidences.Certain,
        Cost = cost
    };

    private static ScreenEdge Dangling(string id, string from, double cost, string breakId) => new()
    {
        EdgeId = id,
        From = Ref(from),
        To = null,
        Trigger = new EdgeTrigger { Kind = "click" },
        Confidence = Confidences.Candidate,
        EndsAtBreak = breakId,
        BreakIds = new[] { breakId },
        Cost = cost
    };

    private static ScreenRef Ref(string screenId) => new() { Terminal = "t", ScreenId = screenId };
}
