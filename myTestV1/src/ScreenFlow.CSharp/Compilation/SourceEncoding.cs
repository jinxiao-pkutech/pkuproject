using System.Text;
using ScreenFlow.Core.Ir;

namespace ScreenFlow.CSharp.Compilation;

public sealed record DecodedSource(string Text, string EncodingName, Encoding Encoding, int LineCount);

/// <summary>
/// Encoding detection (02 §6.3): UTF-8 with BOM, then strict UTF-8, then CP932.
/// <para>
/// Strict decoding is what makes the fallback detectable. A lenient UTF-8 decoder turns invalid
/// bytes into U+FFFD, which both corrupts the Japanese literals and shifts every column position
/// after the first bad byte — and column positions are what every piece of evidence is keyed by.
/// </para>
/// </summary>
public static class SourceEncoding
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

    private static bool _providerRegistered;

    /// <summary>
    /// Registers the code-pages provider. The assembly ships in the .NET 8 shared framework, but the
    /// code pages are not loaded until a provider is registered.
    /// </summary>
    public static void EnsureCodePagesRegistered()
    {
        if (_providerRegistered) return;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        _providerRegistered = true;
    }

    public static Encoding Cp932
    {
        get
        {
            EnsureCodePagesRegistered();
            return Encoding.GetEncoding(932);
        }
    }

    public static DecodedSource Decode(byte[] bytes)
    {
        if (HasUtf8Bom(bytes))
        {
            var text = Utf8NoBom.GetString(bytes, 3, bytes.Length - 3);
            return new DecodedSource(text, Encodings.Utf8Bom, Utf8WithBom, CountLines(text));
        }

        try
        {
            var text = StrictUtf8.GetString(bytes);
            return new DecodedSource(text, Encodings.Utf8, Utf8NoBom, CountLines(text));
        }
        catch (DecoderFallbackException)
        {
            var cp932 = Cp932;
            var text = cp932.GetString(bytes);
            return new DecodedSource(text, Encodings.Cp932, cp932, CountLines(text));
        }
    }

    public static DecodedSource DecodeFile(string path) => Decode(File.ReadAllBytes(path));

    public static bool HasUtf8Bom(byte[] bytes)
        => bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;

    /// <summary>
    /// The encoding the IR records for a file. Only the three values of 02 §6.3 are possible, so a
    /// caller never has to interpret an open-ended encoding name.
    /// </summary>
    public static Encoding ByName(string encodingName) => encodingName switch
    {
        Encodings.Utf8Bom => Utf8WithBom,
        Encodings.Cp932 => Cp932,
        _ => Utf8NoBom
    };

    /// <summary>
    /// Line count as the compiler sees it: CR, LF and CRLF all end a line, and a trailing newline
    /// does not add an extra one. 75 files in the target repository mix CRLF and LF (03 §1).
    /// </summary>
    public static int CountLines(string text)
    {
        if (text.Length == 0) return 1;

        var lines = 1;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                lines++;
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
            }
            else if (text[i] == '\n')
            {
                lines++;
            }
        }

        // A file ending in a newline has no empty final line.
        if (text[^1] is '\n' or '\r') lines--;
        return lines;
    }

    public static string ContentHash(byte[] bytes) => Ids.Sha256Hex(bytes);
}
