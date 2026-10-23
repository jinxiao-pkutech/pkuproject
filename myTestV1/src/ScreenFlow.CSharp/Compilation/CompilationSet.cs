using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace ScreenFlow.CSharp.Compilation;

/// <summary>One compiled assembly, with the facts the extractor needs about its sources.</summary>
public sealed record CompiledAssembly
{
    public required AssemblyInput Input { get; init; }

    public required CSharpCompilation Compilation { get; init; }

    /// <summary>Maps each syntax tree to its project-root-relative path.</summary>
    public required IReadOnlyDictionary<SyntaxTree, string> TreePaths { get; init; }

    /// <summary>Encoding each source was decoded with, keyed by relative path (02 §6.3).</summary>
    public required IReadOnlyDictionary<string, string> Encodings { get; init; }

    public string Name => Input.Name;

    /// <summary>False for an assembly compiled only so that references bind (02 §4.1).</summary>
    public bool ExtractIr => Input.ExtractIr;
}

/// <summary>
/// Every assembly of one build, in dependency order (02 §6.4).
/// <para>
/// Order matters because a source assembly referenced by another is handed over as a
/// <see cref="CompilationReference"/>, which requires it to exist first.
/// </para>
/// </summary>
public sealed record CompilationSet
{
    /// <summary>Assemblies in topological order: a dependency always precedes its consumer.</summary>
    public IReadOnlyList<CompiledAssembly> Assemblies { get; init; } = Array.Empty<CompiledAssembly>();

    public IReadOnlyList<Core.Ir.DiagRec> Diagnostics { get; init; } = Array.Empty<Core.Ir.DiagRec>();

    /// <summary>The assemblies IR is extracted from, in the same order.</summary>
    public IReadOnlyList<CompiledAssembly> ToExtract => Assemblies.Where(a => a.ExtractIr).ToList();

    public CompiledAssembly? ByName(string name)
        => Assemblies.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Compiler error counts per assembly, for the build report.</summary>
    public IReadOnlyDictionary<string, int> ErrorCounts { get; init; } = new Dictionary<string, int>();
}
