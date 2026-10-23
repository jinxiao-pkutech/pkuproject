using System.Text;
using System.Text.RegularExpressions;

namespace ScreenFlow.CSharp.Compilation;

/// <summary>A response file includes itself, directly or through a chain of <c>@</c> references.</summary>
public sealed class RspCycleException : Exception
{
    public RspCycleException(IReadOnlyList<string> chain)
        : base("Response file include cycle: " + string.Join(" -> ", chain))
        => Chain = chain;

    public IReadOnlyList<string> Chain { get; }
}

/// <summary>What one response file says, before any profile override is applied.</summary>
public sealed record ParsedRsp
{
    public required string Path { get; init; }

    public string? OutPath { get; init; }

    public IReadOnlyList<string> Sources { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> References { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Defines { get; init; } = Array.Empty<string>();

    public string? LangVersion { get; init; }

    public bool AllowUnsafe { get; init; }

    public string? Nullable { get; init; }

    public string? Target { get; init; }

    public IReadOnlyList<string> Analyzers { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> IgnoredOptions { get; init; } = Array.Empty<string>();

    /// <summary>Response files pulled in with <c>@</c>, in the order they were read.</summary>
    public IReadOnlyList<string> Included { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Parses a Unity compile-cache response file (02 §6.1, 03 §2).
/// <para>
/// Parsing is deliberately lenient: an option this harness does not act on is recorded in
/// <see cref="ParsedRsp.IgnoredOptions"/> rather than rejected. The real cache is produced by a
/// Unity version nobody here can inspect, so refusing an unfamiliar option would turn a harmless
/// difference into a failed build.
/// </para>
/// </summary>
public static class RspParser
{
    /// <summary>Guards against a pathological include chain even when it is not a cycle.</summary>
    public const int MaxIncludeDepth = 32;

    /// <summary>A <c>UNITY_yyyy_m_p</c> define, which is how the editor version is inferred.</summary>
    private static readonly Regex UnityVersionDefine =
        new(@"^UNITY_(?<year>\d{4})_(?<minor>\d+)_(?<patch>\d+)$", RegexOptions.Compiled);

    public static ParsedRsp Parse(string rspPath, string projectRoot)
    {
        var lines = new List<string>();
        var included = new List<string>();
        ReadInto(rspPath, projectRoot, lines, included, new List<string>());

        var sources = new List<string>();
        var references = new List<string>();
        var defines = new List<string>();
        var analyzers = new List<string>();
        var ignored = new List<string>();

        string? outPath = null;
        string? langVersion = null;
        string? nullable = null;
        string? target = null;
        var allowUnsafe = false;

        foreach (var raw in lines)
        {
            var line = Unquote(raw.Trim());
            if (line.Length == 0) continue;

            if (!IsOption(line))
            {
                // A non-option line ending in .cs is a source; anything else is not ours to guess at.
                if (line.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                    sources.Add(Resolve(projectRoot, line));
                else
                    ignored.Add(line);
                continue;
            }

            var (name, value) = SplitOption(line);

            switch (name)
            {
                case "out":
                    outPath = Resolve(projectRoot, Unquote(value));
                    break;

                case "define":
                case "d":
                    // One option may carry several symbols separated by ';'.
                    foreach (var symbol in Unquote(value).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        defines.Add(symbol);
                    break;

                case "r":
                case "reference":
                    references.Add(Resolve(projectRoot, StripAlias(Unquote(value))));
                    break;

                case "langversion":
                    langVersion = Unquote(value);
                    break;

                case "unsafe":
                    // Written as -unsafe, -unsafe+ or -unsafe-.
                    allowUnsafe = !value.EndsWith("-", StringComparison.Ordinal);
                    break;

                case "nullable":
                    nullable = value.Length == 0 ? "enable" : Unquote(value);
                    break;

                case "analyzer":
                    analyzers.Add(Resolve(projectRoot, Unquote(value)));
                    break;

                case "target":
                    target = Unquote(value);
                    break;

                default:
                    ignored.Add(line);
                    break;
            }
        }

        return new ParsedRsp
        {
            Path = Path.GetFullPath(rspPath).Replace('\\', '/'),
            OutPath = outPath,
            Sources = Distinct(sources),
            References = Distinct(references),
            Defines = defines.Distinct(StringComparer.Ordinal).OrderBy(d => d, StringComparer.Ordinal).ToList(),
            LangVersion = langVersion,
            AllowUnsafe = allowUnsafe,
            Nullable = nullable,
            Target = target,
            Analyzers = Distinct(analyzers),
            IgnoredOptions = ignored,
            Included = included
        };
    }

    /// <summary>
    /// Reads a response file and everything it pulls in with <c>@</c>. A file already on the current
    /// chain is a cycle and fails; the same file reached twice on different branches is not.
    /// </summary>
    private static void ReadInto(
        string rspPath, string projectRoot, List<string> sink, List<string> included, List<string> chain)
    {
        var full = Path.GetFullPath(rspPath).Replace('\\', '/');

        if (chain.Contains(full, StringComparer.OrdinalIgnoreCase))
            throw new RspCycleException(chain.Append(full).ToList());

        if (chain.Count >= MaxIncludeDepth)
            throw new RspCycleException(chain.Append(full).ToList());

        if (!File.Exists(full))
            throw new FileNotFoundException($"Response file not found: {rspPath}", full);

        chain.Add(full);
        if (chain.Count > 1) included.Add(full);

        var directory = Path.GetDirectoryName(full) ?? ".";

        foreach (var raw in File.ReadAllLines(full, new UTF8Encoding(false)))
        {
            var line = raw.Trim();
            if (!line.StartsWith('@'))
            {
                sink.Add(raw);
                continue;
            }

            // An include path is relative to the project root when rooted there, otherwise to the
            // including file; both forms occur in practice, so both are tried.
            var nested = Unquote(line[1..]);
            var candidates = new[]
            {
                Path.IsPathRooted(nested) ? nested : Path.Combine(projectRoot, nested),
                Path.Combine(directory, nested)
            };

            var resolved = candidates.FirstOrDefault(File.Exists) ?? candidates[0];
            ReadInto(resolved, projectRoot, sink, included, chain);
        }

        chain.RemoveAt(chain.Count - 1);
    }

    /// <summary>Option prefixes <c>-</c> and <c>/</c> are equivalent (02 §6.1).</summary>
    internal static bool IsOption(string line)
    {
        if (line.Length < 2) return false;
        if (line[0] != '-' && line[0] != '/') return false;

        // A bare path starting with '/' on a POSIX-style layout is not an option.
        return line[0] != '/' || !line.Contains('/', StringComparison.Ordinal) || !line.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Splits <c>-name:value</c>. Option names are compared case-insensitively.</summary>
    internal static (string Name, string Value) SplitOption(string line)
    {
        var body = line[1..];
        var separator = body.IndexOf(':');
        if (separator < 0) return (body.ToLowerInvariant(), string.Empty);
        return (body[..separator].ToLowerInvariant(), body[(separator + 1)..]);
    }

    /// <summary>A reference may be written as <c>alias=path</c>; only the path is of interest.</summary>
    internal static string StripAlias(string reference)
    {
        var equals = reference.IndexOf('=');
        if (equals <= 0) return reference;

        // A drive letter is not an alias.
        var head = reference[..equals];
        return head.Contains('/') || head.Contains('\\') || head.Contains(':') ? reference : reference[(equals + 1)..];
    }

    internal static string Unquote(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"'
            ? trimmed[1..^1]
            : trimmed;
    }

    internal static string Resolve(string projectRoot, string path)
    {
        var cleaned = path.Replace('\\', '/').Trim();
        if (cleaned.Length == 0) return cleaned;
        var full = Path.IsPathRooted(cleaned) ? cleaned : Path.Combine(projectRoot, cleaned);
        return Path.GetFullPath(full).Replace('\\', '/');
    }

    /// <summary>
    /// The Unity version a define set implies (02 §6.2). Several <c>UNITY_*</c> version defines can
    /// be present at once, so the most specific one wins.
    /// </summary>
    public static string? UnityVersionFrom(IEnumerable<string> defines)
    {
        var best = defines
            .Select(d => UnityVersionDefine.Match(d))
            .Where(m => m.Success)
            .OrderByDescending(m => m.Value.Length)
            .ThenByDescending(m => m.Value, StringComparer.Ordinal)
            .FirstOrDefault();

        return best is null
            ? null
            : $"{best.Groups["year"].Value}.{best.Groups["minor"].Value}.{best.Groups["patch"].Value}";
    }

    /// <summary>Platform from the defines, never from the directory name (03 §2).</summary>
    public static string PlatformFrom(IEnumerable<string> defines)
    {
        var set = defines.ToHashSet(StringComparer.Ordinal);
        if (set.Contains("UNITY_EDITOR")) return RspPlatforms.Editor;
        if (set.Contains("UNITY_ANDROID")) return RspPlatforms.Android;
        return RspPlatforms.Unknown;
    }

    private static List<string> Distinct(IEnumerable<string> values)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var value in values)
        {
            if (seen.Add(value)) result.Add(value);
        }
        return result;
    }
}
