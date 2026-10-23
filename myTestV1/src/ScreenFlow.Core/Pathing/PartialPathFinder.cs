using ScreenFlow.Core.Graph;

namespace ScreenFlow.Core.Pathing;

/// <summary>A route that stops at a break instead of at the target screen (02 §11.5-5).</summary>
public sealed record PartialTrace
{
    public required string Source { get; init; }

    /// <summary>The complete edges taken before the chain stopped being followable.</summary>
    public required IReadOnlyList<CollapsedEdge> Edges { get; init; }

    /// <summary>The dangling edge the route ends on.</summary>
    public required ScreenEdge Dangling { get; init; }

    public double Cost => Edges.Sum(edge => edge.Cost) + Dangling.Cost;
}

/// <summary>
/// What to answer when no complete path exists (02 §11.5-5).
/// <para>
/// A dangling edge is the honest end of a search: it names the screen the analysis got to and the
/// break that stopped it, which is what the caller has to act on.
/// </para>
/// </summary>
public static class PartialPathFinder
{
    public static IReadOnlyList<PartialTrace> Run(ScreenGraph graph, string source, int k, int maxHops)
    {
        if (k <= 0) return Array.Empty<PartialTrace>();

        var reach = Dijkstra.Run(graph, source, maxHops);
        var traces = new List<PartialTrace>();

        foreach (var node in reach.Dist.Keys.OrderBy(key => key, StringComparer.Ordinal))
        {
            var edges = reach.PathTo(node);
            if (edges.Count > maxHops) continue;

            foreach (var dangling in graph.Dangling(node))
            {
                traces.Add(new PartialTrace { Source = source, Edges = edges, Dangling = dangling });
            }
        }

        traces.Sort((left, right) =>
        {
            var byCost = left.Cost.CompareTo(right.Cost);
            return byCost != 0 ? byCost : string.CompareOrdinal(left.Dangling.EdgeId, right.Dangling.EdgeId);
        });

        return traces.Count <= k ? traces : traces.Take(k).ToList();
    }
}
