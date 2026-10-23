using System.Text.Json.Serialization;
using ScreenFlow.Core.Graph;
using ScreenFlow.Core.Ir;
using ScreenFlow.Core.Storage;

namespace ScreenFlow.Core.Query;

/// <summary><c>symbol find</c> candidate (02 §11.2).</summary>
public sealed record SymbolCandidate
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("kind")] public required string Kind { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("fqName")] public required string FqName { get; init; }
    [JsonPropertyName("file")] public string? File { get; init; }
}

/// <summary>One caller or callee relation, with how sure the binding is (02 §11.2).</summary>
public sealed record CallRelation
{
    [JsonPropertyName("callsiteId")] public required string CallsiteId { get; init; }
    [JsonPropertyName("from")] public SymbolSummary? From { get; init; }
    [JsonPropertyName("to")] public SymbolSummary? To { get; init; }
    [JsonPropertyName("confidence")] public required string Confidence { get; init; }
    [JsonPropertyName("file")] public string? File { get; init; }
    [JsonPropertyName("span")] public Span? Span { get; init; }
}

/// <summary>
/// The <c>symbol find/get/callers/callees</c> commands (02 §11.2).
/// <para>
/// Lookup is by id only once a name search has narrowed the field; a simple name never resolves to
/// one symbol on its own (CLAUDE.md §4).
/// </para>
/// </summary>
public sealed class SymbolQuery
{
    private readonly IndexReader _reader;

    public SymbolQuery(IndexReader reader) => _reader = reader;

    public static SymbolSummary? Summary(SymbolRec? symbol)
        => symbol is null
            ? null
            : new SymbolSummary
            {
                Id = symbol.Id,
                Kind = symbol.Kind,
                Name = symbol.Name,
                FqName = symbol.FqName,
                File = symbol.File,
                Span = symbol.Span
            };

    public IReadOnlyList<SymbolCandidate> Find(string name, string? kind, int limit)
        => _reader.SymbolsByName(name)
            .Where(symbol => kind is null || string.Equals(symbol.Kind, kind, StringComparison.Ordinal))
            .Take(limit)
            .Select(symbol => new SymbolCandidate
            {
                Id = symbol.Id,
                Kind = symbol.Kind,
                Name = symbol.Name,
                FqName = symbol.FqName,
                File = symbol.File
            })
            .ToList();

    public SymbolRec? Get(string id) => _reader.Symbol(id);

    public IReadOnlyList<CallRelation> Callers(string id)
        => _reader.CallsitesTargeting(id)
            .Select(site =>
            {
                var from = _reader.Symbol(site.CallerId);
                return new CallRelation
                {
                    CallsiteId = site.Id,
                    From = Summary(from),
                    To = site.TargetId is { } targetId ? Summary(_reader.Symbol(targetId)) : null,
                    Confidence = Confidence(site.TargetIsVirtual),
                    File = from?.File,
                    Span = site.Span
                };
            })
            .ToList();

    public IReadOnlyList<CallRelation> Callees(string id)
        => _reader.CallsitesIn(id)
            .Select(site =>
            {
                var from = _reader.Symbol(site.CallerId);
                return new CallRelation
                {
                    CallsiteId = site.Id,
                    From = Summary(from),
                    To = site.TargetId is { } targetId ? Summary(_reader.Symbol(targetId)) : null,
                    Confidence = Confidence(site.TargetIsVirtual),
                    File = from?.File,
                    Span = site.Span
                };
            })
            .ToList();

    private static string Confidence(bool targetIsVirtual)
        => targetIsVirtual ? Confidences.Candidate : Confidences.Certain;
}
