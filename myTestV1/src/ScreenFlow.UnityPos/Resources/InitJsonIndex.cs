using System.Text.Json;
using ScreenFlow.Core.Ir;

namespace ScreenFlow.UnityPos.Resources;

/// <summary>
/// The screen initialize JSON files (02 §9.9; 03 §14.1).
/// <para>
/// Each screen has one, and its values are localization keys — this is the link from a view's
/// <c>dataIni.TextTitle</c> to the text a control shows. The structure is kept as written, because
/// the key names differ per screen (03 §14.1) and inventing a schema would lose the ones not
/// anticipated.
/// </para>
/// </summary>
public sealed class InitJsonIndex
{
    /// <summary>Emitted when an initialize JSON cannot be parsed.</summary>
    public const string UnreadableCode = "W_INIT_JSON_UNREADABLE";

    /// <summary>The placeholder of <c>ui.initJsonNamePattern</c>.</summary>
    public const string NamePlaceholder = "{ViewWithoutSuffix}";

    private readonly Dictionary<string, Entry> _byScreenName;

    private InitJsonIndex(Dictionary<string, Entry> byScreenName) => _byScreenName = byScreenName;

    /// <summary>One file: the document and where it came from.</summary>
    public sealed record Entry(string File, JsonDocument Document)
    {
        public JsonElement Root => Document.RootElement;
    }

    public IReadOnlyList<string> Files
        => _byScreenName.Values.Select(entry => entry.File).OrderBy(file => file, StringComparer.Ordinal).ToList();

    /// <summary>
    /// Reads the directory of initialize JSON files. Comments and trailing commas are allowed,
    /// because the real files have both (02 §9.9).
    /// </summary>
    public static InitJsonIndex Load(
        string projectRoot,
        string directory,
        string namePattern,
        ICollection<DiagRec> diagnostics,
        ICollection<string>? readFiles = null)
    {
        var byScreenName = new Dictionary<string, Entry>(StringComparer.Ordinal);
        var root = Path.Combine(projectRoot, directory);

        if (!Directory.Exists(root)) return new InitJsonIndex(byScreenName);

        var suffix = namePattern.Replace(NamePlaceholder, string.Empty, StringComparison.Ordinal);

        foreach (var absolute in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            var relative = LocalizationIndex.Relative(projectRoot, absolute);
            var fileName = Path.GetFileName(absolute);

            if (suffix.Length > 0 && !fileName.EndsWith(suffix, StringComparison.Ordinal)) continue;

            var screenName = suffix.Length > 0 ? fileName[..^suffix.Length] : Path.GetFileNameWithoutExtension(fileName);
            readFiles?.Add(relative);

            try
            {
                var document = JsonDocument.Parse(
                    File.ReadAllText(absolute),
                    new JsonDocumentOptions
                    {
                        AllowTrailingCommas = true,
                        CommentHandling = JsonCommentHandling.Skip
                    });

                byScreenName[screenName] = new Entry(relative, document);
            }
            catch (JsonException exception)
            {
                diagnostics.Add(DiagRec.Warning(
                    UnreadableCode,
                    $"'{relative}' is not valid JSON ({exception.Message}); the screen's text keys are unknown.",
                    relative,
                    (int?)exception.LineNumber + 1));
            }
        }

        return new InitJsonIndex(byScreenName);
    }

    /// <summary>
    /// The initialize JSON of a view type: the view name without its suffix (03 §14.1), so
    /// <c>EdyCharge_SSAW0903_View</c> reads <c>EdyCharge_SSAW0903_Initialize.json</c>.
    /// </summary>
    public Entry? ForView(string viewTypeName, string viewSuffix)
    {
        var screenName = viewTypeName.EndsWith(viewSuffix, StringComparison.Ordinal)
            ? viewTypeName[..^viewSuffix.Length]
            : viewTypeName;

        return _byScreenName.GetValueOrDefault(screenName);
    }

    public Entry? ForScreenName(string screenName) => _byScreenName.GetValueOrDefault(screenName);

    /// <summary>
    /// The localization key a property of the DTO names, e.g. <c>TextTitle</c>. An array property
    /// is indexed, which is how a button row is bound (03 §14.2).
    /// </summary>
    public static string? Key(JsonElement root, string property, int? index = null)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!root.TryGetProperty(property, out var value)) return null;

        if (index is null)
            return value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        if (value.ValueKind != JsonValueKind.Array) return null;

        var items = value.EnumerateArray().ToList();
        if (index.Value < 0 || index.Value >= items.Count) return null;

        return items[index.Value].ValueKind == JsonValueKind.String ? items[index.Value].GetString() : null;
    }

    public static IReadOnlyList<string> Properties(JsonElement root)
        => root.ValueKind != JsonValueKind.Object
            ? Array.Empty<string>()
            : root.EnumerateObject()
                .Select(property => property.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();
}
