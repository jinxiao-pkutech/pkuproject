using ScreenFlow.Core.Ir;
using ScreenFlow.Core.Storage;

namespace ScreenFlow.UnityPos.Resources;

/// <summary>
/// The localization string tables, keyed as a reader asks about them (02 §9.9; 03 §15).
/// <para>
/// A screen's text is never in the UXML: a control holds a key, and the key is resolved through the
/// collection that belongs to the screen's terminal. The join is by guid — a locale table names the
/// shared table it belongs to — because two collections can hold the same key with different text.
/// </para>
/// </summary>
public sealed class LocalizationIndex
{
    /// <summary>Emitted when a locale table points at a shared table that is not there.</summary>
    public const string OrphanTableCode = "W_STRING_TABLE_ORPHAN";

    private readonly Dictionary<string, Collection> _collections;

    private LocalizationIndex(Dictionary<string, Collection> collections, IReadOnlyList<UnityStringTableFile> files)
    {
        _collections = collections;
        Files = files;
    }

    public IReadOnlyList<UnityStringTableFile> Files { get; }

    public IReadOnlyList<string> Collections
        => _collections.Keys.OrderBy(name => name, StringComparer.Ordinal).ToList();

    /// <summary>One collection: the key-to-id map and the per-locale texts (03 §15).</summary>
    private sealed record Collection(string Name, string Guid)
    {
        public Dictionary<string, long> IdByKey { get; } = new(StringComparer.Ordinal);

        /// <summary>Locale code to (id to text).</summary>
        public Dictionary<string, Dictionary<long, string?>> Locales { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> FileByLocale { get; } = new(StringComparer.Ordinal);

        public string? SharedFile { get; set; }
    }

    /// <summary>
    /// Reads every <c>.asset</c> under the string table root. Diagnostics are returned rather than
    /// thrown: a project with one unreadable table still has every other table.
    /// </summary>
    public static LocalizationIndex Load(
        string projectRoot,
        string stringTableRoot,
        ICollection<DiagRec> diagnostics,
        ICollection<string>? readFiles = null)
    {
        var root = Path.Combine(projectRoot, stringTableRoot);
        var parsed = new List<UnityStringTableFile>();

        if (Directory.Exists(root))
        {
            foreach (var absolute in Directory.EnumerateFiles(root, "*.asset", SearchOption.AllDirectories)
                         .OrderBy(path => path, StringComparer.Ordinal))
            {
                var relative = Relative(projectRoot, absolute);
                readFiles?.Add(relative);
                parsed.Add(UnityYamlStringTable.ParseFile(absolute, relative));
            }
        }

        var collections = new Dictionary<string, Collection>(StringComparer.Ordinal);
        var byGuid = new Dictionary<string, Collection>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in parsed.Where(f => f.IsShared))
        {
            var name = file.CollectionName ?? file.Name ?? file.Path;
            var collection = new Collection(name, file.CollectionGuid!) { SharedFile = file.Path };

            foreach (var entry in file.Entries) collection.IdByKey[entry.Key] = entry.Id;

            collections[name] = collection;
            byGuid[file.CollectionGuid!] = collection;
        }

        foreach (var file in parsed.Where(f => f.IsLocale))
        {
            if (!byGuid.TryGetValue(file.SharedDataGuid!, out var collection))
            {
                // Without its shared table the ids in this file cannot be turned into keys, and
                // guessing which collection it belongs to by file name is exactly the kind of
                // name-matching this project forbids (CLAUDE.md §4).
                diagnostics.Add(DiagRec.Warning(
                    OrphanTableCode,
                    $"'{file.Path}' points at shared table guid '{file.SharedDataGuid}', which no " +
                    "shared table in the project declares; its texts cannot be keyed.",
                    file.Path));

                continue;
            }

            var locale = file.LocaleCode!;
            if (!collection.Locales.TryGetValue(locale, out var texts))
            {
                texts = new Dictionary<long, string?>();
                collection.Locales[locale] = texts;
                collection.FileByLocale[locale] = file.Path;
            }

            foreach (var entry in file.Data) texts[entry.Id] = entry.Text;
        }

        return new LocalizationIndex(collections, parsed);
    }

    /// <summary>The rows for the <c>texts</c> table, ordered so the build is deterministic.</summary>
    public IReadOnlyList<TextRow> Rows()
    {
        var rows = new List<TextRow>();

        foreach (var collection in _collections.Values.OrderBy(c => c.Name, StringComparer.Ordinal))
        foreach (var (key, id) in collection.IdByKey.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        foreach (var (locale, texts) in collection.Locales.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!texts.TryGetValue(id, out var raw)) continue;

            rows.Add(new TextRow
            {
                Collection = collection.Name,
                Key = key,
                Locale = locale,
                TextRaw = raw,
                TextPlain = RichText.PlainOrNull(raw),
                File = collection.FileByLocale.GetValueOrDefault(locale)
            });
        }

        return rows;
    }

    /// <summary>The raw text for one key, or null when that collection has no such key or locale.</summary>
    public string? Text(string collection, string key, string locale)
        => _collections.TryGetValue(collection, out var found)
           && found.IdByKey.TryGetValue(key, out var id)
           && found.Locales.TryGetValue(locale, out var texts)
           && texts.TryGetValue(id, out var raw)
            ? raw
            : null;

    /// <summary>
    /// The first collection in the search order that holds the key (03 §15). The order comes from
    /// the terminal's configuration, because one key can exist in several collections.
    /// </summary>
    public (string? Collection, string? Raw) Lookup(
        IReadOnlyList<string> searchOrder,
        string key,
        string locale)
    {
        foreach (var collection in searchOrder)
        {
            var raw = Text(collection, key, locale);
            if (raw is not null) return (collection, raw);
        }

        // Known key, no text in this locale: say which collection has it, with no text.
        foreach (var collection in searchOrder)
        {
            if (_collections.TryGetValue(collection, out var found) && found.IdByKey.ContainsKey(key))
                return (collection, null);
        }

        return (null, null);
    }

    public IReadOnlyList<string> KeysOf(string collection)
        => _collections.TryGetValue(collection, out var found)
            ? found.IdByKey.Keys.OrderBy(key => key, StringComparer.Ordinal).ToList()
            : Array.Empty<string>();

    public IReadOnlyList<string> LocalesOf(string collection)
        => _collections.TryGetValue(collection, out var found)
            ? found.Locales.Keys.OrderBy(locale => locale, StringComparer.Ordinal).ToList()
            : Array.Empty<string>();

    internal static string Relative(string projectRoot, string absolute)
    {
        var root = Path.GetFullPath(projectRoot).Replace('\\', '/').TrimEnd('/');
        var path = Path.GetFullPath(absolute).Replace('\\', '/');

        return path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)
            ? path[(root.Length + 1)..]
            : path;
    }
}
