using System.Text.RegularExpressions;

namespace ScreenFlow.UnityPos.Resources;

/// <summary>
/// Turns displayed text into its plain form (02 §9.9).
/// <para>
/// Screen text in this project carries TextMeshPro markup — <c>&lt;line-height=48.0&gt;</c>,
/// <c>&lt;align=center&gt;</c>, <c>&lt;br&gt;</c> — and a reader comparing "what does this screen
/// say" against the source needs the words without the markup. The raw form is kept alongside, so
/// nothing is lost.
/// </para>
/// </summary>
public static class RichText
{
    /// <summary><c>&lt;br&gt;</c> in any of its spellings; it is a line break, not a tag to drop.</summary>
    private static readonly Regex BreakTag = new(
        @"<\s*br\s*/?\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Any remaining markup tag. A lone <c>&lt;</c> with no closing <c>&gt;</c> is text.</summary>
    private static readonly Regex AnyTag = new(@"<[^<>]*>", RegexOptions.CultureInvariant);

    /// <summary>
    /// The plain form: <c>&lt;br&gt;</c> becomes a line break, every other tag is removed, and only
    /// the outer whitespace is trimmed — inner spacing is part of the text (02 §9.9).
    /// </summary>
    public static string Plain(string raw)
    {
        if (raw.Length == 0) return raw;

        var text = BreakTag.Replace(raw, "\n");
        text = AnyTag.Replace(text, string.Empty);

        return text.Trim();
    }

    public static string? PlainOrNull(string? raw) => raw is null ? null : Plain(raw);

    /// <summary>True when the text carries markup, so a report can say the raw form differs.</summary>
    public static bool HasMarkup(string raw) => BreakTag.IsMatch(raw) || AnyTag.IsMatch(raw);
}
