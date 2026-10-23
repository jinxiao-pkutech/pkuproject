using System.Text;
using ScreenFlow.Core.Ir;

namespace ScreenFlow.Core.Storage;

/// <summary>
/// Reads a source snippet back off disk and hashes it (02 §7.6, §11.2 <c>evidence get</c>).
/// <para>
/// The hash covers the exact span text decoded with the file's recorded encoding, so the same
/// function at build time and at query time agrees or disagrees on precisely the source bytes
/// between the span's columns.
/// </para>
/// </summary>
public static class SnippetHasher
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private static bool _codePagesRegistered;

    /// <summary>The exact span text, or null when the file or span cannot be read.</summary>
    public static string? ReadSnippet(string projectRoot, string file, Span span, string encodingName)
    {
        if (!span.IsValid) return null;

        var path = Path.IsPathRooted(file) ? file : Path.Combine(projectRoot, file);
        if (!File.Exists(path)) return null;

        return Extract(Decode(File.ReadAllBytes(path), encodingName), span);
    }

    /// <summary>
    /// The span text with up to <paramref name="context"/> whole lines either side. The exact span
    /// columns are honoured for the span's own first and last line; surrounding lines are whole.
    /// </summary>
    public static string? ReadSnippet(string projectRoot, string file, Span span, int context, string encodingName)
    {
        if (context <= 0) return ReadSnippet(projectRoot, file, span, encodingName);
        if (!span.IsValid) return null;

        var path = Path.IsPathRooted(file) ? file : Path.Combine(projectRoot, file);
        if (!File.Exists(path)) return null;

        var text = Decode(File.ReadAllBytes(path), encodingName);
        var firstLine = Math.Max(1, span.StartLine - context);
        var lastLine = span.EndLine + context;

        var start = OffsetOf(text, firstLine - 1);
        var end = OffsetOf(text, lastLine);

        if (start < 0 || end < start || end > text.Length) return null;

        return text[start..end];
    }

    /// <summary>SHA-256 over the exact span text (02 §7.6), null when the snippet cannot be read.</summary>
    public static string? Hash(string projectRoot, string file, Span span, string encodingName)
        => ReadSnippet(projectRoot, file, span, encodingName) is { } snippet ? Ids.Sha256Hex(snippet) : null;

    /// <summary>Decodes bytes with one of the three recorded encodings (02 §6.3).</summary>
    public static string Decode(byte[] bytes, string encodingName)
    {
        if (string.Equals(encodingName, Encodings.Utf8Bom, StringComparison.Ordinal)
            && bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Utf8.GetString(bytes, 3, bytes.Length - 3);
        }

        if (string.Equals(encodingName, Encodings.Cp932, StringComparison.Ordinal))
        {
            return Cp932.GetString(bytes);
        }

        return Utf8.GetString(bytes);
    }

    private static Encoding Cp932
    {
        get
        {
            if (!_codePagesRegistered)
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                _codePagesRegistered = true;
            }

            return Encoding.GetEncoding(932);
        }
    }

    /// <summary>The text of the span, 1-based line/column, end-exclusive (02 §5.2).</summary>
    public static string? Extract(string text, Span span)
    {
        if (!span.IsValid) return null;

        var start = OffsetOf(text, span.StartLine - 1) + span.StartColumn - 1;
        var end = OffsetOf(text, span.EndLine - 1) + span.EndColumn - 1;

        if (start < 0 || end < start || end > text.Length) return null;

        return text[start..end];
    }

    /// <summary>Absolute character index of the first character of a 0-based line.</summary>
    private static int OffsetOf(string text, int line)
    {
        if (line <= 0) return 0;

        var current = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                current++;
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                if (current == line) return i + 1;
            }
            else if (text[i] == '\n')
            {
                current++;
                if (current == line) return i + 1;
            }
        }

        return text.Length;
    }
}
