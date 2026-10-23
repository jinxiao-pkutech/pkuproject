using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using ScreenFlow.Core.Config;
using ScreenFlow.Core.Ir;
using ScreenFlow.CSharp.Compilation;

namespace ScreenFlow.CSharp.Walkers;

/// <summary>
/// Extracts change markers from comments (02 §5.8, §6.7; 03 §1.1).
/// <para>
/// A marker is reporting metadata and never a parsing fact: it says who touched a line and when, so
/// a reviewer can find the ticket. The date is stored exactly as written — the real project contains
/// <c>2025.15.04</c> — because correcting it would misreport what the file says.
/// </para>
/// </summary>
public sealed class MarkerScanner
{
    /// <summary>First date in the remainder of the marker (02 §6.7); the format is not uniform.</summary>
    private static readonly Regex DatePattern = new(
        @"\d{4}\.\d{1,2}\.\d{1,2}", RegexOptions.CultureInvariant);

    /// <summary>An author is a mail-like token, and only when it directly follows the date.</summary>
    private static readonly Regex AuthorPattern = new(
        @"^\S+@\S+$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Issue reference forms of 03 §1.1, longest form first so that <c>gitID#972</c> is kept whole
    /// instead of being reduced to <c>#972</c>.
    /// <para>
    /// The prefixed form is narrower than 02 §6.7's <c>[^\s\]]+#\d+</c>: brackets and sentence
    /// punctuation cannot be part of a reference, or <c>…STEP3対応：#8051</c> would be read as one
    /// long reference instead of the <c>#8051</c> that 03 §1.1 documents. Both documented prefixed
    /// forms — <c>gitID#972</c> and <c>総合(3)#2995</c> — still match whole.
    /// </para>
    /// </summary>
    private static readonly Regex IssuePattern = new(
        @"GSEJPOS\d+-\d+|T\d{3}-\d+|[^\s\[\]：:,，、。]+#\d+|#\d+", RegexOptions.CultureInvariant);

    private static readonly SyntaxKind[] ScannedTrivia =
    {
        SyntaxKind.SingleLineCommentTrivia,
        SyntaxKind.MultiLineCommentTrivia,
        SyntaxKind.SingleLineDocumentationCommentTrivia,
        SyntaxKind.MultiLineDocumentationCommentTrivia,

        // A marker inside an inactive #if region is still a marker in the file, and which regions are
        // active depends on the profile; excluding them would make the marker set profile-dependent.
        SyntaxKind.DisabledTextTrivia
    };

    private readonly Regex _pattern;

    public MarkerScanner(ChangeMarkersConfig config)
        => _pattern = new Regex(config.Pattern, RegexOptions.CultureInvariant);

    public IReadOnlyList<MarkerRec> Scan(CompiledAssembly assembly, CancellationToken ct = default)
    {
        var byFile = new ConcurrentDictionary<string, IReadOnlyList<MarkerRec>>(StringComparer.Ordinal);

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2),
            CancellationToken = ct
        };

        Parallel.ForEach(assembly.Compilation.SyntaxTrees.ToList(), options, tree =>
        {
            var path = assembly.TreePaths.TryGetValue(tree, out var relative)
                ? relative
                : Ids.Normalize(tree.FilePath);

            byFile[path] = ScanTree(path, tree, ct);
        });

        return byFile.Values
            .SelectMany(markers => markers)
            .OrderBy(m => m.File, StringComparer.Ordinal)
            .ThenBy(m => m.Line)
            .ToList();
    }

    public IReadOnlyList<MarkerRec> ScanTree(string file, SyntaxTree tree, CancellationToken ct = default)
    {
        var markers = new List<MarkerRec>();
        var seen = new HashSet<int>();

        foreach (var trivia in tree.GetRoot(ct).DescendantTrivia(descendIntoTrivia: true))
        {
            ct.ThrowIfCancellationRequested();
            if (!ScannedTrivia.Contains(trivia.Kind())) continue;

            var firstLine = tree.GetLineSpan(trivia.Span).StartLinePosition.Line;
            var lines = trivia.ToString().Split('\n');

            for (var i = 0; i < lines.Length; i++)
            {
                var line = firstLine + i + 1;
                var marker = ParseLine(file, line, lines[i].TrimEnd('\r'));

                if (marker is not null && seen.Add(line)) markers.Add(marker);
            }
        }

        return markers.OrderBy(m => m.Line).ToList();
    }

    /// <summary>One comment line, or null when it is not a change marker.</summary>
    public MarkerRec? ParseLine(string file, int line, string text)
    {
        var match = _pattern.Match(text);
        if (!match.Success) return null;

        var rest = match.Groups["rest"].Value;

        return new MarkerRec
        {
            File = file,
            Line = line,
            Dir = match.Groups["dir"].Value,
            Kind = match.Groups["kind"].Value,
            Date = DateOf(rest),
            Author = AuthorOf(rest),
            IssueRefs = IssueRefsOf(rest),
            Text = text.Trim()
        };
    }

    public static string? DateOf(string rest)
    {
        var match = DatePattern.Match(rest);
        return match.Success ? match.Value : null;
    }

    /// <summary>
    /// The token directly after the date, when it looks like a mail address (02 §6.7). Anything else
    /// is left unattributed rather than guessed at from the surrounding words.
    /// </summary>
    public static string? AuthorOf(string rest)
    {
        var date = DatePattern.Match(rest);
        if (!date.Success) return null;

        var after = rest[(date.Index + date.Length)..];
        var token = after.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

        return token is not null && AuthorPattern.IsMatch(token) ? token : null;
    }

    public static IReadOnlyList<string> IssueRefsOf(string rest)
    {
        var refs = new List<string>();

        foreach (var match in IssuePattern.Matches(rest).Cast<Match>())
        {
            // Source order, de-duplicated: the order a line lists its tickets in is part of how the
            // line reads, so it is kept.
            if (!refs.Contains(match.Value, StringComparer.Ordinal)) refs.Add(match.Value);
        }

        return refs;
    }
}
