using System.Text.Json.Serialization;
using ScreenFlow.Core.Storage;

namespace ScreenFlow.Core.Query;

/// <summary><c>message get</c> (02 §11.2).</summary>
public sealed record MessageResult
{
    [JsonPropertyName("code")] public required int Code { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("locale")] public required string Locale { get; init; }
    [JsonPropertyName("raw")] public string? Raw { get; init; }
    [JsonPropertyName("plain")] public string? Plain { get; init; }
}

/// <summary>
/// The <c>message get</c> command (02 §11.2). <c>--code</c> is either a numeric code (<c>22</c>) or
/// the enum member name that carries it (<c>ERRB022</c>), resolved through the enumerated
/// <c>ResultStatusCode</c> members the adapters recorded.
/// </summary>
public sealed class MessageQuery
{
    private readonly IndexReader _reader;

    public MessageQuery(IndexReader reader) => _reader = reader;

    public MessageResult? Get(string code, string locale)
    {
        var (numeric, name) = Resolve(code);
        if (numeric is null) return null;

        var row = _reader.Message(numeric.Value, locale);
        if (row is null) return null;

        return new MessageResult
        {
            Code = row.Code,
            Name = name,
            Locale = row.Locale,
            Raw = row.TextRaw,
            Plain = row.TextPlain
        };
    }

    /// <summary>Turns <c>22</c> or <c>ERRB022</c> into the numeric code and its enum name, if any.</summary>
    public (int? Code, string? Name) Resolve(string code)
    {
        if (int.TryParse(code, out var numeric))
        {
            return (numeric, _reader.EnumMembersByValue(numeric).FirstOrDefault()?.Name);
        }

        var member = _reader.EnumMembersByName(code).FirstOrDefault();
        return member is null ? ((int?)null, null) : (member.ValueId, member.Name);
    }
}
