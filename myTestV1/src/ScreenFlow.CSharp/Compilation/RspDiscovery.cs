using ScreenFlow.Core.Config;
using ScreenFlow.Core.Ir;

namespace ScreenFlow.CSharp.Compilation;

/// <summary>
/// No usable response file was found. The CLI maps this onto <c>E_NO_COMPILATION_INPUT</c>
/// (02 §11.3). The message says what the user has to do, because without a Unity compile cache
/// there is nothing this harness can analyse and inventing one would be worse than failing.
/// </summary>
public sealed class NoCompilationInputException : Exception
{
    public const string ErrorCode = "E_NO_COMPILATION_INPUT";

    public NoCompilationInputException(string projectRoot, string rspRoot, string detail)
        : base($"{ErrorCode}: no usable Unity compile input under '{rspRoot}' in '{projectRoot}'. {detail} " +
               "Open the project in Unity 2022.3.68f1 and compile it; for the Android profile, run an " +
               "Android Player build once so the Android compile cache exists.")
    {
        ProjectRoot = projectRoot;
        RspRoot = rspRoot;
    }

    public string ProjectRoot { get; }
    public string RspRoot { get; }
}

/// <summary>What discovery and preflight concluded (T06 §5).</summary>
public sealed record PreflightResult
{
    public IReadOnlyList<AssemblyInput> Inputs { get; init; } = Array.Empty<AssemblyInput>();

    public IReadOnlyList<DiagRec> Diagnostics { get; init; } = Array.Empty<DiagRec>();

    /// <summary><c>native</c> or <c>derived</c> (02 §4.1).</summary>
    public required string Derivation { get; init; }

    public required string PlatformDetected { get; init; }

    /// <summary>Version from <c>ProjectSettings/ProjectVersion.txt</c>.</summary>
    public string? UnityDeclared { get; init; }

    /// <summary>Identity of the cache the selected response files came from.</summary>
    public string? UnityCache { get; init; }

    /// <summary>Assemblies IR is extracted from (02 §4.1).</summary>
    public IReadOnlyList<AssemblyInput> ExtractedInputs => Inputs.Where(i => i.ExtractIr).ToList();

    /// <summary>
    /// Every source of every selected assembly, which is what the staleness fingerprint covers
    /// (02 §7.6): a change in a reference-only assembly can change what binds, so it counts.
    /// </summary>
    public IReadOnlyList<string> AllSources => Inputs.SelectMany(i => i.Sources)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(s => s, StringComparer.Ordinal)
        .ToList();
}

/// <summary>
/// Finds the Unity compile cache, picks the response files the profile asks for, and reports every
/// preflight condition of 02 §6.2.
/// </summary>
public static class RspDiscovery
{
    public const string ProjectVersionRelativePath = "ProjectSettings/ProjectVersion.txt";

    public const string AssetsDirectoryName = "Assets";

    public static PreflightResult Discover(string projectRoot, ProfileConfig profile)
    {
        var root = Path.GetFullPath(projectRoot);
        var diagnostics = new List<DiagRec>();

        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Project root does not exist: {projectRoot}");

        var unityDeclared = ReadDeclaredUnityVersion(root, diagnostics);
        var rspRoot = Path.Combine(root, profile.Rsp.Root.Replace('/', Path.DirectorySeparatorChar));

        var candidates = EnumerateRspFiles(rspRoot, profile, diagnostics);
        if (candidates.Count == 0)
        {
            throw new NoCompilationInputException(
                projectRoot, profile.Rsp.Root,
                Directory.Exists(rspRoot)
                    ? "The directory exists but holds no response file that is a compilation input."
                    : "The directory does not exist.");
        }

        var parsed = ParseAll(candidates, root, diagnostics);
        if (parsed.Count == 0)
        {
            throw new NoCompilationInputException(
                projectRoot, profile.Rsp.Root,
                $"{candidates.Count} response file(s) were read but none names an output assembly and sources.");
        }

        var (selected, platformDetected, derivation) = Select(parsed, profile, diagnostics);
        if (selected.Count == 0)
        {
            throw new NoCompilationInputException(
                projectRoot, profile.Rsp.Root,
                $"No response file matches the profile's platform '{profile.Rsp.SelectPlatform}'.");
        }

        // Every selected assembly is kept: the ones outside assemblies.include are compiled as
        // references so their symbols still bind (02 §4.1). Only IR extraction is restricted.
        var inputs = selected
            .Select(p => ToInput(p, profile, derivation))
            .Select(i => i with { ExtractIr = IncludedByProfile(i, profile, diagnostics) })
            .OrderBy(i => i.Name, StringComparer.Ordinal)
            .ToList();

        if (inputs.All(i => !i.ExtractIr))
        {
            throw new NoCompilationInputException(
                projectRoot, profile.Rsp.Root,
                $"Every assembly was excluded from extraction by the profile's assembly filter " +
                $"(include: {string.Join(", ", profile.Assemblies.Include)}).");
        }

        AddPreflightDiagnostics(root, inputs, unityDeclared, diagnostics);

        return new PreflightResult
        {
            Inputs = inputs,
            Diagnostics = diagnostics,
            Derivation = derivation,
            PlatformDetected = platformDetected,
            UnityDeclared = unityDeclared,
            UnityCache = CacheTagOf(selected)
        };
    }

    // ---------------------------------------------------------------- discovery

    internal static IReadOnlyList<string> EnumerateRspFiles(
        string rspRoot, ProfileConfig profile, List<DiagRec> diagnostics)
    {
        if (!Directory.Exists(rspRoot)) return Array.Empty<string>();

        var all = Directory.EnumerateFiles(rspRoot, "*.rsp", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        var kept = new List<string>();
        foreach (var path in all)
        {
            var name = Path.GetFileName(path);
            var excludedBy = profile.Rsp.ExcludeNamePatterns
                .FirstOrDefault(pattern => name.Contains(pattern, StringComparison.OrdinalIgnoreCase));

            if (excludedBy is not null)
            {
                diagnostics.Add(DiagRec.Info(
                    "I_RSP_EXCLUDED",
                    $"'{name}' is excluded by the profile pattern '{excludedBy}'; it is not a compilation input."));
                continue;
            }

            kept.Add(path);
        }

        return kept;
    }

    private static IReadOnlyList<ParsedRsp> ParseAll(
        IReadOnlyList<string> paths, string projectRoot, List<DiagRec> diagnostics)
    {
        var parsed = new List<ParsedRsp>();

        foreach (var path in paths)
        {
            ParsedRsp candidate;
            try
            {
                candidate = RspParser.Parse(path, projectRoot);
            }
            catch (RspCycleException ex)
            {
                diagnostics.Add(DiagRec.Error("E_RSP_CYCLE", ex.Message, Relative(projectRoot, path)));
                continue;
            }
            catch (Exception ex) when (ex is IOException or FileNotFoundException or UnauthorizedAccessException)
            {
                diagnostics.Add(DiagRec.Error("E_RSP_UNREADABLE",
                    $"'{Path.GetFileName(path)}' could not be read: {ex.Message}", Relative(projectRoot, path)));
                continue;
            }

            // 02 §6.1: a response file with no output assembly or no sources is not a compilation
            // input. Reported, not silently skipped.
            if (candidate.OutPath is null)
            {
                diagnostics.Add(DiagRec.Info("I_RSP_NOT_INPUT",
                    $"'{Path.GetFileName(path)}' names no -out: assembly; it is not a compilation input.",
                    Relative(projectRoot, path)));
                continue;
            }

            if (candidate.Sources.Count == 0)
            {
                diagnostics.Add(DiagRec.Info("I_RSP_NOT_INPUT",
                    $"'{Path.GetFileName(path)}' lists no .cs sources; it is not a compilation input.",
                    Relative(projectRoot, path)));
                continue;
            }

            parsed.Add(candidate);
        }

        return parsed;
    }

    // ---------------------------------------------------------------- selection

    /// <summary>
    /// Picks the response files the profile's platform asks for (02 §4.1, §6.1).
    /// <para>
    /// When nothing matches but something else was found, the index is still built from what exists
    /// and the snapshot is marked <c>derived</c>, with a diagnostic. That is the honest outcome: the
    /// facts are real, they just came from a cache built for another platform.
    /// </para>
    /// </summary>
    internal static (IReadOnlyList<ParsedRsp> Selected, string PlatformDetected, string Derivation) Select(
        IReadOnlyList<ParsedRsp> parsed, ProfileConfig profile, List<DiagRec> diagnostics)
    {
        var wanted = profile.Rsp.SelectPlatform;

        var byPlatform = parsed
            .GroupBy(p => RspParser.PlatformFrom(p.Defines), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ParsedRsp>)g.ToList(), StringComparer.Ordinal);

        IReadOnlyList<ParsedRsp> matching;
        var derivation = Core.Ir.Derivations.Native;

        if (wanted == RspConfig.Platforms.Any)
        {
            matching = parsed;
        }
        else if (byPlatform.TryGetValue(wanted, out var exact) && exact.Count > 0)
        {
            matching = exact;
        }
        else
        {
            // 02 §4.1: fall back to whatever cache exists, and say so.
            var fallbackPlatform = byPlatform.Keys.OrderBy(k => k, StringComparer.Ordinal).FirstOrDefault();
            if (fallbackPlatform is null) return (Array.Empty<ParsedRsp>(), RspPlatforms.Unknown, derivation);

            matching = byPlatform[fallbackPlatform];
            derivation = Core.Ir.Derivations.Derived;

            diagnostics.Add(DiagRec.Warning(
                "W_PLATFORM_DERIVED",
                $"Profile '{profile.Id}' asks for a '{wanted}' compile cache, but only '{fallbackPlatform}' was " +
                $"found. The index is built from it and every query result carries derivation=derived."));
        }

        // 02 §6.1: one assembly may be described by several response files.
        var selected = new List<ParsedRsp>();
        foreach (var group in matching
                     .GroupBy(p => AssemblyNameOf(p), StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var members = group.ToList();
            if (members.Count == 1)
            {
                selected.Add(members[0]);
                continue;
            }

            // selectPlatform filters; the profile's own declared platform breaks a tie. Without
            // that, a profile whose selectPlatform is "any" would pick between two equally sized
            // caches by path order, which has nothing to do with what the profile is for.
            var winner = members
                .OrderByDescending(m => string.Equals(
                    RspParser.PlatformFrom(m.Defines), profile.Platform, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(m => m.Sources.Count)
                .ThenBy(m => m.Path, StringComparer.Ordinal)
                .First();

            selected.Add(winner);
            diagnostics.Add(DiagRec.Warning(
                "W_DUPLICATE_ASSEMBLY_RSP",
                $"Assembly '{group.Key}' is described by {members.Count} response files; " +
                $"'{Path.GetFileName(winner.Path)}' was chosen because it lists the most sources " +
                $"({winner.Sources.Count}). Ignored: " +
                string.Join(", ", members.Where(m => m != winner).Select(m => Path.GetFileName(m.Path)))));
        }

        // The platform reported is the one the selected files actually came from: that is what the
        // index was built from, which is the only thing a reader can act on.
        var selectedPlatforms = selected
            .Select(p => RspParser.PlatformFrom(p.Defines))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var platformDetected = selectedPlatforms.Count == 1 ? selectedPlatforms[0] : RspPlatforms.Unknown;

        return (selected, platformDetected, derivation);
    }

    private static string AssemblyNameOf(ParsedRsp parsed)
        => Path.GetFileNameWithoutExtension(parsed.OutPath ?? parsed.Path);

    private static AssemblyInput ToInput(ParsedRsp parsed, ProfileConfig profile, string derivation)
    {
        // 02 §4.1: remove always applies; add applies only to a derived snapshot.
        var defines = new SortedSet<string>(parsed.Defines, StringComparer.Ordinal);
        foreach (var symbol in profile.Defines.Remove) defines.Remove(symbol);
        if (derivation == Core.Ir.Derivations.Derived)
        {
            foreach (var symbol in profile.Defines.Add) defines.Add(symbol);
        }

        return new AssemblyInput
        {
            Name = AssemblyNameOf(parsed),
            OutPath = parsed.OutPath ?? string.Empty,
            Sources = parsed.Sources,
            References = parsed.References,
            Defines = defines.ToList(),
            LangVersion = parsed.LangVersion,
            AllowUnsafe = parsed.AllowUnsafe,
            Nullable = parsed.Nullable,
            // The platform is read from the response file's own defines, before the overrides.
            Platform = RspParser.PlatformFrom(parsed.Defines),
            RspPath = parsed.Path,
            IgnoredOptions = parsed.IgnoredOptions,
            UnityVersionFromDefines = RspParser.UnityVersionFrom(parsed.Defines)
        };
    }

    /// <summary>
    /// 02 §4.1: only assemblies in <c>include</c> have IR extracted. Everything else still takes
    /// part in compilation as a reference, which is handled in T07, so an excluded assembly is an
    /// info diagnostic rather than a loss.
    /// </summary>
    private static bool IncludedByProfile(AssemblyInput input, ProfileConfig profile, List<DiagRec> diagnostics)
    {
        if (profile.Assemblies.Exclude.Any(pattern => GlobMatch(input.Name, pattern)))
        {
            diagnostics.Add(DiagRec.Info("I_ASSEMBLY_EXCLUDED",
                $"Assembly '{input.Name}' is excluded by profile '{profile.Id}'."));
            return false;
        }

        if (profile.Assemblies.Include.Count == 0) return true;
        if (profile.Assemblies.Include.Any(pattern => GlobMatch(input.Name, pattern))) return true;

        diagnostics.Add(DiagRec.Info("I_ASSEMBLY_NOT_INCLUDED",
            $"Assembly '{input.Name}' is not in the profile's include list; it is used as a reference only."));
        return false;
    }

    /// <summary>Glob match supporting a leading and trailing <c>*</c>, which is all the config uses.</summary>
    internal static bool GlobMatch(string value, string pattern)
    {
        if (pattern.Length == 0) return false;
        if (pattern == "*") return true;

        var startsWild = pattern[0] == '*';
        var endsWild = pattern.Length > 1 && pattern[^1] == '*';
        var core = pattern.Trim('*');
        if (core.Length == 0) return true;

        return (startsWild, endsWild) switch
        {
            (true, true) => value.Contains(core, StringComparison.OrdinalIgnoreCase),
            (true, false) => value.EndsWith(core, StringComparison.OrdinalIgnoreCase),
            (false, true) => value.StartsWith(core, StringComparison.OrdinalIgnoreCase),
            _ => string.Equals(value, core, StringComparison.OrdinalIgnoreCase)
        };
    }

    // ---------------------------------------------------------------- preflight

    private static string? ReadDeclaredUnityVersion(string root, List<DiagRec> diagnostics)
    {
        var path = Path.Combine(root, ProjectVersionRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
        {
            diagnostics.Add(DiagRec.Info("I_NO_PROJECT_VERSION",
                $"{ProjectVersionRelativePath} is missing, so the declared Unity version is unknown.",
                ProjectVersionRelativePath));
            return null;
        }

        foreach (var line in File.ReadAllLines(path))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("m_EditorVersion:", StringComparison.Ordinal))
                return trimmed["m_EditorVersion:".Length..].Trim();
        }

        diagnostics.Add(DiagRec.Info("I_NO_PROJECT_VERSION",
            $"{ProjectVersionRelativePath} declares no m_EditorVersion.", ProjectVersionRelativePath));
        return null;
    }

    private static void AddPreflightDiagnostics(
        string root, IReadOnlyList<AssemblyInput> inputs, string? unityDeclared, List<DiagRec> diagnostics)
    {
        CheckUnityVersion(inputs, unityDeclared, diagnostics);
        CheckReferences(root, inputs, diagnostics);
        CheckSources(root, inputs, diagnostics);
        CheckSourcesNotInRsp(root, inputs, diagnostics);
        CheckEncodings(root, inputs, diagnostics);
    }

    private static void CheckUnityVersion(
        IReadOnlyList<AssemblyInput> inputs, string? unityDeclared, List<DiagRec> diagnostics)
    {
        var fromDefines = inputs
            .Select(i => i.UnityVersionFromDefines)
            .Where(v => v is not null)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();

        if (unityDeclared is null || fromDefines.Count == 0)
        {
            diagnostics.Add(DiagRec.Info("W_UNITY_VERSION_MISMATCH",
                "The Unity version could not be compared: " +
                (unityDeclared is null ? "ProjectVersion.txt declares none." : "no UNITY_yyyy_m_p define was found.")));
            return;
        }

        // ProjectVersion.txt reads like 2022.3.68f1; only the first three components are comparable.
        var declaredShort = string.Join(".", unityDeclared.Split('.').Take(3))
            .TrimEnd('f', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        var declaredNumeric = NumericPrefix(unityDeclared);

        if (fromDefines.Any(v => string.Equals(v, declaredNumeric, StringComparison.Ordinal))) return;

        diagnostics.Add(DiagRec.Warning("W_UNITY_VERSION_MISMATCH",
            $"ProjectVersion.txt declares '{unityDeclared}' but the compile cache implies " +
            $"{string.Join(", ", fromDefines)}. The cache may have been produced by another editor version."));
    }

    /// <summary>The first three dot-separated numeric components, e.g. 2022.3.68f1 to 2022.3.68.</summary>
    public static string NumericPrefix(string version)
    {
        var parts = version.Split('.');
        var numeric = new List<string>();
        foreach (var part in parts.Take(3))
        {
            var digits = new string(part.TakeWhile(char.IsDigit).ToArray());
            if (digits.Length == 0) break;
            numeric.Add(digits);
        }
        return string.Join(".", numeric);
    }

    private static void CheckReferences(string root, IReadOnlyList<AssemblyInput> inputs, List<DiagRec> diagnostics)
    {
        foreach (var input in inputs)
        {
            foreach (var reference in input.References.Where(r => !File.Exists(r)))
            {
                diagnostics.Add(DiagRec.Warning("W_REFERENCE_MISSING",
                    $"Assembly '{input.Name}' references '{Relative(root, reference)}', which is not on disk. " +
                    "Symbols that fail to bind are recorded with isCompilerCertain=false; no type is fabricated."));
            }
        }
    }

    private static void CheckSources(string root, IReadOnlyList<AssemblyInput> inputs, List<DiagRec> diagnostics)
    {
        foreach (var input in inputs)
        {
            foreach (var source in input.Sources.Where(s => !File.Exists(s)))
            {
                diagnostics.Add(DiagRec.Warning("W_SOURCE_MISSING",
                    $"Assembly '{input.Name}' lists source '{Relative(root, source)}', which is not on disk.",
                    Relative(root, source)));
            }
        }
    }

    /// <summary>
    /// Sources on disk that no selected response file mentions (02 §6.2). Such a file is simply not
    /// analysed, and saying so is the difference between an incomplete answer and a wrong one.
    /// </summary>
    private static void CheckSourcesNotInRsp(
        string root, IReadOnlyList<AssemblyInput> inputs, List<DiagRec> diagnostics)
    {
        var assets = Path.Combine(root, AssetsDirectoryName);
        if (!Directory.Exists(assets)) return;

        var listed = inputs
            .SelectMany(i => i.Sources)
            .Select(Normalize)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Both .cs and .CS, matched case-insensitively (T06 §4).
        var onDisk = Directory.EnumerateFiles(assets, "*.*", SearchOption.AllDirectories)
            .Where(p => Path.GetExtension(p).Equals(".cs", StringComparison.OrdinalIgnoreCase))
            .Select(Normalize)
            .Where(p => !listed.Contains(p))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        foreach (var orphan in onDisk)
        {
            diagnostics.Add(DiagRec.Info("I_SOURCE_NOT_IN_RSP",
                $"'{Relative(root, orphan)}' is on disk but in no selected response file, so it is not analysed.",
                Relative(root, orphan)));
        }
    }

    private static void CheckEncodings(string root, IReadOnlyList<AssemblyInput> inputs, List<DiagRec> diagnostics)
    {
        foreach (var source in inputs.SelectMany(i => i.Sources)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(s => s, StringComparer.Ordinal))
        {
            if (!File.Exists(source)) continue;

            var decoded = SourceEncoding.DecodeFile(source);
            if (decoded.EncodingName != Encodings.Cp932) continue;

            diagnostics.Add(DiagRec.Info("I_NON_UTF8",
                $"'{Relative(root, source)}' is not valid UTF-8 and was decoded as CP932.",
                Relative(root, source)));
        }
    }

    /// <summary>The cache identity: the <c>.dag</c> directory names the selected files came from.</summary>
    private static string? CacheTagOf(IReadOnlyList<ParsedRsp> selected)
    {
        var tags = selected
            .Select(p => Path.GetFileName(Path.GetDirectoryName(p.Path.Replace('/', Path.DirectorySeparatorChar)) ?? string.Empty))
            .Where(t => !string.IsNullOrEmpty(t))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

        return tags.Count == 0 ? null : string.Join(",", tags);
    }

    private static string Normalize(string path) => Path.GetFullPath(path).Replace('\\', '/');

    internal static string Relative(string root, string absolute)
    {
        var normalisedRoot = Path.GetFullPath(root).Replace('\\', '/').TrimEnd('/');
        var normalised = Normalize(absolute);
        return normalised.StartsWith(normalisedRoot + "/", StringComparison.OrdinalIgnoreCase)
            ? normalised[(normalisedRoot.Length + 1)..]
            : normalised;
    }
}
