using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ScreenFlow.Core.Graph;
using ScreenFlow.Core.Ir;
using ScreenFlow.Core.Storage;

namespace ScreenFlow.Core.Query;

/// <summary><c>debug stats</c> (02 §11.2).</summary>
public sealed record DebugStats
{
    [JsonPropertyName("counts")] public required IReadOnlyDictionary<string, long> Counts { get; init; }
    [JsonPropertyName("domainEdgeKinds")] public required IReadOnlyDictionary<string, long> DomainEdgeKinds { get; init; }
    [JsonPropertyName("breakCategories")] public required IReadOnlyDictionary<string, long> BreakCategories { get; init; }
    [JsonPropertyName("diagnosticsByCode")] public required IReadOnlyDictionary<string, long> DiagnosticsByCode { get; init; }
}

/// <summary><c>debug callsites</c> (02 §11.2).</summary>
public sealed record DebugSites
{
    [JsonPropertyName("callsites")] public IReadOnlyList<CallsiteRec> Callsites { get; init; } = Array.Empty<CallsiteRec>();
    [JsonPropertyName("assigns")] public IReadOnlyList<AssignRec> Assigns { get; init; } = Array.Empty<AssignRec>();
    [JsonPropertyName("creations")] public IReadOnlyList<CreationRec> Creations { get; init; } = Array.Empty<CreationRec>();
}

/// <summary>One <c>messageSend</c> domain edge, with its sender resolved (02 §11.2).</summary>
public sealed record SendView
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("from")] public SymbolSummary? From { get; init; }

    /// <summary>The containing type of the sending method, e.g. <c>Payment_SSAW0089_Presenter</c>.</summary>
    [JsonPropertyName("type")] public string? Type { get; init; }

    /// <summary>The sending method's simple name.</summary>
    [JsonPropertyName("method")] public string? Method { get; init; }

    [JsonPropertyName("file")] public string? File { get; init; }
    [JsonPropertyName("line")] public int? Line { get; init; }
    [JsonPropertyName("channel")] public string? Channel { get; init; }
    [JsonPropertyName("keys")] public IReadOnlyList<string> Keys { get; init; } = Array.Empty<string>();
    [JsonPropertyName("eventId")] public string? EventId { get; init; }
    [JsonPropertyName("displayTargets")] public IReadOnlyList<string> DisplayTargets { get; init; } = Array.Empty<string>();
}

/// <summary>
/// The <c>debug stats/callsites/sends</c> commands (02 §11.2). These are read-only diagnostics for
/// acceptance: they count and list what the index recorded, without re-deriving any of it.
/// </summary>
public sealed class DebugQuery
{
    private readonly IndexReader _reader;

    public DebugQuery(IndexReader reader) => _reader = reader;

    public DebugStats Stats()
        => new()
        {
            Counts = _reader.Db.AllCounts(),
            DomainEdgeKinds = GroupCount("domain_edges", "kind"),
            BreakCategories = GroupCount("breaks", "category"),
            DiagnosticsByCode = GroupCount("diagnostics", "code")
        };

    public DebugSites Sites(string file, int fromLine, int toLine)
        => new()
        {
            Callsites = _reader.CallsitesInRange(file, fromLine, toLine),
            Assigns = _reader.AssignsInRange(file, fromLine, toLine),
            Creations = CreationsInRange(file, fromLine, toLine)
        };

    public IReadOnlyList<SendView> Sends(string? keyContains, string? targetScreen)
        => _reader.DomainEdges(DomainEdgeKinds.MessageSend)
            .Select(edge =>
            {
                var json = JsonNode.Parse(edge.Json);
                var method = edge.FromMethodId is { } methodId ? _reader.Symbol(methodId) : null;
                var type = method?.ContainerId is { } containerId ? _reader.Symbol(containerId)?.Name : null;

                return new SendView
                {
                    Id = edge.Id,
                    From = SymbolQuery.Summary(method),
                    Type = type,
                    Method = method?.Name,
                    File = method?.File,
                    Line = method?.Span?.StartLine,
                    Channel = StringValue(json, "channel"),
                    Keys = StringArray(json, "keys"),
                    EventId = StringValue(json, "eventId"),
                    DisplayTargets = StringArray(json, "displayTargets")
                };
            })
            .Where(send =>
                (keyContains is null || send.Keys.Any(key => key.Contains(keyContains, StringComparison.Ordinal)))
                && (targetScreen is null || send.DisplayTargets.Contains(targetScreen, StringComparer.Ordinal)))
            .ToList();

    private IReadOnlyList<CreationRec> CreationsInRange(string file, int fromLine, int toLine)
    {
        // Creations are resolved to a file through their method, so a range query joins the symbol
        // table rather than reading a stored file column.
        using var command = _reader.Db.Command("""
            SELECT c.json FROM creations c
            JOIN symbols s ON s.id = c.method_id
            WHERE s.file = $f AND s.sl >= $from AND s.sl <= $to
            ORDER BY s.sl, c.id;
            """);
        command.Parameters.AddWithValue("$f", file);
        command.Parameters.AddWithValue("$from", fromLine);
        command.Parameters.AddWithValue("$to", toLine);

        using var rows = command.ExecuteReader();
        var result = new List<CreationRec>();
        while (rows.Read()) result.Add((CreationRec)IrJson.ReadLine(rows.GetString(0)));
        return result;
    }

    private IReadOnlyDictionary<string, long> GroupCount(string table, string column)
    {
        var counts = new SortedDictionary<string, long>(StringComparer.Ordinal);

        using var command = _reader.Db.Command($"SELECT {column}, COUNT(*) FROM {table} GROUP BY {column} ORDER BY {column};");
        using var rows = command.ExecuteReader();

        while (rows.Read())
            counts[rows.GetString(0)] = rows.GetInt64(1);

        return counts;
    }

    private static string? StringValue(JsonNode? node, string property)
        => node?[property]?.GetValue<string>();

    private static IReadOnlyList<string> StringArray(JsonNode? node, string property)
    {
        if (node?[property] is not JsonArray array) return Array.Empty<string>();

        return array
            .Select(item => item?.GetValue<string>())
            .Where(value => value is not null)
            .Cast<string>()
            .ToList();
    }
}
