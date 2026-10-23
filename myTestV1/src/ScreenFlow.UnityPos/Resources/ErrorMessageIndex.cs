using System.Text.Json;
using ScreenFlow.Core.Ir;
using ScreenFlow.Core.Storage;

namespace ScreenFlow.UnityPos.Resources;

/// <summary>
/// The error message table (02 §9.9; 03 §16).
/// <para>
/// Error texts are not in the string tables: they live in one JSON array keyed by error code, with
/// one field per locale and a separate field for full-self mode. Both the normal and the full-self
/// text are kept, because which one a screen shows depends on the run-time mode (03 §16) — picking
/// one would answer a question the code does not answer.
/// </para>
/// </summary>
public sealed class ErrorMessageIndex
{
    public const string CodeField = "ErrCode";

    /// <summary>Emitted when the error message file is missing or unreadable.</summary>
    public const string UnreadableCode = "W_ERROR_MESSAGES_UNREADABLE";

    private readonly Dictionary<int, Dictionary<string, string?>> _byCode;

    private ErrorMessageIndex(Dictionary<int, Dictionary<string, string?>> byCode, string? file)
    {
        _byCode = byCode;
        File = file;
    }

    /// <summary>Project-relative path of the file the messages came from.</summary>
    public string? File { get; }

    public IReadOnlyList<int> Codes => _byCode.Keys.OrderBy(code => code).ToList();

    public static ErrorMessageIndex Load(
        string projectRoot,
        string relativePath,
        IReadOnlyDictionary<string, string> localeFields,
        ICollection<DiagRec> diagnostics,
        ICollection<string>? readFiles = null)
    {
        var byCode = new Dictionary<int, Dictionary<string, string?>>();
        var absolute = Path.Combine(projectRoot, relativePath);

        if (!System.IO.File.Exists(absolute))
        {
            diagnostics.Add(DiagRec.Warning(
                UnreadableCode,
                $"The error message file '{relativePath}' does not exist; no error text can be reported.",
                relativePath));

            return new ErrorMessageIndex(byCode, null);
        }

        readFiles?.Add(relativePath);

        try
        {
            using var document = JsonDocument.Parse(
                System.IO.File.ReadAllText(absolute),
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });

            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                diagnostics.Add(DiagRec.Warning(
                    UnreadableCode,
                    $"'{relativePath}' is not a JSON array, which is the documented shape (03 §16).",
                    relativePath));

                return new ErrorMessageIndex(byCode, relativePath);
            }

            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                if (!entry.TryGetProperty(CodeField, out var codeValue)) continue;
                if (codeValue.ValueKind != JsonValueKind.Number || !codeValue.TryGetInt32(out var code)) continue;

                var texts = new Dictionary<string, string?>(StringComparer.Ordinal);

                foreach (var (locale, field) in localeFields)
                {
                    if (!entry.TryGetProperty(field, out var text)) continue;
                    if (text.ValueKind != JsonValueKind.String) continue;

                    texts[locale] = text.GetString();
                }

                byCode[code] = texts;
            }
        }
        catch (JsonException exception)
        {
            diagnostics.Add(DiagRec.Warning(
                UnreadableCode,
                $"'{relativePath}' is not valid JSON ({exception.Message}); no error text can be reported.",
                relativePath,
                (int?)exception.LineNumber + 1));
        }

        return new ErrorMessageIndex(byCode, relativePath);
    }

    /// <summary>The rows for the <c>messages</c> table (02 §7.3).</summary>
    public IReadOnlyList<MessageRow> Rows()
    {
        var rows = new List<MessageRow>();

        foreach (var (code, texts) in _byCode.OrderBy(pair => pair.Key))
        foreach (var (locale, raw) in texts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            rows.Add(new MessageRow
            {
                Code = code,
                Locale = locale,
                TextRaw = raw,
                TextPlain = RichText.PlainOrNull(raw)
            });
        }

        return rows;
    }

    public string? Text(int code, string locale)
        => _byCode.TryGetValue(code, out var texts) ? texts.GetValueOrDefault(locale) : null;

    public IReadOnlyDictionary<string, string?> TextsOf(int code)
        => _byCode.TryGetValue(code, out var texts)
            ? texts
            : new Dictionary<string, string?>(StringComparer.Ordinal);
}
