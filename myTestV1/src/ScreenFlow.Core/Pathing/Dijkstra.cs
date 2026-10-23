namespace ScreenFlow.Core.Pathing;

/// <summary>
/// Shortest paths on the collapsed screen graph (02 §11.5-3).
/// <para>
/// Ties are broken by the smallest edge id of the collapsed edge, and equal distances leave the
/// queue in node-key order, so the same index always yields the same path.
/// </para>
/// </summary>
public static class Dijkstra
{
    public sealed record Result(
        IReadOnlyDictionary<string, double> Dist,
        IReadOnlyDictionary<string, CollapsedEdge> Prev,
        IReadOnlyDictionary<string, int> Hops)
    {
        public bool Reached(string nodeKey) => Dist.ContainsKey(nodeKey);

        /// <summary>The edges from the source to <paramref name="nodeKey"/>, in order.</summary>
        public IReadOnlyList<CollapsedEdge> PathTo(string nodeKey)
        {
            var edges = new List<CollapsedEdge>();
            for (var at = nodeKey; Prev.TryGetValue(at, out var edge); at = edge.From.Key) edges.Add(edge);
            edges.Reverse();
            return edges;
        }
    }

    /// <param name="maxHops">
    /// Upper bound on the number of edges. A node whose cheapest route needs more hops than this is
    /// not expanded further: the limit states how far an answer may reach, it is not an optimisation.
    /// </param>
    public static Result Run(
        ScreenGraph graph,
        string source,
        int maxHops = int.MaxValue,
        Func<CollapsedEdge, bool>? edgeFilter = null,
        Func<string, bool>? nodeFilter = null)
    {
        var dist = new Dictionary<string, double>(StringComparer.Ordinal);
        var prev = new Dictionary<string, CollapsedEdge>(StringComparer.Ordinal);
        var hops = new Dictionary<string, int>(StringComparer.Ordinal);
        var settled = new HashSet<string>(StringComparer.Ordinal);

        if (nodeFilter is not null && !nodeFilter(source)) return new Result(dist, prev, hops);

        dist[source] = 0.0;
        hops[source] = 0;

        var queue = new PriorityQueue<string, (double Cost, string Key)>();
        queue.Enqueue(source, (0.0, source));

        while (queue.TryDequeue(out var node, out _))
        {
            if (!settled.Add(node)) continue;
            if (hops[node] >= maxHops) continue;

            foreach (var edge in graph.Out(node))
            {
                if (edgeFilter is not null && !edgeFilter(edge)) continue;

                var next = edge.To.Key;
                if (nodeFilter is not null && !nodeFilter(next)) continue;

                var candidate = dist[node] + edge.Cost;
                var known = dist.TryGetValue(next, out var current);

                var better = !known
                             || candidate < current
                             || (candidate == current
                                 && string.CompareOrdinal(edge.MinEdgeId, prev[next].MinEdgeId) < 0);

                if (!better) continue;

                dist[next] = candidate;
                prev[next] = edge;
                hops[next] = hops[node] + 1;
                queue.Enqueue(next, (candidate, next));
            }
        }

        return new Result(dist, prev, hops);
    }
}
