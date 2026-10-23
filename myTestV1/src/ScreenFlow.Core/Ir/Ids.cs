using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScreenFlow.Core.Ir;

/// <summary>
/// Stable identifier construction (02 §5.6). Every id is a prefix plus 20 hex characters of a SHA-1
/// over the fields that define the thing, so two runs over the same input agree and a diff between
/// two indexes is meaningful.
/// </summary>
public static class Ids
{
    /// <summary>Hex characters retained from the hash.</summary>
    public const int HashLength = 20;

    public const string SymbolPrefix = "s_";
    public const string CallsitePrefix = "c_";
    public const string AssignPrefix = "a_";
    public const string CreationPrefix = "n_";
    public const string EvidencePrefix = "e_";

    /// <summary>Lambda and local-function suffix marker (02 §5.6).</summary>
    public const string LambdaMarker = "~λ";

    /// <summary>Local variable suffix marker (02 §5.6).</summary>
    public const string LocalMarker = "~local:";

    /// <summary><c>s_ + sha1(profileId | assembly | symbolKey)</c>.</summary>
    public static string SymbolId(string profileId, string assembly, string symbolKey)
        => SymbolPrefix + Sha1Short(string.Concat(profileId, "|", assembly, "|", symbolKey));

    /// <summary>
    /// <c>&lt;prefix&gt; + sha1(callerId | file | spanStart)</c> for a callsite, assignment or
    /// creation. <paramref name="spanStart"/> is the absolute character offset of the expression,
    /// which distinguishes two sites on one line.
    /// </summary>
    public static string SiteId(string prefix, string callerId, string file, int spanStart)
        => prefix + Sha1Short(string.Concat(
            callerId, "|", Normalize(file), "|", spanStart.ToString(CultureInfo.InvariantCulture)));

    public static string CallsiteId(string callerId, string file, int spanStart)
        => SiteId(CallsitePrefix, callerId, file, spanStart);

    public static string AssignId(string methodId, string file, int spanStart)
        => SiteId(AssignPrefix, methodId, file, spanStart);

    public static string CreationId(string methodId, string file, int spanStart)
        => SiteId(CreationPrefix, methodId, file, spanStart);

    /// <summary><c>e_ + sha1(file | sl:sc-el:ec)</c>: identical locations collapse onto one id.</summary>
    public static string EvidenceId(string file, Span span)
        => EvidencePrefix + Sha1Short(string.Concat(Normalize(file), "|", span.ToIdPart()));

    /// <summary>
    /// A derived id — screen edge, break, path and so on — over the canonical JSON of the fields
    /// that define it. Canonical means property names sorted and no whitespace, so the id does not
    /// depend on the order the producer happened to write the fields in.
    /// </summary>
    public static string DerivedId<T>(string prefix, T payload)
        => prefix + Sha1Short(CanonicalJson(payload));

    public static string DerivedIdFromNode(string prefix, JsonNode? payload)
        => prefix + Sha1Short(Canonicalize(payload)?.ToJsonString(CompactOptions) ?? "null");

    // ---------------------------------------------------------------- symbol keys

    /// <summary>
    /// The key of a lambda or local function: its container's key plus the ordinal marker. Nesting
    /// concatenates, so an inner lambda's key contains its outer lambda's ordinal (02 §5.6).
    /// </summary>
    public static string LambdaKey(string containerKey, int ordinal)
        => string.Concat(containerKey, LambdaMarker, ordinal.ToString(CultureInfo.InvariantCulture));

    /// <summary>The key of a local variable: name plus the declaration's start offset (02 §5.6).</summary>
    public static string LocalKey(string containerKey, string name, int declarationStartOffset)
        => string.Concat(containerKey, LocalMarker, name, "@",
            declarationStartOffset.ToString(CultureInfo.InvariantCulture));

    // ---------------------------------------------------------------- helpers

    /// <summary>Repository paths are forward-slashed so an id never depends on the host OS.</summary>
    public static string Normalize(string path) => path.Replace('\\', '/');

    public static string Sha1Short(string value)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant()[..HashLength];
    }

    public static string Sha256Hex(ReadOnlySpan<byte> bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static string Sha256Hex(string value) => Sha256Hex(Encoding.UTF8.GetBytes(value));

    private static readonly JsonSerializerOptions CompactOptions = CreateCompactOptions();

    private static JsonSerializerOptions CreateCompactOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    /// <summary>
    /// Canonical JSON for id derivation: object keys sorted, no whitespace. Array order is kept,
    /// because in an array order is part of the value.
    /// </summary>
    public static string CanonicalJson<T>(T payload)
    {
        var node = JsonSerializer.SerializeToNode(payload, CompactOptions);
        return Canonicalize(node)?.ToJsonString(CompactOptions) ?? "null";
    }

    private static JsonNode? Canonicalize(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var sorted = new JsonObject();
                foreach (var property in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                    sorted[property.Key] = Canonicalize(property.Value?.DeepClone());
                return sorted;
            }
            case JsonArray array:
            {
                var copy = new JsonArray();
                foreach (var item in array) copy.Add(Canonicalize(item?.DeepClone()));
                return copy;
            }
            default:
                return node?.DeepClone();
        }
    }

    /// <summary>True when an id carries the prefix its kind requires.</summary>
    public static bool HasPrefix(string id, string prefix)
        => id.StartsWith(prefix, StringComparison.Ordinal)
           && id.Length == prefix.Length + HashLength;
}
