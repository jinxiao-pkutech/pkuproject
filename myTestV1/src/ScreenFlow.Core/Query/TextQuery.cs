using System.Text.Json.Serialization;
using ScreenFlow.Core.Storage;

namespace ScreenFlow.Core.Query;

/// <summary><c>text get</c> (02 §11.2).</summary>
public sealed record TextResult
{
    [JsonPropertyName("collection")] public required string Collection { get; init; }
    [JsonPropertyName("key")] public required string Key { get; init; }
    [JsonPropertyName("locale")] public required string Locale { get; init; }
    [JsonPropertyName("raw")] public string? Raw { get; init; }
    [JsonPropertyName("plain")] public string? Plain { get; init; }
    [JsonPropertyName("file")] public string? File { get; init; }
}

/// <summary>
/// The <c>text get</c> command (02 §11.2). A key is looked up across every collection unless one
/// is named, because the same key can be shared between the customer and employee tables.
/// </summary>
public sealed class TextQuery
{
    private readonly IndexReader _reader;

    public TextQuery(IndexReader reader) => _reader = reader;

    public IReadOnlyList<TextResult> Get(string key, string? collection, string? locale)
        => _reader.Texts(key, collection, locale)
            .Select(row => new TextResult
            {
                Collection = row.Collection,
                Key = row.Key,
                Locale = row.Locale,
                Raw = row.TextRaw,
                Plain = row.TextPlain,
                File = row.File
            })
            .ToList();
}
