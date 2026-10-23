using System.Text.Json.Nodes;
using ScreenFlow.Core.Storage;

namespace ScreenFlow.Core.Query;

/// <summary>
/// The <c>break list/get</c> commands (02 §11.2).
/// <para>
/// A break is tied to a screen or an edge through the screen edges it terminates
/// (<c>ends_at_break</c>) or the break ids an edge carries, so filtering by screen or edge works
/// backwards from the edges rather than denormalising the breaks table.
/// </para>
/// </summary>
public sealed class BreakQuery
{
    private readonly IndexReader _reader;

    public BreakQuery(IndexReader reader) => _reader = reader;

    /// <summary>Full break records, as stored (02 §8.3).</summary>
    public IReadOnlyList<JsonNode> List(string? category, string? screen, string? edge, int limit)
    {
        var breaks = _reader.Breaks(category).Select(row => Parse(row.Json)).ToList();

        if (screen is not null)
        {
            var ids = BreakIdsForScreen(screen);
            breaks = breaks.Where(node => Id(node) is { } id && ids.Contains(id)).ToList();
        }

        if (edge is not null)
        {
            var ids = BreakIdsForEdge(edge);
            breaks = breaks.Where(node => Id(node) is { } id && ids.Contains(id)).ToList();
        }

        return breaks.Take(limit).ToList();
    }

    /// <summary>One full break record, or null when the id does not exist.</summary>
    public JsonNode? Get(string id)
        => _reader.Breaks().FirstOrDefault(row => string.Equals(row.Id, id, StringComparison.Ordinal))
            is { } row
            ? Parse(row.Json)
            : null;

    private HashSet<string> BreakIdsForScreen(string screenId)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var edge in _reader.AllScreenEdges())
        {
            if (!string.Equals(edge.FromScreen, screenId, StringComparison.Ordinal)
                && !string.Equals(edge.ToScreen, screenId, StringComparison.Ordinal))
            {
                continue;
            }

            AddEdgeBreaks(edge, ids);
        }

        return ids;
    }

    private HashSet<string> BreakIdsForEdge(string edgeId)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var edge in _reader.AllScreenEdges())
        {
            if (string.Equals(edge.Id, edgeId, StringComparison.Ordinal))
                AddEdgeBreaks(edge, ids);
        }

        return ids;
    }

    private static void AddEdgeBreaks(ScreenEdgeRow edge, HashSet<string> ids)
    {
        if (edge.EndsAtBreak is { Length: > 0 } ends) ids.Add(ends);

        var node = JsonNode.Parse(edge.Json);
        if (node?["breakIds"] is JsonArray array)
        {
            foreach (var item in array)
                if (item?.GetValue<string>() is { Length: > 0 } id) ids.Add(id);
        }
    }

    private static JsonNode Parse(string json) => JsonNode.Parse(json) ?? new JsonObject();

    private static string? Id(JsonNode node) => node["breakId"]?.GetValue<string>();
}
