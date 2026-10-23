using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ScreenFlow.UnityPos.Resources;

/// <summary>One key and the numeric id it is stored under, from a shared table (03 §15).</summary>
public sealed record SharedEntry(long Id, string Key);

/// <summary>One numeric id and the text a locale table holds for it (03 §15).</summary>
public sealed record LocalizedEntry(long Id, string? Text);

/// <summary>One parsed <c>.asset</c> file: either a shared table or a locale table (03 §15).</summary>
public sealed record UnityStringTableFile
{
    /// <summary>Project-root relative path, which is what every record cites (02 §11.3).</summary>
    public required string Path { get; init; }

    public string? Name { get; init; }

    /// <summary>Collection name, e.g. <c>StringLocalizationTable_Customer</c>.</summary>
    public string? CollectionName { get; init; }

    /// <summary>Guid of a shared table, which the locale tables point at.</summary>
    public string? CollectionGuid { get; init; }

    /// <summary>Locale code of a locale table, from <c>m_LocaleId.m_Code</c>.</summary>
    public string? LocaleCode { get; init; }

    /// <summary>Guid of the shared table this locale table belongs to.</summary>
    public string? SharedDataGuid { get; init; }

    public IReadOnlyList<SharedEntry> Entries { get; init; } = Array.Empty<SharedEntry>();

    public IReadOnlyList<LocalizedEntry> Data { get; init; } = Array.Empty<LocalizedEntry>();

    public bool IsShared => CollectionGuid is not null;

    public bool IsLocale => LocaleCode is not null && SharedDataGuid is not null;
}

/// <summary>
/// A line parser for Unity's localization <c>.asset</c> files (02 §9.9).
/// <para>
/// Hand-written on purpose: a YAML library is forbidden here (02 §9.9), and only the handful of keys
/// 03 §15 documents is read. Everything else in the file is ignored rather than interpreted, which
/// keeps a Unity version change from turning into a parse failure.
/// </para>
/// <para>
/// The hard part is the scalar: a text may be double quoted with <c>\uXXXX</c> escapes, single
/// quoted, bare, empty, or folded across lines. Mis-reading one of those silently corrupts the one
/// thing a screen's text is read from, so every documented form is handled explicitly (E-21).
/// </para>
/// </summary>
public static class UnityYamlStringTable
{
    private const string NameKey = "m_Name";
    private const string CollectionNameKey = "m_TableCollectionName";
    private const string CollectionGuidKey = "m_TableCollectionNameGuidString";
    private const string LocaleIdKey = "m_LocaleId";
    private const string LocaleCodeKey = "m_Code";
    private const string SharedDataKey = "m_SharedData";
    private const string EntriesKey = "m_Entries";
    private const string TableDataKey = "m_TableData";
    private const string IdKey = "m_Id";
    private const string KeyKey = "m_Key";
    private const string LocalizedKey = "m_Localized";

    private static readonly Regex GuidPattern = new(
        @"guid:\s*(?<guid>[0-9a-fA-F]+)", RegexOptions.CultureInvariant);

    /// <summary>A mapping key at the start of a line, used to end a bare scalar.</summary>
    private static readonly Regex MappingKey = new(
        @"^-?\s*[A-Za-z_][A-Za-z0-9_]*:", RegexOptions.CultureInvariant);

    public static UnityStringTableFile ParseFile(string absolutePath, string relativePath)
        => Parse(relativePath, File.ReadAllLines(absolutePath));

    public static UnityStringTableFile Parse(string relativePath, IReadOnlyList<string> lines)
    {
        string? name = null;
        string? collectionName = null;
        string? collectionGuid = null;
        string? localeCode = null;
        string? sharedGuid = null;

        var entries = new List<SharedEntry>();
        var data = new List<LocalizedEntry>();

        var inEntries = false;
        var inTableData = false;
        long? currentId = null;
        string? currentKey = null;
        string? currentText = null;
        var hasCurrent = false;

        void FlushCurrent()
        {
            if (!hasCurrent || currentId is null) return;

            if (inEntries && currentKey is not null) entries.Add(new SharedEntry(currentId.Value, currentKey));
            else if (inTableData) data.Add(new LocalizedEntry(currentId.Value, currentText));

            currentId = null;
            currentKey = null;
            currentText = null;
            hasCurrent = false;
        }

        for (var i = 0; i < lines.Count;)
        {
            var line = lines[i];
            var indent = IndentOf(line);
            var trimmed = line.Trim();

            if (trimmed.Length == 0 || trimmed.StartsWith('%') || trimmed.StartsWith("---"))
            {
                i++;
                continue;
            }

            // A sequence item starts a new entry; the previous one is complete.
            if (trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                FlushCurrent();

                var item = trimmed[2..].Trim();
                if (KeyOf(item) == IdKey)
                {
                    currentId = ParseId(ValueOf(item));
                    hasCurrent = currentId is not null;
                }

                i++;
                continue;
            }

            var key = KeyOf(trimmed);

            switch (key)
            {
                case EntriesKey:
                    FlushCurrent();
                    inEntries = true;
                    inTableData = false;
                    i++;
                    continue;

                case TableDataKey:
                    FlushCurrent();
                    inTableData = true;
                    inEntries = false;
                    i++;
                    continue;

                case NameKey:
                {
                    var (value, next) = ReadScalar(lines, i, indent);
                    name ??= value;
                    i = next;
                    continue;
                }

                case CollectionNameKey:
                {
                    var (value, next) = ReadScalar(lines, i, indent);
                    collectionName ??= value;
                    i = next;
                    continue;
                }

                case CollectionGuidKey:
                {
                    var (value, next) = ReadScalar(lines, i, indent);
                    collectionGuid ??= value;
                    i = next;
                    continue;
                }

                case LocaleIdKey:
                {
                    // m_Code sits one level deeper, under m_LocaleId (03 §15).
                    localeCode ??= ReadNested(lines, i, indent, LocaleCodeKey);
                    i++;
                    continue;
                }

                case SharedDataKey:
                {
                    var match = GuidPattern.Match(trimmed);
                    if (match.Success) sharedGuid ??= match.Groups["guid"].Value;
                    i++;
                    continue;
                }

                case KeyKey when hasCurrent:
                {
                    var (value, next) = ReadScalar(lines, i, indent);
                    currentKey = value;
                    i = next;
                    continue;
                }

                case LocalizedKey when hasCurrent:
                {
                    var (value, next) = ReadScalar(lines, i, indent);
                    currentText = value;
                    i = next;
                    continue;
                }

                default:
                    i++;
                    continue;
            }
        }

        FlushCurrent();

        return new UnityStringTableFile
        {
            Path = relativePath,
            Name = name,
            CollectionName = collectionName,
            CollectionGuid = collectionGuid,
            LocaleCode = localeCode,
            SharedDataGuid = sharedGuid,
            Entries = entries,
            Data = data
        };
    }

    /// <summary>
    /// One scalar, following it across lines when it is folded (02 §9.9). Returns the value and the
    /// next line to read; a key with nothing after it is null, which is not the same as empty text.
    /// </summary>
    public static (string? Value, int Next) ReadScalar(IReadOnlyList<string> lines, int index, int keyIndent)
    {
        var rest = ValueOf(lines[index].Trim());

        if (rest.Length == 0) return (null, index + 1);

        return rest[0] switch
        {
            '"' => ReadQuoted(lines, index, rest, '"'),
            '\'' => ReadQuoted(lines, index, rest, '\''),
            _ => ReadBare(lines, index, rest, keyIndent)
        };
    }

    private static (string? Value, int Next) ReadQuoted(
        IReadOnlyList<string> lines,
        int index,
        string rest,
        char quote)
    {
        var raw = new StringBuilder();
        var line = rest[1..];
        var current = index;

        while (true)
        {
            var end = FindClosingQuote(line, quote);

            if (end >= 0)
            {
                raw.Append(line[..end]);
                return (quote == '"' ? UnescapeDouble(raw.ToString()) : UnescapeSingle(raw.ToString()), current + 1);
            }

            raw.Append(line);
            current++;

            if (current >= lines.Count)
            {
                // Unterminated: the file is malformed, and what was read is still the best fact.
                return (quote == '"' ? UnescapeDouble(raw.ToString()) : UnescapeSingle(raw.ToString()), current);
            }

            // A line ending in a backslash continues without a space; otherwise the fold becomes one
            // space and the next line's indentation is dropped (02 §9.9).
            if (raw.Length > 0 && raw[^1] == '\\') raw.Length -= 1;
            else raw.Append(' ');

            line = lines[current].TrimStart();
        }
    }

    private static (string? Value, int Next) ReadBare(
        IReadOnlyList<string> lines,
        int index,
        string rest,
        int keyIndent)
    {
        var text = new StringBuilder(rest.TrimEnd());
        var next = index + 1;

        while (next < lines.Count)
        {
            var line = lines[next];
            var trimmed = line.Trim();

            if (trimmed.Length == 0) break;
            if (IndentOf(line) <= keyIndent) break;
            if (MappingKey.IsMatch(trimmed)) break;

            text.Append(' ').Append(trimmed);
            next++;
        }

        return (text.ToString(), next);
    }

    /// <summary>The value of a key nested directly under the given line, e.g. <c>m_LocaleId.m_Code</c>.</summary>
    private static string? ReadNested(IReadOnlyList<string> lines, int index, int parentIndent, string key)
    {
        for (var i = index + 1; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Trim().Length == 0) continue;
            if (IndentOf(line) <= parentIndent) return null;

            if (KeyOf(line.Trim()) == key) return ReadScalar(lines, i, IndentOf(line)).Value;
        }

        return null;
    }

    /// <summary>Index of the closing quote, or -1. An escaped quote does not close the scalar.</summary>
    private static int FindClosingQuote(string line, char quote)
    {
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '\\' && quote == '"')
            {
                i++;
                continue;
            }

            if (line[i] != quote) continue;

            // In a single-quoted scalar '' is an escaped quote, not the end.
            if (quote == '\'' && i + 1 < line.Length && line[i + 1] == '\'')
            {
                i++;
                continue;
            }

            return i;
        }

        return -1;
    }

    /// <summary>The escapes 02 §9.9 lists, and nothing invented beyond them.</summary>
    public static string UnescapeDouble(string text)
    {
        if (!text.Contains('\\', StringComparison.Ordinal)) return text;

        var result = new StringBuilder(text.Length);

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\\' || i + 1 >= text.Length)
            {
                result.Append(text[i]);
                continue;
            }

            var escape = text[++i];

            switch (escape)
            {
                case 'n': result.Append('\n'); break;
                case 't': result.Append('\t'); break;
                case 'r': result.Append('\r'); break;
                case '0': result.Append('\0'); break;
                case '"': result.Append('"'); break;
                case '\\': result.Append('\\'); break;
                case '/': result.Append('/'); break;
                case 'u' when i + 4 < text.Length
                              && ushort.TryParse(
                                  text.AsSpan(i + 1, 4),
                                  NumberStyles.HexNumber,
                                  CultureInfo.InvariantCulture,
                                  out var code):
                    result.Append((char)code);
                    i += 4;
                    break;
                default:
                    // An escape the format does not define is kept as written rather than guessed.
                    result.Append('\\').Append(escape);
                    break;
            }
        }

        return result.ToString();
    }

    public static string UnescapeSingle(string text) => text.Replace("''", "'", StringComparison.Ordinal);

    public static string? KeyOf(string trimmedLine)
    {
        var colon = trimmedLine.IndexOf(':');
        if (colon <= 0) return null;

        var key = trimmedLine[..colon];
        return key.All(c => char.IsLetterOrDigit(c) || c == '_') ? key : null;
    }

    public static string ValueOf(string trimmedLine)
    {
        var colon = trimmedLine.IndexOf(':');
        return colon < 0 ? string.Empty : trimmedLine[(colon + 1)..].Trim();
    }

    private static long? ParseId(string text)
        => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : null;

    private static int IndentOf(string line)
    {
        var indent = 0;
        while (indent < line.Length && line[indent] == ' ') indent++;
        return indent;
    }
}
