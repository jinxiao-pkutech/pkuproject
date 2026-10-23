using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using ScreenFlow.Core.Config;
using ScreenFlow.Core.Ir;
using ScreenFlow.Core.Storage;

namespace ScreenFlow.UnityPos.Anchors;

/// <summary>What an anchor resolved to, or why it did not (02 §4.2).</summary>
public sealed record AnchorResolution
{
    public required AnchorSpec Anchor { get; init; }

    public IReadOnlyList<SymbolRec> Symbols { get; init; } = Array.Empty<SymbolRec>();

    /// <summary>The types the type part matched, kept for a member anchor's diagnostics.</summary>
    public IReadOnlyList<SymbolRec> Types { get; init; } = Array.Empty<SymbolRec>();

    /// <summary><see cref="AnchorResolver.UnresolvedCode"/> or <see cref="AnchorResolver.AmbiguousCode"/>.</summary>
    public string? ErrorCode { get; init; }

    public string? Message { get; init; }

    public bool Resolved => ErrorCode is null;

    /// <summary>The single symbol, for the shapes that require uniqueness.</summary>
    public SymbolRec? Single => Symbols.Count == 1 ? Symbols[0] : null;

    public SymbolRec Require()
        => Single ?? throw new InvalidOperationException(
            Message ?? $"anchor '{Anchor.Describe()}' did not resolve to exactly one symbol.");
}

/// <summary>
/// Resolves the anchors of <c>config/unitypos.json</c> against the index (02 §4.2).
/// <para>
/// Anchors exist so that no POS type or member name is written into this assembly's code, and they
/// are matched by (name, file), glob or fully qualified name — never by simple name alone. The real
/// project has 125 duplicate simple type names, so a global name search would silently anchor an
/// adapter to the wrong type (CLAUDE.md §4).
/// </para>
/// </summary>
public sealed class AnchorResolver
{
    public const string UnresolvedCode = "E_ANCHOR_UNRESOLVED";
    public const string AmbiguousCode = "E_ANCHOR_AMBIGUOUS";

    private readonly IndexReader _reader;
    private readonly ConcurrentDictionary<string, AnchorResolution> _cache = new(StringComparer.Ordinal);
    private readonly Lazy<IReadOnlyList<SymbolRec>> _types;

    public AnchorResolver(IndexReader reader)
    {
        _reader = reader;
        _types = new Lazy<IReadOnlyList<SymbolRec>>(
            () => reader.SymbolsByKind(SymbolKinds.Type).Where(s => s.IsSource).ToList());
    }

    /// <summary>Every source type in the index, read once.</summary>
    public IReadOnlyList<SymbolRec> SourceTypes => _types.Value;

    public AnchorResolution Resolve(AnchorSpec anchor)
        => _cache.GetOrAdd(Key(anchor), _ => ResolveCore(anchor));

    private AnchorResolution ResolveCore(AnchorSpec anchor)
    {
        var problems = anchor.Validate("anchor");
        if (problems.Count > 0)
        {
            return new AnchorResolution
            {
                Anchor = anchor,
                ErrorCode = UnresolvedCode,
                Message = $"Anchor '{Describe(anchor)}' is malformed: {string.Join("; ", problems)}"
            };
        }

        return anchor.Kind switch
        {
            AnchorKind.TypeSet => ResolveTypeSet(anchor),
            AnchorKind.Member => ResolveMember(anchor),
            _ => ResolveType(anchor)
        };
    }

    private AnchorResolution ResolveType(AnchorSpec anchor)
    {
        var matches = MatchTypes(anchor);

        if (matches.Count == 0)
        {
            return new AnchorResolution
            {
                Anchor = anchor,
                ErrorCode = UnresolvedCode,
                Message = $"No type matches anchor '{Describe(anchor)}'."
            };
        }

        if (matches.Count > 1 && anchor.RequiresUniqueType)
        {
            return new AnchorResolution
            {
                Anchor = anchor,
                ErrorCode = AmbiguousCode,
                Message =
                    $"Anchor '{Describe(anchor)}' matches {matches.Count} types: " +
                    string.Join(", ", matches.Take(5).Select(t => $"{t.FqName} ({t.File})")) +
                    (matches.Count > 5 ? ", …" : string.Empty)
            };
        }

        return new AnchorResolution { Anchor = anchor, Symbols = matches, Types = matches };
    }

    private AnchorResolution ResolveMember(AnchorSpec anchor)
    {
        var types = MatchTypes(anchor);

        if (types.Count == 0)
        {
            return new AnchorResolution
            {
                Anchor = anchor,
                ErrorCode = UnresolvedCode,
                Message = $"No type matches the type part of anchor '{Describe(anchor)}'."
            };
        }

        if (types.Count > 1 && anchor.RequiresUniqueType)
        {
            return new AnchorResolution
            {
                Anchor = anchor,
                ErrorCode = AmbiguousCode,
                Message =
                    $"The type part of anchor '{Describe(anchor)}' matches {types.Count} types: " +
                    string.Join(", ", types.Take(5).Select(t => $"{t.FqName} ({t.File})"))
            };
        }

        var members = types
            .SelectMany(type => MembersOf(type, anchor.Member!))
            .Distinct()
            .OrderBy(m => m.Id, StringComparer.Ordinal)
            .ToList();

        if (members.Count == 0)
        {
            return new AnchorResolution
            {
                Anchor = anchor,
                Types = types,
                ErrorCode = UnresolvedCode,
                Message =
                    $"Anchor '{Describe(anchor)}' found the type but no member '{anchor.Member}' " +
                    "in it or in its source base types."
            };
        }

        // Every same-named member counts, overloads included (02 §4.2), so several is not ambiguous.
        return new AnchorResolution { Anchor = anchor, Symbols = members, Types = types };
    }

    private AnchorResolution ResolveTypeSet(AnchorSpec anchor)
    {
        var pattern = new Regex(anchor.TypeNamePattern!, RegexOptions.CultureInvariant);

        var matches = SourceTypes
            .Where(type => anchor.Namespace is null || type.Namespace == anchor.Namespace)
            .Where(type => anchor.Dir is null || InDirectory(type.File, anchor.Dir))
            .Where(type => anchor.TypeKind is null || type.TypeKind == anchor.TypeKind)
            .Where(type => pattern.IsMatch(type.Name))
            .OrderBy(type => type.Id, StringComparer.Ordinal)
            .ToList();

        // A type set may legitimately be empty in a trimmed project, but an adapter that depends on
        // it then has nothing to work with, so it is reported like any other unresolved anchor.
        return matches.Count == 0
            ? new AnchorResolution
            {
                Anchor = anchor,
                ErrorCode = UnresolvedCode,
                Message = $"No type matches the type-set anchor '{Describe(anchor)}'."
            }
            : new AnchorResolution { Anchor = anchor, Symbols = matches, Types = matches };
    }

    private IReadOnlyList<SymbolRec> MatchTypes(AnchorSpec anchor) => anchor.TypePartKind switch
    {
        AnchorKind.TypeByFqName => SourceTypes
            .Where(type => string.Equals(type.FqName, anchor.FqName, StringComparison.Ordinal))
            .OrderBy(type => type.Id, StringComparer.Ordinal)
            .ToList(),

        AnchorKind.TypeInFile => ByName(anchor.Type!)
            .Where(type => DeclaredIn(type, anchor.File!))
            .OrderBy(type => type.Id, StringComparer.Ordinal)
            .ToList(),

        AnchorKind.TypeInFileGlob => ByName(anchor.Type!)
            .Where(type => DeclaredInGlob(type, anchor.FileGlob!))
            .OrderBy(type => type.Id, StringComparer.Ordinal)
            .ToList(),

        AnchorKind.AnyTypeInFileGlob => SourceTypes
            .Where(type => DeclaredInGlob(type, anchor.FileGlob!))
            .OrderBy(type => type.Id, StringComparer.Ordinal)
            .ToList(),

        _ => Array.Empty<SymbolRec>()
    };

    /// <summary>
    /// Members of the type and of its source base types (02 §4.2). The base chain matters because
    /// the POS views inherit their control fields from a base screen (03 §7).
    /// </summary>
    public IReadOnlyList<SymbolRec> MembersOf(SymbolRec type, string member)
    {
        var members = new List<SymbolRec>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var current = type; current is not null; current = BaseOf(current))
        {
            if (!seen.Add(current.Id)) break;

            foreach (var candidate in _reader.SymbolsInContainer(current.Id))
            {
                if (candidate.Kind == SymbolKinds.Type) continue;
                if (member != AnchorSpec.Wildcard && !string.Equals(candidate.Name, member, StringComparison.Ordinal))
                    continue;

                members.Add(candidate);
            }
        }

        return members;
    }

    public SymbolRec? BaseOf(SymbolRec type)
        => type.BaseTypeId is null ? null : _reader.Symbol(type.BaseTypeId);

    /// <summary>True when the type is declared in that exact file, in any of its declarations.</summary>
    private static bool DeclaredIn(SymbolRec type, string file)
        => Paths(type).Any(path => string.Equals(path, Normalize(file), StringComparison.OrdinalIgnoreCase));

    private static bool DeclaredInGlob(SymbolRec type, string glob)
    {
        var pattern = GlobRegex(glob);
        return Paths(type).Any(path => pattern.IsMatch(path));
    }

    private static IEnumerable<string> Paths(SymbolRec type)
    {
        if (type.File is not null) yield return Normalize(type.File);
        foreach (var declaration in type.Declarations) yield return Normalize(declaration.File);
    }

    private static bool InDirectory(string? file, string directory)
    {
        if (file is null) return false;

        var normalized = Normalize(directory).TrimEnd('/');
        return Normalize(file).StartsWith(normalized + "/", StringComparison.OrdinalIgnoreCase);
    }

    private IEnumerable<SymbolRec> ByName(string name)
        => _reader.SymbolsByName(name).Where(s => s.Kind == SymbolKinds.Type && s.IsSource);

    private static readonly ConcurrentDictionary<string, Regex> GlobCache = new(StringComparer.Ordinal);

    /// <summary>
    /// A glob over project-relative paths: <c>**</c> crosses directories, <c>*</c> does not, and
    /// <c>?</c> is one character (02 §4.2).
    /// </summary>
    public static Regex GlobRegex(string glob) => GlobCache.GetOrAdd(glob, pattern =>
    {
        var text = new StringBuilder("^");

        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];

            switch (c)
            {
                case '*' when i + 1 < pattern.Length && pattern[i + 1] == '*':
                    text.Append(".*");
                    i++;
                    if (i + 1 < pattern.Length && pattern[i + 1] == '/') i++;
                    break;
                case '*':
                    text.Append("[^/]*");
                    break;
                case '?':
                    text.Append("[^/]");
                    break;
                case '/':
                    text.Append('/');
                    break;
                default:
                    text.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }

        text.Append('$');
        return new Regex(text.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    });

    private static string Normalize(string path) => path.Replace('\\', '/');

    private static string Describe(AnchorSpec anchor)
    {
        try
        {
            return anchor.Describe();
        }
        catch (InvalidOperationException)
        {
            return "(invalid anchor)";
        }
    }

    private static string Key(AnchorSpec anchor) => string.Join(
        '|',
        anchor.Type,
        anchor.File,
        anchor.FileGlob,
        anchor.FqName,
        anchor.Member,
        anchor.Namespace,
        anchor.Dir,
        anchor.TypeNamePattern,
        anchor.TypeKind);
}
