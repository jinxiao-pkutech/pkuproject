using System.Text.Json.Serialization;
using ScreenFlow.Core.Ir;
using ScreenFlow.Core.Storage;

namespace ScreenFlow.Core.Query;

/// <summary><c>evidence get</c> (02 §11.2).</summary>
public sealed record EvidenceResult
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("file")] public required string File { get; init; }
    [JsonPropertyName("sl")] public required int StartLine { get; init; }
    [JsonPropertyName("sc")] public required int StartColumn { get; init; }
    [JsonPropertyName("el")] public required int EndLine { get; init; }
    [JsonPropertyName("ec")] public required int EndColumn { get; init; }
    [JsonPropertyName("snippet")] public string? Snippet { get; init; }
    [JsonPropertyName("snippetHash")] public string? SnippetHash { get; init; }
    [JsonPropertyName("storedHash")] public string? StoredHash { get; init; }
    [JsonPropertyName("matches")] public required bool Matches { get; init; }
}

/// <summary>
/// The <c>evidence get</c> command (02 §11.2). Reads the snippet back off disk, re-hashes it and
/// compares against the stored <c>snippet_hash</c>; a mismatch is reported through
/// <see cref="IsStale"/> so the envelope's <c>meta.stale</c> can carry it.
/// </summary>
public sealed class EvidenceQuery
{
    private readonly IndexReader _reader;
    private readonly string _projectRoot;

    public EvidenceQuery(IndexReader reader, string projectRoot)
    {
        _reader = reader;
        _projectRoot = projectRoot;
    }

    public EvidenceResult? Get(string id, int context = 0)
    {
        var evidence = _reader.Evidence(id);
        if (evidence is null) return null;

        var encoding = _reader.File(evidence.File)?.Encoding ?? Encodings.Utf8;
        var snippet = SnippetHasher.ReadSnippet(_projectRoot, evidence.File, evidence.Span, context, encoding);
        var exact = context > 0
            ? SnippetHasher.ReadSnippet(_projectRoot, evidence.File, evidence.Span, encoding)
            : snippet;
        var hash = exact is null ? null : Ids.Sha256Hex(exact);

        return new EvidenceResult
        {
            Id = evidence.Id,
            File = evidence.File,
            StartLine = evidence.Span.StartLine,
            StartColumn = evidence.Span.StartColumn,
            EndLine = evidence.Span.EndLine,
            EndColumn = evidence.Span.EndColumn,
            Snippet = snippet,
            SnippetHash = hash,
            StoredHash = evidence.SnippetHash,
            Matches = hash is not null && string.Equals(hash, evidence.SnippetHash, StringComparison.Ordinal)
        };
    }

    /// <summary>True when the stored hash exists and the file no longer hashes to it.</summary>
    public bool IsStale(IndexReader.EvidenceRecord evidence)
    {
        if (evidence.SnippetHash is null) return false;

        var encoding = _reader.File(evidence.File)?.Encoding ?? Encodings.Utf8;
        var hash = SnippetHasher.Hash(_projectRoot, evidence.File, evidence.Span, encoding);
        return hash is not null && !string.Equals(hash, evidence.SnippetHash, StringComparison.Ordinal);
    }
}
