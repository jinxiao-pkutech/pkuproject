using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ScreenFlow.Core.Config;
using ScreenFlow.Core.Graph;
using ScreenFlow.Core.Ir;
using ScreenFlow.Core.Storage;

namespace ScreenFlow.Core.Pathing;

/// <summary>What <c>path find</c> was asked for (02 §11.2).</summary>
public sealed record PathOptions
{
    public string? TerminalFrom { get; init; }
    public string? TerminalTo { get; init; }
    public int K { get; init; } = 5;
    public int MaxHops { get; init; } = 25;
    public bool IncludePrefix { get; init; }

    /// <summary><c>summary</c> leaves the chains out, <c>full</c> keeps them (02 §11.5-8).</summary>
    public string Detail { get; init; } = PathDetail.Summary;

    public string? Locale { get; init; }

    /// <summary>Parallel edges kept per hop (02 §11.5-4).</summary>
    public int MaxEdgesPerHop { get; init; } = 10;
}

public static class PathDetail
{
    public const string Summary = "summary";
    public const string Full = "full";
}

public sealed record ScreenEndpoint
{
    [JsonPropertyName("terminal")] public required string Terminal { get; init; }
    [JsonPropertyName("screenId")] public required string ScreenId { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
}

/// <summary>The words on the control that starts the hop (02 §11.4).</summary>
public sealed record TriggerText
{
    [JsonPropertyName("locale")] public required string Locale { get; init; }
    [JsonPropertyName("plain")] public string? Plain { get; init; }
    [JsonPropertyName("raw")] public string? Raw { get; init; }
    [JsonPropertyName("key")] public string? Key { get; init; }
    [JsonPropertyName("confidence")] public required string Confidence { get; init; }
}

public sealed record PathEdgeView
{
    [JsonPropertyName("edgeId")] public required string EdgeId { get; init; }
    [JsonPropertyName("trigger")] public required EdgeTrigger Trigger { get; init; }
    [JsonPropertyName("triggerText")] public TriggerText? TriggerText { get; init; }
    [JsonPropertyName("confidence")] public required string Confidence { get; init; }
    [JsonPropertyName("conditions")] public IReadOnlyList<string> Conditions { get; init; } = Array.Empty<string>();
    [JsonPropertyName("breakIds")] public IReadOnlyList<string> BreakIds { get; init; } = Array.Empty<string>();

    /// <summary>Null with <c>--detail summary</c> (02 §11.5-8).</summary>
    [JsonPropertyName("chain")] public IReadOnlyList<ChainStep>? Chain { get; init; }

    [JsonPropertyName("endsAtBreak")] public string? EndsAtBreak { get; init; }
}

public sealed record PathHop
{
    [JsonPropertyName("order")] public required int Order { get; init; }
    [JsonPropertyName("from")] public required ScreenRef From { get; init; }

    /// <summary>Null on the last hop of a partial path: it ends at a break (02 §11.5-5).</summary>
    [JsonPropertyName("to")] public ScreenRef? To { get; init; }

    [JsonPropertyName("edges")] public required IReadOnlyList<PathEdgeView> Edges { get; init; }
    [JsonPropertyName("edgesTruncated")] public bool EdgesTruncated { get; init; }
}

public sealed record ScreenPath
{
    [JsonPropertyName("pathId")] public required string PathId { get; init; }
    [JsonPropertyName("rank")] public required int Rank { get; init; }
    [JsonPropertyName("cost")] public required double Cost { get; init; }
    [JsonPropertyName("reachedTarget")] public required bool ReachedTarget { get; init; }
    [JsonPropertyName("overallConfidence")] public required string OverallConfidence { get; init; }
    [JsonPropertyName("hops")] public required IReadOnlyList<PathHop> Hops { get; init; }

    /// <summary>Every break the path passes, de-duplicated and sorted by id (02 §11.4).</summary>
    [JsonPropertyName("breaks")] public IReadOnlyList<JsonNode> Breaks { get; init; } = Array.Empty<JsonNode>();
}

public sealed record PathDiagnosis
{
    [JsonPropertyName("targetHasInboundEdges")] public required bool TargetHasInboundEdges { get; init; }
    [JsonPropertyName("fromHasOutboundEdges")] public required bool FromHasOutboundEdges { get; init; }
    [JsonPropertyName("note")] public string? Note { get; init; }
}

/// <summary>The <c>path find</c> data of 02 §11.4.</summary>
public sealed record PathFindResult
{
    /// <summary>
    /// The resolved start screens. A list because 02 §11.5-1 computes every terminal the id exists
    /// on when the caller did not name one, and the answer has to say which terminal it means.
    /// </summary>
    [JsonPropertyName("from")] public required IReadOnlyList<ScreenEndpoint> From { get; init; }

    [JsonPropertyName("to")] public required IReadOnlyList<ScreenEndpoint> To { get; init; }
    [JsonPropertyName("prefixPath")] public ScreenPath? PrefixPath { get; init; }
    [JsonPropertyName("paths")] public IReadOnlyList<ScreenPath> Paths { get; init; } = Array.Empty<ScreenPath>();
    [JsonPropertyName("partialPaths")] public IReadOnlyList<ScreenPath> PartialPaths { get; init; } = Array.Empty<ScreenPath>();
    [JsonPropertyName("diagnosis")] public required PathDiagnosis Diagnosis { get; init; }
    [JsonPropertyName("truncated")] public bool Truncated { get; init; }

    /// <summary>Controls whose text could not be resolved, as <c>terminal/screen/control</c>.</summary>
    [JsonPropertyName("unresolved")] public IReadOnlyList<string> Unresolved { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Path search over the screen graph (02 §11.4, §11.5).
/// <para>
/// The service only reads the index: every cost, confidence and break on a path is the one the
/// graph stage already recorded, so a path never claims more than the edges it is made of.
/// </para>
/// </summary>
public sealed class PathService
{
    private readonly IndexReader _reader;
    private readonly DomainConfig _domain;
    private readonly ScreenGraph _graph;
    private readonly string _defaultLocale;

    public PathService(IndexReader reader, DomainConfig domain)
        : this(reader, domain, ScreenGraph.FromIndex(reader))
    {
    }

    public PathService(IndexReader reader, DomainConfig domain, ScreenGraph graph)
    {
        _reader = reader;
        _domain = domain;
        _graph = graph;
        _defaultLocale = StringOrNull(domain.Resources, "defaultLocale") ?? "ja";
    }

    public ScreenGraph Graph => _graph;

    private Dictionary<string, string>? _breaksById;

    /// <summary>The break records by id, read once: a path of ten hops would otherwise re-read the table ten times.</summary>
    private Dictionary<string, string> BreaksById
        => _breaksById ??= _reader.Breaks().ToDictionary(row => row.Id, row => row.Json, StringComparer.Ordinal);

    public PathFindResult Find(string fromScreenId, string toScreenId, PathOptions? options = null)
    {
        options ??= new PathOptions();

        var sources = Endpoints(fromScreenId, options.TerminalFrom);
        var targets = Endpoints(toScreenId, options.TerminalTo);
        var unresolved = new SortedSet<string>(StringComparer.Ordinal);

        var found = new List<GraphPath>();
        foreach (var source in sources)
        {
            foreach (var target in targets)
            {
                found.AddRange(YenKShortest.Run(_graph, Key(source), Key(target), options.K, options.MaxHops));
            }
        }

        found.Sort(YenKShortest.Compare);
        var truncated = found.Count > options.K;
        var paths = new List<ScreenPath>();
        var rank = 1;

        foreach (var path in found.Take(options.K))
        {
            paths.Add(Build(path, rank++, options, unresolved));
        }

        var partials = new List<ScreenPath>();
        if (paths.Count < options.K)
        {
            // 02 §11.5-5: when the graph cannot get there, say how far it does get and what stopped it.
            var traces = new List<PartialTrace>();
            foreach (var source in sources)
            {
                traces.AddRange(PartialPathFinder.Run(_graph, Key(source), options.K - paths.Count, options.MaxHops));
            }

            traces.Sort((left, right) =>
            {
                var byCost = left.Cost.CompareTo(right.Cost);
                return byCost != 0 ? byCost : string.CompareOrdinal(left.Dangling.EdgeId, right.Dangling.EdgeId);
            });

            var partialRank = 1;
            foreach (var trace in traces.Take(options.K - paths.Count))
            {
                partials.Add(Build(trace, partialRank++, options, unresolved));
            }
        }

        ScreenPath? prefix = null;
        if (options.IncludePrefix)
        {
            var prefixResult = Prefix(fromScreenId, options with { IncludePrefix = false, K = 1, TerminalTo = options.TerminalFrom });
            prefix = prefixResult.Paths.FirstOrDefault();
        }

        return new PathFindResult
        {
            From = sources,
            To = targets,
            PrefixPath = prefix,
            Paths = paths,
            PartialPaths = partials,
            Diagnosis = Diagnose(sources, targets, paths.Count > 0),
            Truncated = truncated,
            Unresolved = unresolved.ToList()
        };
    }

    /// <summary>The route from a standby screen to <paramref name="toScreenId"/> (02 §11.5-7).</summary>
    public PathFindResult Prefix(string toScreenId, PathOptions? options = null)
    {
        options ??= new PathOptions();
        var standby = StandbyScreens();
        var targets = Endpoints(toScreenId, options.TerminalTo ?? options.TerminalFrom);
        var unresolved = new SortedSet<string>(StringComparer.Ordinal);

        var found = new List<GraphPath>();
        foreach (var start in standby)
        {
            foreach (var target in targets)
            {
                found.AddRange(YenKShortest.Run(_graph, Key(start), Key(target), Math.Max(options.K, 1), options.MaxHops));
            }
        }

        found.Sort(YenKShortest.Compare);
        var paths = new List<ScreenPath>();
        var rank = 1;
        foreach (var path in found.Take(Math.Max(options.K, 1)))
        {
            paths.Add(Build(path, rank++, options, unresolved));
        }

        return new PathFindResult
        {
            From = standby,
            To = targets,
            Paths = paths,
            Diagnosis = Diagnose(standby, targets, paths.Count > 0),
            Unresolved = unresolved.ToList()
        };
    }

    // ---------------------------------------------------------------- building

    private ScreenPath Build(GraphPath path, int rank, PathOptions options, SortedSet<string> unresolved)
    {
        var hops = new List<PathHop>();
        var order = 1;

        foreach (var edge in path.Edges)
        {
            hops.Add(Hop(order++, edge.From.AsRef(), edge.To.AsRef(), edge.Parallel, options, unresolved));
        }

        return Finish(path.Source, hops, path.Cost, reachedTarget: true, rank);
    }

    private ScreenPath Build(PartialTrace trace, int rank, PathOptions options, SortedSet<string> unresolved)
    {
        var hops = new List<PathHop>();
        var order = 1;

        foreach (var edge in trace.Edges)
        {
            hops.Add(Hop(order++, edge.From.AsRef(), edge.To.AsRef(), edge.Parallel, options, unresolved));
        }

        hops.Add(Hop(order, trace.Dangling.From, null, new[] { trace.Dangling }, options, unresolved));

        return Finish(trace.Source, hops, trace.Cost, reachedTarget: false, rank);
    }

    private ScreenPath Finish(string source, IReadOnlyList<PathHop> hops, double cost, bool reachedTarget, int rank)
    {
        var breakIds = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var hop in hops)
        {
            foreach (var edge in hop.Edges)
            {
                foreach (var id in edge.BreakIds) breakIds.Add(id);
                if (edge.EndsAtBreak is { } ends) breakIds.Add(ends);
            }
        }

        var breaks = new List<JsonNode>();
        foreach (var id in breakIds)
        {
            if (!BreaksById.TryGetValue(id, out var json)) continue;

            var node = JsonNode.Parse(json);
            if (node is not null) breaks.Add(node);
        }

        // A hop is certain when at least one of its ways across is certain; the path is certain only
        // when every hop is. No edge is ever re-labelled: each keeps the confidence it was stored with.
        var certain = reachedTarget
                      && hops.Count > 0
                      && hops.All(hop => hop.Edges.Any(edge =>
                          string.Equals(edge.Confidence, Confidences.Certain, StringComparison.Ordinal)
                          && edge.BreakIds.Count == 0));

        var pathId = Ids.DerivedId("p_", new
        {
            source,
            reachedTarget,
            edges = hops.SelectMany(hop => hop.Edges.Select(edge => edge.EdgeId)).ToList()
        });

        return new ScreenPath
        {
            PathId = pathId,
            Rank = rank,
            Cost = cost,
            ReachedTarget = reachedTarget,
            OverallConfidence = certain ? Confidences.Certain : Confidences.Candidate,
            Hops = hops,
            Breaks = breaks
        };
    }

    private PathHop Hop(
        int order,
        ScreenRef from,
        ScreenRef? to,
        IReadOnlyList<ScreenEdge> parallel,
        PathOptions options,
        SortedSet<string> unresolved)
    {
        var kept = parallel.Take(options.MaxEdgesPerHop).ToList();

        return new PathHop
        {
            Order = order,
            From = from,
            To = to,
            Edges = kept.Select(edge => View(edge, options, unresolved)).ToList(),
            EdgesTruncated = parallel.Count > kept.Count
        };
    }

    private PathEdgeView View(ScreenEdge edge, PathOptions options, SortedSet<string> unresolved)
        => new()
        {
            EdgeId = edge.EdgeId,
            Trigger = edge.Trigger,
            TriggerText = Text(edge, options.Locale ?? _defaultLocale, unresolved),
            Confidence = edge.Confidence,
            Conditions = edge.Conditions,
            BreakIds = edge.BreakIds,
            EndsAtBreak = edge.EndsAtBreak,
            Chain = string.Equals(options.Detail, PathDetail.Full, StringComparison.Ordinal) ? edge.Chain : null
        };

    private TriggerText? Text(ScreenEdge edge, string locale, SortedSet<string> unresolved)
    {
        if (edge.Trigger.ControlId is not { Length: > 0 } controlId) return null;

        var where = edge.From.Terminal + "/" + edge.From.ScreenId + "/" + controlId;
        var control = _reader.Controls(edge.From.Terminal, edge.From.ScreenId)
            .FirstOrDefault(row => string.Equals(row.ControlId, controlId, StringComparison.Ordinal));

        if (control?.TextKey is not { Length: > 0 } key)
        {
            unresolved.Add(where);
            return null;
        }

        foreach (var collection in SearchOrder(edge.From.Terminal))
        {
            var text = _reader.Text(collection, key, locale);
            if (text is null) continue;

            return new TriggerText
            {
                Locale = locale,
                Plain = text.TextPlain,
                Raw = text.TextRaw,
                Key = key,
                Confidence = control.TextKeyConfidence ?? Confidences.Certain
            };
        }

        unresolved.Add(where + " (" + key + "@" + locale + ")");
        return null;
    }

    // ---------------------------------------------------------------- lookups

    private PathDiagnosis Diagnose(
        IReadOnlyList<ScreenEndpoint> sources,
        IReadOnlyList<ScreenEndpoint> targets,
        bool anyPath)
    {
        var inbound = targets.Any(target => _graph.In(Key(target)).Count > 0);
        var outbound = sources.Any(source =>
            _graph.Out(Key(source)).Count > 0 || _graph.Dangling(Key(source)).Count > 0);

        string? note = null;
        if (!anyPath && !inbound)
        {
            var references = targets.Sum(target => ReferencesTo(target.ScreenId));
            var ids = string.Join("、", targets.Select(target => target.Terminal + "/" + target.ScreenId));
            note = $"终点画面 {ids} 没有任何入边；源码中有 {references} 处引用该画面 ID"
                   + "（navigate 的 screenIds 或 messageSend 的 displayTargets），但所在链未能形成画面边。";
        }
        else if (!anyPath && !outbound)
        {
            var ids = string.Join("、", sources.Select(source => source.Terminal + "/" + source.ScreenId));
            note = $"起点画面 {ids} 没有任何出边。";
        }
        else if (!anyPath)
        {
            note = "起点与终点各自都有画面边，但两者之间没有已确定的路径。";
        }

        return new PathDiagnosis
        {
            TargetHasInboundEdges = inbound,
            FromHasOutboundEdges = outbound,
            Note = note
        };
    }

    /// <summary>Callsites that name the screen but whose chain never became an edge (02 §11.5-6).</summary>
    private int ReferencesTo(string screenId)
    {
        var count = 0;

        foreach (var row in _reader.DomainEdges(DomainEdgeKinds.Navigate))
        {
            if (Contains(row.Json, "screenIds", screenId)) count++;
        }

        foreach (var row in _reader.DomainEdges(DomainEdgeKinds.MessageSend))
        {
            if (Contains(row.Json, "displayTargets", screenId)) count++;
        }

        return count;
    }

    private static bool Contains(string json, string property, string value)
    {
        var node = JsonNode.Parse(json);
        if (node?[property] is not JsonArray array) return false;

        return array.Any(item => string.Equals(item?.GetValue<string>(), value, StringComparison.Ordinal));
    }

    private IReadOnlyList<ScreenEndpoint> Endpoints(string screenId, string? terminal)
    {
        var rows = _reader.Screens(screenId, terminal);
        var endpoints = rows
            .Select(row => new ScreenEndpoint { Terminal = row.Terminal, ScreenId = row.ScreenId, Name = row.Name })
            .ToList();

        // A screen that is not in the registry can still be an end of an edge; dropping it here
        // would turn "not registered" into "does not exist".
        foreach (var node in _graph.Nodes)
        {
            if (!string.Equals(node.ScreenId, screenId, StringComparison.Ordinal)) continue;
            if (terminal is not null && !string.Equals(node.Terminal, terminal, StringComparison.Ordinal)) continue;
            if (endpoints.Any(end => string.Equals(end.Terminal, node.Terminal, StringComparison.Ordinal))) continue;

            endpoints.Add(new ScreenEndpoint { Terminal = node.Terminal, ScreenId = node.ScreenId });
        }

        endpoints.Sort((left, right) => string.CompareOrdinal(left.Terminal, right.Terminal));
        return endpoints;
    }

    private IReadOnlyList<ScreenEndpoint> StandbyScreens()
    {
        var result = new List<ScreenEndpoint>();
        if (_domain.Screens.ValueKind != JsonValueKind.Object) return result;
        if (!_domain.Screens.TryGetProperty("standbyScreens", out var list)) return result;

        foreach (var item in list.EnumerateArray())
        {
            var terminal = item.TryGetProperty("terminal", out var t) ? t.GetString() : null;
            var screenId = item.TryGetProperty("screenId", out var s) ? s.GetString() : null;
            if (terminal is null || screenId is null) continue;

            var row = _reader.Screens(screenId, terminal).FirstOrDefault();
            result.Add(new ScreenEndpoint { Terminal = terminal, ScreenId = screenId, Name = row?.Name });
        }

        result.Sort((left, right) => string.CompareOrdinal(left.Terminal + left.ScreenId, right.Terminal + right.ScreenId));
        return result;
    }

    private IReadOnlyList<string> SearchOrder(string terminal)
    {
        if (_domain.Resources.ValueKind != JsonValueKind.Object) return Array.Empty<string>();
        if (!_domain.Resources.TryGetProperty("stringTableSearchOrder", out var orders)) return Array.Empty<string>();
        if (!orders.TryGetProperty(terminal, out var list)) return Array.Empty<string>();

        return list.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToList();
    }

    private static string Key(ScreenEndpoint endpoint) => endpoint.Terminal + "/" + endpoint.ScreenId;

    private static string? StringOrNull(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
            ? value.GetString()
            : null;
}
