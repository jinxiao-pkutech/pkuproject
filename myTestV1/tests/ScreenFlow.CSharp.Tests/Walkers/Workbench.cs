using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ScreenFlow.Core.Config;
using ScreenFlow.Core.Ir;
using ScreenFlow.CSharp.Compilation;
using ScreenFlow.CSharp.Tests.Fixture;
using ScreenFlow.CSharp.Values;
using ScreenFlow.CSharp.Walkers;

namespace ScreenFlow.CSharp.Tests.Walkers;

/// <summary>
/// The fixture compiled once more, kept open so a test can fold one named expression directly.
/// <para>
/// Needed because not every expression ends up in a record: a <c>return</c> expression is folded
/// into whatever reads it, so <c>GetTopic</c>'s interpolated string is only observable by asking the
/// value builder for it.
/// </para>
/// </summary>
internal sealed class Workbench
{
    private static readonly Lazy<Workbench> LazyEditor =
        new(() => Create("fixture"), LazyThreadSafetyMode.ExecutionAndPublication);

    public static Workbench Editor => LazyEditor.Value;

    private Workbench(CompiledAssembly assembly, SymbolCollector symbols)
    {
        Assembly = assembly;
        Symbols = symbols;
        Values = new ValueBuilder(symbols, assembly.Compilation);
    }

    public CompiledAssembly Assembly { get; }

    public SymbolCollector Symbols { get; }

    public ValueBuilder Values { get; }

    private static Workbench Create(string profileId)
    {
        var (profiles, _) = ConfigLoader.LoadAll(Path.Combine(FixtureLayout.RepoRoot, "config"));
        var profile = profiles.Find(profileId)
                      ?? throw new InvalidOperationException($"profile '{profileId}' is missing");

        var preflight = Preflight.Run(FixtureLayout.MiniPos, profile);
        var compilations = new CompilationBuilder().Build(FixtureLayout.MiniPos, preflight, profile);
        var assembly = compilations.ToExtract.Single();

        var symbols = new SymbolCollector(profile.Id);
        symbols.Collect(assembly);

        return new Workbench(assembly, symbols);
    }

    /// <summary>
    /// Folds one expression out of a self-contained snippet. Used for the folding rules the fixture
    /// has no occurrence of, so that the rule is covered without inventing a shape in MiniPos that
    /// 03 does not document.
    /// </summary>
    public static Value FoldSnippet(string source, string expression)
        => CreateSnippet(source).Fold("Snippet.cs", expression);

    /// <summary>The callsites, assignments, creations and control flow of a snippet.</summary>
    public static (BodyResult Bodies, SymbolCollector Symbols) SnippetBodies(string source)
    {
        var bench = CreateSnippet(source);
        return (new BodyWalker(bench.Symbols).Walk(bench.Assembly), bench.Symbols);
    }

    private static Workbench CreateSnippet(string source)
    {
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source, path: "Snippet.cs");

        var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
            "Snippet",
            new[] { tree },
            FixtureLayout.HostFrameworkReferences(),
            new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        if (errors.Count > 0)
            throw new InvalidOperationException($"the snippet does not compile: {errors[0]}");

        var assembly = new CompiledAssembly
        {
            Input = new AssemblyInput
            {
                Name = "Snippet",
                OutPath = "Snippet.dll",
                Platform = RspPlatforms.Unknown,
                RspPath = "Snippet.rsp"
            },
            Compilation = compilation,
            TreePaths = new Dictionary<SyntaxTree, string> { [tree] = "Snippet.cs" },
            Encodings = new Dictionary<string, string>()
        };

        var symbols = new SymbolCollector("snippet");
        symbols.Collect(assembly);

        return new Workbench(assembly, symbols);
    }

    /// <summary>
    /// Folds the first expression in <paramref name="fileSuffix"/> whose source text is exactly
    /// <paramref name="expression"/>, in the context of the body that contains it.
    /// </summary>
    public Value Fold(string fileSuffix, string expression)
    {
        var tree = Assembly.Compilation.SyntaxTrees
            .Single(t => t.FilePath.EndsWith(fileSuffix, StringComparison.Ordinal));

        var model = Assembly.Compilation.GetSemanticModel(tree);

        var node = tree.GetRoot().DescendantNodes()
            .OfType<ExpressionSyntax>()
            .FirstOrDefault(e => e.ToString() == expression)
            ?? throw new InvalidOperationException($"'{expression}' does not occur in {fileSuffix}");

        var operation = model.GetOperation(node)
                        ?? throw new InvalidOperationException($"'{expression}' has no operation");

        var owner = OwnerOf(model, node);
        var context = new ValueContext
        {
            OwnerId = owner.Id,
            File = Assembly.TreePaths[tree],
            Locals = owner.Body is null ? null : LocalFlow.Of(owner.Body)
        };

        return Values.Build(operation, context);
    }

    private (string Id, IOperation? Body) OwnerOf(SemanticModel model, SyntaxNode node)
    {
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            switch (current)
            {
                case BaseMethodDeclarationSyntax method:
                    return (Symbols.IdOf(model.GetDeclaredSymbol(method)!), model.GetOperation(method));

                case AccessorDeclarationSyntax accessor:
                    return (Symbols.IdOf(model.GetDeclaredSymbol(accessor)!), model.GetOperation(accessor));

                case VariableDeclaratorSyntax declarator when model.GetDeclaredSymbol(declarator) is IFieldSymbol field:
                    return (Symbols.IdOf(field), null);

                case PropertyDeclarationSyntax property:
                    return (Symbols.IdOf(model.GetDeclaredSymbol(property)!), null);
            }
        }

        throw new InvalidOperationException($"no body owner above '{node}'");
    }
}
