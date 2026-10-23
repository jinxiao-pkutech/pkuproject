using ScreenFlow.Core.Graph;
using ScreenFlow.Core.Ir;
using ScreenFlow.Core.Storage;

namespace ScreenFlow.Core.Pathing;

/// <summary>One node of the screen-level graph: a screen on a terminal (02 §11.5-2, §11.5-3).</summary>
public readonly record struct GraphNode(string Terminal, string ScreenId)
{
    public string Key => Terminal + "/" + ScreenId;

    public ScreenRef AsRef() => new() { Terminal = Terminal, ScreenId = ScreenId };

    public static GraphNode Of(ScreenRef reference) => new(reference.Terminal, reference.ScreenId);

    public override string ToString() => Key;
}

/// <summary>
/// Every screen edge between the same two screens, as one edge of the search graph (02 §11.5-2).
/// <para>
/// The search runs on the cheapest of them, but all of them are kept: a reader asking how to get
/// from one screen to another wants every way, not the one the algorithm happened to pick.
/// </para>
/// </summary>
public sealed record CollapsedEdge
{
    public required GraphNode From { get; init; }
    public required GraphNode To { get; init; }

    /// <summary>The parallel edges, cheapest first, then by edge id (02 §11.5-4).</summary>
    public required IReadOnlyList<ScreenEdge> Parallel { get; init; }

    public double Cost => Parallel[0].Cost;

    /// <summary>The Dijkstra tie-break of 02 §11.5-3.</summary>
    public string MinEdgeId => Parallel[0].EdgeId;
}

/// <summary>The screen graph the path search runs on, built from the <c>screen_edges</c> table.</summary>
public sealed class ScreenGraph
{
    private readonly Dictionary<string, List<CollapsedEdge>> _out;
    private readonly Dictionary<string, List<CollapsedEdge>> _in;
    private readonly Dictionary<string, List<ScreenEdge>> _dangling;
    private readonly Dictionary<string, GraphNode> _nodes;

    private ScreenGraph(
        Dictionary<string, List<CollapsedEdge>> outgoing,
        Dictionary<string, List<CollapsedEdge>> incoming,
        Dictionary<string, List<ScreenEdge>> dangling,
        Dictionary<string, GraphNode> nodes)
    {
        _out = outgoing;
        _in = incoming;
        _dangling = dangling;
        _nodes = nodes;
    }

    public IReadOnlyCollection<GraphNode> Nodes => _nodes.Values;

    public bool Contains(string nodeKey) => _nodes.ContainsKey(nodeKey);

    public IReadOnlyList<CollapsedEdge> Out(string nodeKey)
        => _out.TryGetValue(nodeKey, out var edges) ? edges : Array.Empty<CollapsedEdge>();

    public IReadOnlyList<CollapsedEdge> In(string nodeKey)
        => _in.TryGetValue(nodeKey, out var edges) ? edges : Array.Empty<CollapsedEdge>();

    /// <summary>Edges that end at a break instead of a screen; they never take part in the search (02 §11.5-2).</summary>
    public IReadOnlyList<ScreenEdge> Dangling(string nodeKey)
        => _dangling.TryGetValue(nodeKey, out var edges) ? edges : Array.Empty<ScreenEdge>();

    public static ScreenGraph FromIndex(IndexReader reader)
        => FromEdges(reader.AllScreenEdges().Select(row => Deserialize(row.Json)));

    public static ScreenGraph FromEdges(IEnumerable<ScreenEdge> edges)
    {
        var parallel = new Dictionary<string, List<ScreenEdge>>(StringComparer.Ordinal);
        var dangling = new Dictionary<string, List<ScreenEdge>>(StringComparer.Ordinal);
        var nodes = new Dictionary<string, GraphNode>(StringComparer.Ordinal);

        foreach (var edge in edges)
        {
            var from = GraphNode.Of(edge.From);
            nodes[from.Key] = from;

            if (edge.To is null)
            {
                Add(dangling, from.Key, edge);
                continue;
            }

            var to = GraphNode.Of(edge.To);
            nodes[to.Key] = to;
            Add(parallel, from.Key + ">" + to.Key, edge);
        }

        var outgoing = new Dictionary<string, List<CollapsedEdge>>(StringComparer.Ordinal);
        var incoming = new Dictionary<string, List<CollapsedEdge>>(StringComparer.Ordinal);

        foreach (var pair in parallel.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var sorted = Sort(pair.Value);
            var collapsed = new CollapsedEdge
            {
                From = GraphNode.Of(sorted[0].From),
                To = GraphNode.Of(sorted[0].To!),
                Parallel = sorted
            };

            Add(outgoing, collapsed.From.Key, collapsed);
            Add(incoming, collapsed.To.Key, collapsed);
        }

        foreach (var list in outgoing.Values) list.Sort(CompareCollapsed);
        foreach (var list in incoming.Values) list.Sort(CompareCollapsed);
        foreach (var key in dangling.Keys.ToList()) dangling[key] = Sort(dangling[key]);

        return new ScreenGraph(outgoing, incoming, dangling, nodes);
    }

    public static ScreenEdge Deserialize(string json)
        => System.Text.Json.JsonSerializer.Deserialize<ScreenEdge>(json, IrJson.Options)
           ?? throw new InvalidOperationException("a screen_edges row did not deserialize");

    private static List<ScreenEdge> Sort(List<ScreenEdge> edges)
    {
        edges.Sort((left, right) =>
        {
            var byCost = left.Cost.CompareTo(right.Cost);
            return byCost != 0 ? byCost : string.CompareOrdinal(left.EdgeId, right.EdgeId);
        });

        return edges;
    }

    private static int CompareCollapsed(CollapsedEdge left, CollapsedEdge right)
    {
        var byCost = left.Cost.CompareTo(right.Cost);
        return byCost != 0 ? byCost : string.CompareOrdinal(left.MinEdgeId, right.MinEdgeId);
    }

    private static void Add<T>(Dictionary<string, List<T>> map, string key, T value)
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = new List<T>();
        list.Add(value);
    }
}
