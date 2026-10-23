using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using ScreenFlow.Core.Config;
using ScreenFlow.Core.Extraction;
using ScreenFlow.Core.Ir;

namespace ScreenFlow.CSharp.Compilation;

/// <summary>
/// Builds a <see cref="CSharpCompilation"/> per assembly (02 §6.4), in dependency order.
/// <para>
/// No type is ever injected to make something bind. A missing reference stays a diagnostic and the
/// symbols that depend on it are recorded as not compiler-certain, because a fabricated type would
/// produce facts about code that does not exist (01 anti-fabrication rules).
/// </para>
/// </summary>
public sealed class CompilationBuilder
{
    /// <summary>Compiler errors written out in detail per assembly; the count is always complete.</summary>
    public const int MaxReportedErrorsPerAssembly = 200;

    public const string CompileDiagnosticCode = "E_COMPILE";

    /// <summary>Assembly names that mean the response file already references a BCL.</summary>
    private static readonly string[] BclMarkers = { "mscorlib", "netstandard", "System.Runtime", "System.Private.CoreLib" };

    private readonly List<DiagRec> _diagnostics = new();
    private readonly Dictionary<string, MetadataReference> _metadataCache = new(StringComparer.OrdinalIgnoreCase);

    public CompilationSet Build(
        string projectRoot,
        PreflightResult preflight,
        ProfileConfig profile,
        IProgress<ProgressEvent>? progress = null,
        CancellationToken ct = default)
    {
        SourceEncoding.EnsureCodePagesRegistered();

        var ordered = TopologicalOrder(preflight.Inputs);
        var built = new Dictionary<string, CompiledAssembly>(StringComparer.OrdinalIgnoreCase);
        var result = new List<CompiledAssembly>();
        var errorCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var i = 0; i < ordered.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var input = ordered[i];
            progress?.Report(new ProgressEvent("P2", input.Name, i, ordered.Count));

            var assembly = BuildOne(projectRoot, input, profile, built, ct);
            built[input.Name] = assembly;
            result.Add(assembly);
            errorCounts[input.Name] = ReportCompileDiagnostics(assembly);
        }

        return new CompilationSet
        {
            Assemblies = result,
            Diagnostics = _diagnostics,
            ErrorCounts = errorCounts
        };
    }

    private CompiledAssembly BuildOne(
        string projectRoot,
        AssemblyInput input,
        ProfileConfig profile,
        IReadOnlyDictionary<string, CompiledAssembly> built,
        CancellationToken ct)
    {
        var parseOptions = new CSharpParseOptions(
            LanguageVersion.CSharp9,
            DocumentationMode.None,
            preprocessorSymbols: input.Defines);

        var trees = new List<SyntaxTree>(input.Sources.Count);
        var treePaths = new Dictionary<SyntaxTree, string>();
        var encodings = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var source in input.Sources)
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(source)) continue;

            var bytes = File.ReadAllBytes(source);
            var decoded = SourceEncoding.Decode(bytes);
            var relative = RspDiscovery.Relative(projectRoot, source);

            // The SourceText carries the detected encoding so line and column positions match the
            // compiler's view, which is what every span in the index is keyed by (02 §6.3).
            var text = SourceText.From(decoded.Text, decoded.Encoding);
            var tree = CSharpSyntaxTree.ParseText(text, parseOptions, path: relative, cancellationToken: ct);

            trees.Add(tree);
            treePaths[tree] = relative;
            encodings[relative] = decoded.EncodingName;
        }

        var references = ResolveReferences(projectRoot, input, profile, built);

        var options = new CSharpCompilationOptions(
            OutputKind.DynamicallyLinkedLibrary,
            allowUnsafe: input.AllowUnsafe,
            nullableContextOptions: NullableFrom(input.Nullable),
            concurrentBuild: true,
            deterministic: true);

        var compilation = CSharpCompilation.Create(input.Name, trees, references, options);

        return new CompiledAssembly
        {
            Input = input,
            Compilation = compilation,
            TreePaths = treePaths,
            Encodings = encodings
        };
    }

    /// <summary>
    /// References for one assembly: a source assembly built in this run is handed over as a
    /// <see cref="CompilationReference"/> so its symbols are the same symbols (02 §6.4); anything
    /// else is a file reference.
    /// </summary>
    private IReadOnlyList<MetadataReference> ResolveReferences(
        string projectRoot,
        AssemblyInput input,
        ProfileConfig profile,
        IReadOnlyDictionary<string, CompiledAssembly> built)
    {
        var references = new List<MetadataReference>();
        var sawBcl = false;

        foreach (var reference in input.References)
        {
            var name = Path.GetFileNameWithoutExtension(reference);
            if (BclMarkers.Any(marker => string.Equals(name, marker, StringComparison.OrdinalIgnoreCase)))
                sawBcl = true;

            if (built.TryGetValue(name, out var sourceAssembly))
            {
                references.Add(sourceAssembly.Compilation.ToMetadataReference());
                continue;
            }

            // A missing reference was already reported by the preflight (02 §6.2); adding nothing
            // here is what makes the dependent symbols bind as errors rather than as guesses.
            if (!File.Exists(reference)) continue;

            var metadata = LoadMetadata(projectRoot, reference);
            if (metadata is not null) references.Add(metadata);
        }

        // Whether a base class library is present is decided by the BCL markers alone. A
        // CompilationReference to another source assembly is not one, and treating it as one would
        // suppress the fallback and leave even System.Object unbound.
        if (!sawBcl && profile.AllowHostFrameworkReferences)
        {
            references.AddRange(HostFrameworkReferences(input.Name));
        }
        else if (!sawBcl)
        {
            _diagnostics.Add(DiagRec.Warning(
                Preflight.ReferenceMissing,
                $"Assembly '{input.Name}' references no base class library and profile '{profile.Id}' does not " +
                "allow the host framework to stand in, so most symbols will fail to bind. Every affected fact is " +
                "recorded with isCompilerCertain=false rather than guessed."));
        }

        return references;
    }

    /// <summary>
    /// The host runtime's reference assemblies. Permitted only for a fixture profile: on a real
    /// project the host BCL is not the one the app was built against, so substituting it would make
    /// bindings look certain when they are not (02 §4.1).
    /// </summary>
    private IReadOnlyList<MetadataReference> HostFrameworkReferences(string assemblyName)
    {
        var trusted = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Where(File.Exists)
            .ToList();

        if (trusted.Count == 0)
        {
            _diagnostics.Add(DiagRec.Warning(
                Preflight.ReferenceMissing,
                $"Assembly '{assemblyName}': the profile allows host framework references but " +
                "TRUSTED_PLATFORM_ASSEMBLIES is empty, so no base class library could be supplied."));
            return Array.Empty<MetadataReference>();
        }

        _diagnostics.Add(DiagRec.Info(
            "I_HOST_FRAMEWORK_REFERENCES",
            $"Assembly '{assemblyName}' had no base class library reference, so the host runtime's " +
            $"{trusted.Count} reference assemblies were used. Permitted for a fixture profile only."));

        var references = new List<MetadataReference>(trusted.Count);
        foreach (var path in trusted)
        {
            var metadata = LoadMetadata(null, path);
            if (metadata is not null) references.Add(metadata);
        }
        return references;
    }

    private MetadataReference? LoadMetadata(string? projectRoot, string path)
    {
        if (_metadataCache.TryGetValue(path, out var cached)) return cached;

        try
        {
            var reference = MetadataReference.CreateFromFile(path);
            _metadataCache[path] = reference;
            return reference;
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or ArgumentException)
        {
            _diagnostics.Add(DiagRec.Warning(
                "W_REFERENCE_UNREADABLE",
                $"Reference '{(projectRoot is null ? path : RspDiscovery.Relative(projectRoot, path))}' " +
                $"could not be loaded: {ex.Message}"));
            return null;
        }
    }

    /// <summary>
    /// Compiler errors per assembly (02 §6.4). Recorded at warning severity because analysis
    /// continues: a binding failure narrows what can be said, it does not invalidate the rest.
    /// </summary>
    private int ReportCompileDiagnostics(CompiledAssembly assembly)
    {
        var errors = assembly.Compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        if (errors.Count == 0) return 0;

        foreach (var error in errors.Take(MaxReportedErrorsPerAssembly))
        {
            var span = error.Location.GetLineSpan();
            _diagnostics.Add(DiagRec.Warning(
                CompileDiagnosticCode,
                $"[{assembly.Name}] {error.Id}: {error.GetMessage()}",
                span.Path.Length == 0 ? null : span.Path.Replace('\\', '/'),
                span.Path.Length == 0 ? null : span.StartLinePosition.Line + 1));
        }

        _diagnostics.Add(DiagRec.Warning(
            CompileDiagnosticCode,
            $"[{assembly.Name}] {errors.Count} compile error(s)" +
            (errors.Count > MaxReportedErrorsPerAssembly
                ? $"; the first {MaxReportedErrorsPerAssembly} are listed individually"
                : string.Empty) +
            ". Symbols that failed to bind are recorded with isCompilerCertain=false; no type is fabricated."));

        return errors.Count;
    }

    private static NullableContextOptions NullableFrom(string? nullable) => nullable?.ToLowerInvariant() switch
    {
        "enable" => NullableContextOptions.Enable,
        "warnings" => NullableContextOptions.Warnings,
        "annotations" => NullableContextOptions.Annotations,
        _ => NullableContextOptions.Disable
    };

    /// <summary>
    /// Orders assemblies so a dependency is built before its consumer (02 §6.4). A cycle — which
    /// Unity does not produce, but a hand-edited cache could — is reported and broken by assembly
    /// name, after which the references inside it resolve from file instead.
    /// </summary>
    internal IReadOnlyList<AssemblyInput> TopologicalOrder(IReadOnlyList<AssemblyInput> inputs)
    {
        var byName = inputs.ToDictionary(i => i.Name, StringComparer.OrdinalIgnoreCase);
        var ordered = new List<AssemblyInput>();
        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        void Visit(AssemblyInput assembly, List<string> chain)
        {
            state.TryGetValue(assembly.Name, out var marker);
            if (marker == 2) return;

            if (marker == 1)
            {
                _diagnostics.Add(DiagRec.Warning(
                    "W_ASSEMBLY_CYCLE",
                    $"Assembly reference cycle: {string.Join(" -> ", chain.Append(assembly.Name))}. " +
                    "The cycle is broken in assembly-name order and the references inside it resolve from file."));
                return;
            }

            state[assembly.Name] = 1;
            chain.Add(assembly.Name);

            foreach (var dependency in assembly.References
                         .Select(Path.GetFileNameWithoutExtension)
                         .Where(n => !string.IsNullOrEmpty(n))
                         .Select(n => n!)
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(n => n, StringComparer.Ordinal))
            {
                if (byName.TryGetValue(dependency, out var target)
                    && !string.Equals(target.Name, assembly.Name, StringComparison.OrdinalIgnoreCase))
                {
                    Visit(target, chain);
                }
            }

            chain.RemoveAt(chain.Count - 1);
            state[assembly.Name] = 2;
            ordered.Add(assembly);
        }

        foreach (var input in inputs.OrderBy(i => i.Name, StringComparer.Ordinal))
            Visit(input, new List<string>());

        return ordered;
    }

    public IReadOnlyList<DiagRec> Diagnostics => _diagnostics;
}
