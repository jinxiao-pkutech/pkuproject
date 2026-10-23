using System.Text.Json.Nodes;
using ScreenFlow.Core.Ir;
using ScreenFlow.Core.Storage;

namespace ScreenFlow.Core.Breaks;

/// <summary>Where a break happens (02 §8.3).</summary>
public sealed record BreakSite
{
    public string? CallsiteId { get; init; }
    public string? MethodId { get; init; }
    public string? Namespace { get; init; }
    public string? Type { get; init; }
    public string? Method { get; init; }
    public string? File { get; init; }
    public int? Line { get; init; }
    public string? EvidenceId { get; init; }

    public JsonObject ToJson() => new()
    {
        ["callsiteId"] = CallsiteId,
        ["methodId"] = MethodId,
        ["namespace"] = Namespace,
        ["type"] = Type,
        ["method"] = Method,
        ["file"] = File,
        ["line"] = Line,
        ["evidenceId"] = EvidenceId
    };
}

/// <summary>What the chain was doing when it broke (02 §8.3).</summary>
public sealed record BreakContract
{
    public string Kind { get; init; } = "unknown";
    public string? Key { get; init; }
    public IReadOnlyList<string> RequestTypes { get; init; } = Array.Empty<string>();
    public string? ResponseType { get; init; }
    public string? TopicTemplate { get; init; }
    public JsonObject Extra { get; init; } = new();

    public JsonObject ToJson()
    {
        var json = new JsonObject
        {
            ["kind"] = Kind,
            ["key"] = Key,
            ["requestTypes"] = new JsonArray(RequestTypes.Select(type => (JsonNode)type!).ToArray()),
            ["responseType"] = ResponseType,
            ["topicTemplate"] = TopicTemplate,
            ["extra"] = Extra.DeepClone()
        };

        return json;
    }
}

/// <summary>Where the chain could be picked up again (02 §8.3).</summary>
public sealed record ResumeCandidate
{
    public required string MethodId { get; init; }
    public string? Type { get; init; }
    public string? Method { get; init; }
    public string? File { get; init; }
    public int? Line { get; init; }
    public string? Condition { get; init; }

    public JsonObject ToJson() => new()
    {
        ["methodId"] = MethodId,
        ["type"] = Type,
        ["method"] = Method,
        ["file"] = File,
        ["line"] = Line,
        ["condition"] = Condition
    };
}

/// <summary>One break, ready to be written (02 §8.3).</summary>
public sealed record BreakRecord
{
    public required string Id { get; init; }
    public required string Category { get; init; }
    public required string Reason { get; init; }
    public required BreakSite At { get; init; }
    public required BreakContract Contract { get; init; }
    public required string Why { get; init; }
    public IReadOnlyList<ResumeCandidate> Resume { get; init; } = Array.Empty<ResumeCandidate>();

    public string ResumeConfidence => Resume.Count switch
    {
        0 => ResumeConfidences.None,
        1 => ResumeConfidences.Single,
        _ => ResumeConfidences.Multiple
    };

    public BreakRow ToRow() => new()
    {
        Id = Id,
        Category = Category,
        Reason = Reason,
        AtCallsiteId = At.CallsiteId,
        AtMethodId = At.MethodId,
        ContractKey = Contract.Key,
        ResumeConfidence = ResumeConfidence,
        Json = new JsonObject
        {
            ["breakId"] = Id,
            ["category"] = Category,
            ["reason"] = Reason,
            ["at"] = At.ToJson(),
            ["contract"] = Contract.ToJson(),
            ["why"] = Why,
            ["resume"] = new JsonObject
            {
                ["confidence"] = ResumeConfidence,
                ["candidates"] = new JsonArray(Resume.Select(c => (JsonNode)c.ToJson()).ToArray())
            }
        }.ToJsonString()
    };
}

/// <summary>
/// Builds break records (02 §8.3, §10.5).
/// <para>
/// A break is the most load-bearing thing this tool produces: it is where the answer stops being
/// "the code does this" and becomes "the code cannot say, and here is why, and here is where to
/// look". The explanation always comes from the template table, and the same break reached by
/// several edges is one record (02 §10.5).
/// </para>
/// </summary>
public sealed class BreakFactory
{
    public const string Prefix = "b_";

    private readonly Dictionary<string, BreakRecord> _records = new(StringComparer.Ordinal);

    /// <summary>Every break produced, by id, in a stable order.</summary>
    public IReadOnlyList<BreakRecord> Records
        => _records.Values.OrderBy(record => record.Id, StringComparer.Ordinal).ToList();

    /// <summary>
    /// Creates a break, or returns the one already recorded for the same point (02 §10.5): the id
    /// is the category, the reason, the callsite and the contract key.
    /// </summary>
    public BreakRecord Create(
        string category,
        string reason,
        BreakSite at,
        BreakContract contract,
        WhyTemplates.Fields fields,
        IReadOnlyList<ResumeCandidate>? resume = null)
    {
        var id = Ids.DerivedId(Prefix, new
        {
            category,
            reason,
            callsite = at.CallsiteId ?? string.Empty,
            key = contract.Key ?? string.Empty
        });

        if (_records.TryGetValue(id, out var existing)) return existing;

        var record = new BreakRecord
        {
            Id = id,
            Category = category,
            Reason = reason,
            At = at,
            Contract = contract,
            Why = WhyTemplates.Of(category, reason, fields),
            Resume = resume ?? Array.Empty<ResumeCandidate>()
        };

        _records[id] = record;
        return record;
    }

    public void WriteAll(DomainWriter writer)
    {
        foreach (var record in Records) writer.Add(record.ToRow());
    }
}
