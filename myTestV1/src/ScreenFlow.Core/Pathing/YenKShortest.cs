namespace ScreenFlow.Core.Pathing;

/// <summary>One route through the collapsed graph.</summary>
public sealed record GraphPath
{
    public required string Source { get; init; }
    public required IReadOnlyList<CollapsedEdge> Edges { get; init; }

    public double Cost => Edges.Sum(edge => edge.Cost);

    /// <summary>The nodes visited, source first.</summary>
    public IReadOnlyList<string> Nodes
        => Edges.Count == 0
            ? new[] { Source }
            : new[] { Source }.Concat(Edges.Select(edge => edge.To.Key)).ToList();

    /// <summary>Identity for de-duplication and for the deterministic tie-break of 02 §11.5-3.</summary>
    public string Signature => string.Join(">", Edges.Select(edge => edge.MinEdgeId));
}

/// <summary>
/// Yen's K shortest loopless paths (02 §11.5-3), loopless by <c>(terminal, screenId)</c>.
/// <para>
/// Everything that can tie is ordered: candidates by cost then by their edge-id sequence, so two
/// runs over the same index return the same K paths in the same order.
/// </para>
/// </summary>
public static class YenKShortest
{
    public static IReadOnlyList<GraphPath> Run(
        ScreenGraph graph,
        string source,
        string target,
        int k,
        int maxHops = int.MaxValue)
    {
        if (k <= 0) return Array.Empty<GraphPath>();

        if (string.Equals(source, target, StringComparison.Ordinal))
            return new[] { new GraphPath { Source = source, Edges = Array.Empty<CollapsedEdge>() } };

        var first = ShortestFrom(graph, source, target, maxHops, null, null);
        if (first is null) return Array.Empty<GraphPath>();

        var accepted = new List<GraphPath> { first };
        var candidates = new List<GraphPath>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { first.Signature };

        while (accepted.Count < k)
        {
            var previous = accepted[^1];

            for (var i = 0; i < previous.Edges.Count; i++)
            {
                var spurNode = previous.Edges[i].From.Key;
                var rootEdges = previous.Edges.Take(i).ToList();
                var rootSignature = string.Join(">", rootEdges.Select(edge => edge.MinEdgeId));

                // Edges already used out of this spur node by an accepted path with the same root
                // are removed, which is what makes the next path different rather than the same one.
                var bannedEdges = new HashSet<string>(StringComparer.Ordinal);
                foreach (var path in accepted)
                {
                    if (path.Edges.Count <= i) continue;

                    var prefix = string.Join(">", path.Edges.Take(i).Select(edge => edge.MinEdgeId));
                    if (string.Equals(prefix, rootSignature, StringComparison.Ordinal))
                        bannedEdges.Add(path.Edges[i].MinEdgeId);
                }

                var bannedNodes = new HashSet<string>(rootEdges.Select(edge => edge.From.Key), StringComparer.Ordinal);

                var spur = ShortestFrom(
                    graph, spurNode, target, maxHops - rootEdges.Count,
                    edge => !bannedEdges.Contains(edge.MinEdgeId),
                    node => !bannedNodes.Contains(node));

                if (spur is null) continue;

                var total = new GraphPath
                {
                    Source = source,
                    Edges = rootEdges.Concat(spur.Edges).ToList()
                };

                if (total.Edges.Count > maxHops) continue;
                if (seen.Add(total.Signature)) candidates.Add(total);
            }

            if (candidates.Count == 0) break;

            candidates.Sort(Compare);
            accepted.Add(candidates[0]);
            candidates.RemoveAt(0);
        }

        return accepted;
    }

    public static int Compare(GraphPath left, GraphPath right)
    {
        var byCost = left.Cost.CompareTo(right.Cost);
        if (byCost != 0) return byCost;

        var byLength = left.Edges.Count.CompareTo(right.Edges.Count);
        return byLength != 0 ? byLength : string.CompareOrdinal(left.Signature, right.Signature);
    }

    private static GraphPath? ShortestFrom(
        ScreenGraph graph,
        string source,
        string target,
        int maxHops,
        Func<CollapsedEdge, bool>? edgeFilter,
        Func<string, bool>? nodeFilter)
    {
        if (maxHops <= 0) return null;

        var result = Dijkstra.Run(graph, source, maxHops, edgeFilter, nodeFilter);
        if (!result.Reached(target)) return null;

        return new GraphPath { Source = source, Edges = result.PathTo(target) };
    }
}
