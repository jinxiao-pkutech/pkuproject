using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ScreenFlow.Core.Ir;
using ScreenFlow.CSharp.Compilation;

namespace ScreenFlow.CSharp.Walkers;

/// <summary>
/// Collects <c>symbol</c> records from a compilation (02 §5.2, §6.5).
/// <para>
/// A <c>partial</c> type is one symbol with every declaration listed, and a symbol that could not be
/// fully bound is recorded with <see cref="SymbolRec.IsCompilerCertain"/> false rather than dropped
/// or completed by guesswork. Metadata symbols appear only once they are referenced, which keeps the
/// index to the types the project actually touches.
/// </para>
/// </summary>
public sealed class SymbolCollector
{
    private readonly ConcurrentDictionary<string, SymbolRec> _records = new(StringComparer.Ordinal);

    /// <summary>Ids already recorded on reference, or being recorded; this is what stops cycles.</summary>
    private readonly ConcurrentDictionary<string, byte> _referenced = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<SyntaxTree, string> _paths = new();

    public SymbolCollector(string profileId, int parallelism = 0)
    {
        Keys = new SymbolKeys(profileId);
        Parallelism = parallelism > 0 ? parallelism : DefaultParallelism;
    }

    /// <summary>Half the cores by default (02 §6.9), leaving room for the rest of the pipeline.</summary>
    public static int DefaultParallelism => Math.Max(1, Environment.ProcessorCount / 2);

    public SymbolKeys Keys { get; }

    public int Parallelism { get; }

    /// <summary>Every collected symbol, ordered by id so the record stream is deterministic.</summary>
    public IReadOnlyList<SymbolRec> Symbols
        => _records.Values.OrderBy(r => r.Id, StringComparer.Ordinal).ToList();

    public void Collect(CompiledAssembly assembly, CancellationToken ct = default)
    {
        var trees = assembly.Compilation.SyntaxTrees.ToList();
        foreach (var tree in trees)
            _paths[tree] = assembly.TreePaths.TryGetValue(tree, out var path) ? path : Ids.Normalize(tree.FilePath);

        var options = new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct };
        Parallel.ForEach(trees, options, tree => CollectTree(assembly, tree, ct));
    }

    /// <summary>
    /// The id of a symbol, recording the symbol first if nothing has recorded it yet (02 §6.5).
    /// Always call this instead of <see cref="SymbolKeys.IdOf"/>, so that no record ever points at
    /// an id the index does not contain.
    /// </summary>
    public string IdOf(ISymbol symbol) => EnsureExternalSymbol(symbol) ?? Keys.IdOf(symbol);

    /// <summary>
    /// Records a referenced symbol, once. Returns its id, or null for something that has no symbol
    /// record of its own — an array type, a type parameter, an unbound type.
    /// <para>
    /// Also covers what the declaration walk cannot see: a delegate's <c>Invoke</c> and a default
    /// constructor are declared by nothing, so they carry no location and are recorded the way a
    /// metadata symbol is. A declaration, when there is one, always overwrites this.
    /// </para>
    /// </summary>
    public string? EnsureExternalSymbol(ISymbol symbol)
    {
        if (!IsRecordable(symbol)) return null;

        var id = Keys.IdOf(symbol);
        if (!_referenced.TryAdd(id, 0)) return id;

        var definition = Canonical(symbol.OriginalDefinition);
        var declarations = DeclarationsOf(definition);

        var record = BuildRecord(definition, id, new BuildOptions
        {
            IsSource = declarations.Count > 0,
            Declarations = declarations
        });

        if (record is not null) _records.TryAdd(id, record);
        return id;
    }

    // ------------------------------------------------------------------ per tree

    private void CollectTree(CompiledAssembly assembly, SyntaxTree tree, CancellationToken ct)
    {
        var model = assembly.Compilation.GetSemanticModel(tree);

        foreach (var node in tree.GetRoot(ct).DescendantNodes())
        {
            ct.ThrowIfCancellationRequested();
            Visit(model, node, ct);
        }
    }

    private void Visit(SemanticModel model, SyntaxNode node, CancellationToken ct)
    {
        switch (node)
        {
            case BaseNamespaceDeclarationSyntax ns:
                Declare(model.GetDeclaredSymbol(ns, ct), node, registerNested: false);
                break;

            case BaseTypeDeclarationSyntax type:
                Declare(model.GetDeclaredSymbol(type, ct), node, registerNested: false);
                break;

            case DelegateDeclarationSyntax del:
                Declare(model.GetDeclaredSymbol(del, ct), node, registerNested: false);
                break;

            case BaseMethodDeclarationSyntax method:
                Declare(model.GetDeclaredSymbol(method, ct), node, registerNested: true);
                break;

            case BasePropertyDeclarationSyntax property:
                Declare(model.GetDeclaredSymbol(property, ct), node, registerNested: true);
                break;

            case EnumMemberDeclarationSyntax enumMember:
                Declare(model.GetDeclaredSymbol(enumMember, ct), node, registerNested: true);
                break;

            // Covers fields, event fields and local variables alike; which one it is comes from the
            // symbol rather than from the syntax, so a declarator is never classified by its shape.
            case VariableDeclaratorSyntax declarator:
                Declare(model.GetDeclaredSymbol(declarator, ct), node, registerNested: true);
                break;

            case ForEachStatementSyntax forEach:
                Declare(model.GetDeclaredSymbol(forEach, ct), node, registerNested: false);
                break;

            case CatchDeclarationSyntax katch:
                Declare(model.GetDeclaredSymbol(katch, ct), node, registerNested: false);
                break;

            case SingleVariableDesignationSyntax designation:
                Declare(model.GetDeclaredSymbol(designation, ct), node, registerNested: false);
                break;

            case LocalFunctionStatementSyntax local:
                DeclareNestedFunction(model.GetDeclaredSymbol(local, ct), node, isLambda: false);
                break;

            case AnonymousFunctionExpressionSyntax lambda:
                DeclareNestedFunction(model.GetSymbolInfo(lambda, ct).Symbol, node, isLambda: true);
                break;
        }
    }

    private void Declare(ISymbol? declared, SyntaxNode node, bool registerNested)
    {
        if (declared is null || !IsRecordable(declared)) return;

        // A partial method is two symbols — the declaration and the implementation — with one
        // documentation id and therefore one record. Which of the two the parallel walk reaches
        // first must not decide what the record says, so it is always built from the defining part
        // with the declarations of both.
        var symbol = Canonical(declared);
        var key = Keys.KeyOf(symbol);
        var id = Keys.IdFromKey(SymbolKeys.AssemblyOf(symbol), key);

        // Nested functions take their ordinal from their container, so the container has to be
        // registered before the walk reaches them — which it is, since it is their ancestor.
        if (registerNested) RegisterNestedFunctions(node, key);

        var options = new BuildOptions
        {
            IsSource = true,
            Declarations = DeclarationsOf(symbol),
            Initializer = InitializerOf(node)
        };

        var record = BuildRecord(symbol, id, options);
        if (record is null) return;

        // The declaration is authoritative: it carries the initialiser and every declaration site,
        // which a record made from a reference alone does not have.
        _records[id] = record;
    }

    private void DeclareNestedFunction(ISymbol? symbol, SyntaxNode node, bool isLambda)
    {
        if (symbol is not IMethodSymbol method) return;

        var entry = Keys.NestedFunction(node);
        var key = entry?.Key ?? Keys.KeyOf(method);
        var id = Keys.IdFromKey(SymbolKeys.AssemblyOf(method), key);

        RegisterNestedFunctions(node, key);

        var containerFq = method.ContainingSymbol is null
            ? string.Empty
            : SymbolKeys.FqName(method.ContainingSymbol);

        var ordinal = entry?.Ordinal;
        var suffix = ordinal is null ? string.Empty : Ids.LambdaMarker + ordinal.Value.ToString();
        var fqName = isLambda ? containerFq + suffix : containerFq + suffix + ":" + method.Name;

        var options = new BuildOptions
        {
            IsSource = true,
            Declarations = DeclarationsOf(method),
            Ordinal = ordinal,
            IsLambda = isLambda,
            IsLocalFunction = !isLambda,
            NameOverride = isLambda ? SymbolKeys.LambdaName : method.Name,
            FqNameOverride = fqName
        };

        var record = BuildRecord(method, id, options);
        if (record is null) return;

        _records[id] = record;
    }

    private void RegisterNestedFunctions(SyntaxNode container, string containerKey)
    {
        var ordinal = 0;
        foreach (var nested in DirectNestedFunctions(container))
        {
            var entry = Keys.Register(nested, containerKey, ordinal++);
            RegisterNestedFunctions(nested, entry.Key);
        }
    }

    /// <summary>
    /// The lambdas and local functions whose nearest enclosing function is
    /// <paramref name="container"/>, in source order — which is what the ordinal counts (02 §5.6).
    /// </summary>
    internal static IEnumerable<SyntaxNode> DirectNestedFunctions(SyntaxNode container)
        => container
            .DescendantNodes(descendIntoChildren: n => n == container || !IsNestedFunctionSyntax(n))
            .Where(IsNestedFunctionSyntax);

    internal static bool IsNestedFunctionSyntax(SyntaxNode node)
        => node is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax;

    // ------------------------------------------------------------------ record construction

    /// <summary>Everything about a record that the symbol itself cannot answer.</summary>
    private sealed record BuildOptions
    {
        public bool IsSource { get; init; }
        public IReadOnlyList<DeclarationInfo> Declarations { get; init; } = Array.Empty<DeclarationInfo>();
        public Value? Initializer { get; init; }
        public int? Ordinal { get; init; }
        public bool IsLambda { get; init; }
        public bool IsLocalFunction { get; init; }
        public string? NameOverride { get; init; }
        public string? FqNameOverride { get; init; }
    }

    /// <summary>Set to false as soon as one referenced type fails to bind.</summary>
    private sealed class Certainty
    {
        public bool Value { get; set; } = true;
    }

    private SymbolRec? BuildRecord(ISymbol symbol, string id, BuildOptions options)
    {
        var kind = KindOf(symbol);
        if (kind is null) return null;

        var certainty = new Certainty();
        var first = options.Declarations.Count > 0 ? options.Declarations[0] : null;

        return new SymbolRec
        {
            Id = id,
            Kind = kind,
            Name = options.NameOverride ?? symbol.Name,
            FqName = options.FqNameOverride ?? SymbolKeys.FqName(symbol),
            ContainerId = ContainerIdOf(symbol),
            Namespace = NamespaceOf(symbol),
            Assembly = SymbolKeys.AssemblyOf(symbol),
            IsSource = options.IsSource,
            File = first?.File,
            Span = first?.Span,
            TypeKind = TypeKindOf(symbol),
            BaseTypeId = BaseTypeIdOf(symbol, certainty),
            InterfaceIds = InterfaceIdsOf(symbol),
            Modifiers = ModifiersOf(symbol),
            TypeRef = ValueTypeOf(symbol, certainty),
            ReturnType = symbol is IMethodSymbol method ? TypeRefOf(method.ReturnType, certainty) : null,
            Parameters = ParametersOf(symbol, certainty),
            TypeParameters = TypeParametersOf(symbol),
            Attributes = AttributesOf(symbol),
            Initializer = options.Initializer,
            ConstantValue = ConstantOf(symbol),
            OverridesId = OverriddenIdOf(symbol),
            IsLambda = options.IsLambda,
            IsLocalFunction = options.IsLocalFunction,
            Ordinal = options.Ordinal,
            IsCompilerCertain = certainty.Value,
            Declarations = options.Declarations
        };
    }

    private string? ContainerIdOf(ISymbol symbol)
    {
        if (symbol is ILocalSymbol || SymbolKeys.IsNestedFunction(symbol))
        {
            return symbol.ContainingSymbol is null or INamespaceSymbol
                ? null
                : IdOf(symbol.ContainingSymbol);
        }

        return symbol.ContainingType is null ? null : IdOf(symbol.ContainingType);
    }

    private static string? NamespaceOf(ISymbol symbol)
    {
        if (symbol is INamespaceSymbol ns) return ns.IsGlobalNamespace ? null : ns.ToDisplayString();

        var containing = symbol.ContainingNamespace;
        return containing is null || containing.IsGlobalNamespace ? null : containing.ToDisplayString();
    }

    private static string? TypeKindOf(ISymbol symbol)
    {
        if (symbol is not INamedTypeSymbol named) return null;

        return named.TypeKind switch
        {
            Microsoft.CodeAnalysis.TypeKind.Class => named.IsRecord ? TypeKinds.Record : TypeKinds.Class,
            Microsoft.CodeAnalysis.TypeKind.Struct => named.IsRecord ? TypeKinds.Record : TypeKinds.Struct,
            Microsoft.CodeAnalysis.TypeKind.Interface => TypeKinds.Interface,
            Microsoft.CodeAnalysis.TypeKind.Enum => TypeKinds.Enum,
            Microsoft.CodeAnalysis.TypeKind.Delegate => TypeKinds.Delegate,
            _ => null
        };
    }

    private string? BaseTypeIdOf(ISymbol symbol, Certainty certainty)
    {
        if (symbol is not INamedTypeSymbol named || named.BaseType is null) return null;

        if (named.BaseType is IErrorTypeSymbol)
        {
            certainty.Value = false;
            return null;
        }

        return IdOf(named.BaseType.OriginalDefinition);
    }

    private IReadOnlyList<string> InterfaceIdsOf(ISymbol symbol)
    {
        if (symbol is not INamedTypeSymbol named || named.Interfaces.Length == 0) return Array.Empty<string>();

        return named.Interfaces
            .Where(i => i is not IErrorTypeSymbol)
            .Select(i => IdOf(i.OriginalDefinition))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(i => i, StringComparer.Ordinal)
            .ToList();
    }

    private static IReadOnlyList<string> ModifiersOf(ISymbol symbol)
    {
        var present = new HashSet<string>(StringComparer.Ordinal);

        if (symbol.IsStatic) present.Add(Modifiers.Static);
        if (symbol.IsAbstract) present.Add(Modifiers.Abstract);
        if (symbol.IsVirtual) present.Add(Modifiers.Virtual);
        if (symbol.IsOverride) present.Add(Modifiers.Override);
        if (symbol.IsSealed) present.Add(Modifiers.Sealed);
        if (symbol.IsExtern) present.Add(Modifiers.Extern);

        switch (symbol)
        {
            case IMethodSymbol method when method.IsAsync:
                present.Add(Modifiers.Async);
                break;
            case IFieldSymbol field:
                if (field.IsConst) present.Add(Modifiers.Const);
                if (field.IsReadOnly) present.Add(Modifiers.Readonly);
                break;
            case IPropertySymbol property when property.IsReadOnly:
                present.Add(Modifiers.Readonly);
                break;
            case INamedTypeSymbol type when type.IsReadOnly:
                present.Add(Modifiers.Readonly);
                break;
            case ILocalSymbol local when local.IsConst:
                present.Add(Modifiers.Const);
                break;
        }

        if (HasPartialKeyword(symbol)) present.Add(Modifiers.Partial);

        // Fixed vocabulary order rather than insertion order, so the array is reproducible.
        return Modifiers.All.Where(present.Contains).ToList();
    }

    private static bool HasPartialKeyword(ISymbol symbol)
    {
        foreach (var reference in SyntaxReferencesOf(symbol))
        {
            var modifiers = reference.GetSyntax() switch
            {
                TypeDeclarationSyntax type => type.Modifiers,
                MethodDeclarationSyntax method => method.Modifiers,
                _ => default
            };

            if (modifiers.Any(SyntaxKind.PartialKeyword)) return true;
        }

        return false;
    }

    private TypeRef? ValueTypeOf(ISymbol symbol, Certainty certainty) => symbol switch
    {
        IFieldSymbol field => TypeRefOf(field.Type, certainty),
        IPropertySymbol property => TypeRefOf(property.Type, certainty),
        IEventSymbol @event => TypeRefOf(@event.Type, certainty),
        ILocalSymbol local => TypeRefOf(local.Type, certainty),
        _ => null
    };

    private IReadOnlyList<ParameterInfo> ParametersOf(ISymbol symbol, Certainty certainty)
    {
        var parameters = symbol switch
        {
            IMethodSymbol method => method.Parameters,
            IPropertySymbol property => property.Parameters,
            _ => default
        };

        if (parameters.IsDefaultOrEmpty) return Array.Empty<ParameterInfo>();

        return parameters.Select(p => new ParameterInfo
        {
            Name = p.Name,
            Type = TypeRefOf(p.Type, certainty),
            RefKind = p.RefKind switch
            {
                Microsoft.CodeAnalysis.RefKind.Ref => RefKinds.Ref,
                Microsoft.CodeAnalysis.RefKind.Out => RefKinds.Out,
                Microsoft.CodeAnalysis.RefKind.In => RefKinds.In,
                _ => RefKinds.None
            }
        }).ToList();
    }

    private static IReadOnlyList<string> TypeParametersOf(ISymbol symbol)
    {
        var parameters = symbol switch
        {
            INamedTypeSymbol named => named.TypeParameters,
            IMethodSymbol method => method.TypeParameters,
            _ => default
        };

        return parameters.IsDefaultOrEmpty
            ? Array.Empty<string>()
            : parameters.Select(p => p.Name).ToList();
    }

    private static IReadOnlyList<AttributeInfo> AttributesOf(ISymbol symbol)
    {
        var attributes = symbol.GetAttributes();
        if (attributes.Length == 0) return Array.Empty<AttributeInfo>();

        return attributes
            .Where(a => a.AttributeClass is not null)
            .Select(a => new AttributeInfo
            {
                TypeFqName = SymbolKeys.FqName(a.AttributeClass!),
                Args = a.ConstructorArguments.Select(AttributeArgValue).ToList()
            })
            .ToList();
    }

    /// <summary>
    /// An attribute argument. Only a primitive constant is stated as one; anything else keeps its
    /// source form as <c>unknown</c> instead of being approximated (02 §5.4).
    /// </summary>
    private static Value AttributeArgValue(TypedConstant constant)
    {
        if (constant.Kind is TypedConstantKind.Array or TypedConstantKind.Type)
            return UnknownValue.Of(constant.ToCSharpString());

        return constant.Value switch
        {
            string text => ConstValue.Of(text),
            bool flag => ConstValue.Of(flag),
            int number => ConstValue.Of(number),
            null => ConstValue.Null(),
            _ => UnknownValue.Of(constant.ToCSharpString())
        };
    }

    private static JsonNode? ConstantOf(ISymbol symbol)
    {
        var value = symbol switch
        {
            IFieldSymbol { HasConstantValue: true } field => field.ConstantValue,
            ILocalSymbol { HasConstantValue: true } local => local.ConstantValue,
            _ => null
        };

        return Values.ValueBuilder.ConstantNode(value);
    }

    private string? OverriddenIdOf(ISymbol symbol)
    {
        var overridden = symbol switch
        {
            IMethodSymbol method => (ISymbol?)method.OverriddenMethod,
            IPropertySymbol property => property.OverriddenProperty,
            IEventSymbol @event => @event.OverriddenEvent,
            _ => null
        };

        return overridden is null ? null : IdOf(overridden);
    }

    /// <summary>
    /// A type reference for a type the caller already has. Used by the value builder, which records
    /// the type of every value it produces (02 §5.4).
    /// </summary>
    public TypeRef? TypeRefFor(ITypeSymbol? type) => type is null ? null : TypeRefOf(type, new Certainty());

    /// <summary>
    /// A type reference (02 §5.2). A constructed generic carries the id of its definition plus its
    /// arguments; an unbound type carries only the name it was written with, and marks the owning
    /// symbol as not compiler-certain.
    /// </summary>
    private TypeRef TypeRefOf(ITypeSymbol type, Certainty certainty)
    {
        switch (type)
        {
            case IErrorTypeSymbol:
                certainty.Value = false;
                return TypeRef.Named(type.Name.Length == 0 ? "?" : type.Name);

            case INamedTypeSymbol named:
                return new TypeRef
                {
                    Id = IdOf(named.OriginalDefinition),
                    FqName = SymbolKeys.FqName(named),
                    Args = named.TypeArguments.Select(a => TypeRefOf(a, certainty)).ToList()
                };

            default:
                // Arrays, type parameters, pointers: no symbol record of their own, so the written
                // name is all there is to say about them.
                return TypeRef.Named(SymbolKeys.FqName(type));
        }
    }

    private static Value? InitializerOf(SyntaxNode node)
    {
        var expression = node switch
        {
            VariableDeclaratorSyntax declarator => declarator.Initializer?.Value,
            PropertyDeclarationSyntax property => property.Initializer?.Value,
            EnumMemberDeclarationSyntax enumMember => enumMember.EqualsValue?.Value,
            _ => null
        };

        // T09 replaces this with the folded Value; until then the source text is recorded as
        // unknown, which is what it is — not an empty initialiser.
        return expression is null ? null : UnknownValue.Of(expression.ToString());
    }

    private IReadOnlyList<DeclarationInfo> DeclarationsOf(ISymbol symbol)
    {
        var references = SyntaxReferencesOf(symbol);
        if (references.Count == 0) return Array.Empty<DeclarationInfo>();

        return references
            .Select(reference => reference.GetSyntax())
            .Select(node => new DeclarationInfo { File = PathOf(node.SyntaxTree), Span = SpanOf(node) })
            .Distinct()
            .OrderBy(d => d.File, StringComparer.Ordinal)
            .ThenBy(d => d.Span.StartLine)
            .ThenBy(d => d.Span.StartColumn)
            .ToList();
    }

    /// <summary>
    /// Every place a symbol is declared, both parts of a partial method included (02 §5.2). Roslyn
    /// models those as two symbols, and each one knows only its own half.
    /// </summary>
    private static IReadOnlyList<SyntaxReference> SyntaxReferencesOf(ISymbol symbol)
    {
        var references = symbol.DeclaringSyntaxReferences.AsEnumerable();

        if (symbol is IMethodSymbol method)
        {
            if (method.PartialDefinitionPart is { } definition)
                references = references.Concat(definition.DeclaringSyntaxReferences);

            if (method.PartialImplementationPart is { } implementation)
                references = references.Concat(implementation.DeclaringSyntaxReferences);
        }

        return references.Distinct().ToList();
    }

    /// <summary>The part of a partial method that owns the record: its declaration.</summary>
    private static ISymbol Canonical(ISymbol symbol)
        => symbol is IMethodSymbol { PartialDefinitionPart: not null } implementation
            ? implementation.PartialDefinitionPart!
            : symbol;

    private string PathOf(SyntaxTree tree)
        => _paths.TryGetValue(tree, out var path) ? path : Ids.Normalize(tree.FilePath);

    public static Span SpanOf(SyntaxNode node)
    {
        var line = node.SyntaxTree.GetLineSpan(node.Span);
        return Span.Of(
            line.StartLinePosition.Line + 1,
            line.StartLinePosition.Character + 1,
            line.EndLinePosition.Line + 1,
            line.EndLinePosition.Character + 1);
    }

    private static bool IsRecordable(ISymbol symbol) => symbol switch
    {
        INamespaceSymbol ns => !ns.IsGlobalNamespace,
        IErrorTypeSymbol => false,
        INamedTypeSymbol => true,
        IMethodSymbol => true,
        IFieldSymbol => true,
        IPropertySymbol => true,
        IEventSymbol => true,
        ILocalSymbol => true,
        _ => false
    };

    /// <summary>
    /// Replaces each field and property initialiser with its folded value (02 §5.4, §6.5). It is a
    /// second pass because folding needs the symbol ids this collector produces — and the domain
    /// adapters depend on it: a screen registry is nothing but a set of static initialisers.
    /// </summary>
    public void FoldInitializers(
        CompiledAssembly assembly,
        Values.ValueBuilder values,
        CancellationToken ct = default)
    {
        var options = new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct };

        Parallel.ForEach(assembly.Compilation.SyntaxTrees.ToList(), options, tree =>
        {
            var model = assembly.Compilation.GetSemanticModel(tree);
            var path = PathOf(tree);

            foreach (var node in tree.GetRoot(ct).DescendantNodes())
            {
                ct.ThrowIfCancellationRequested();

                var expression = InitializerExpression(node);
                if (expression is null) continue;

                var symbol = model.GetDeclaredSymbol(node, ct);
                if (symbol is null || !IsRecordable(symbol)) continue;

                var operation = model.GetOperation(expression, ct);
                if (operation is null) continue;

                var id = Keys.IdFromKey(SymbolKeys.AssemblyOf(symbol), Keys.KeyOf(symbol));
                if (!_records.TryGetValue(id, out var record)) continue;

                var context = new Values.ValueContext { OwnerId = id, File = path };
                _records[id] = record with { Initializer = values.Build(operation, context) };
            }
        });
    }

    /// <summary>
    /// The expression that gives a symbol its value: a field, property or enum member initialiser,
    /// and the single returned expression of a method that has one.
    /// <para>
    /// The method case is what makes a topic template readable (02 §9.8): the template is the
    /// return value of <c>GetTopic()</c>, and no IR record carries a return. Recording it here
    /// keeps the record kinds of 02 §5.1 unchanged and uses a field that already means "the
    /// expression this symbol evaluates to"; see PROGRESS Q-16.
    /// </para>
    /// </summary>
    private static ExpressionSyntax? InitializerExpression(SyntaxNode node) => node switch
    {
        VariableDeclaratorSyntax declarator => declarator.Initializer?.Value,
        PropertyDeclarationSyntax property => property.Initializer?.Value,
        EnumMemberDeclarationSyntax enumMember => enumMember.EqualsValue?.Value,
        MethodDeclarationSyntax method => SingleReturn(method),
        _ => null
    };

    /// <summary>
    /// The one expression a method returns, or null when it returns in more than one place. More
    /// than one return is more than one value, and picking one would be a guess.
    /// </summary>
    private static ExpressionSyntax? SingleReturn(MethodDeclarationSyntax method)
    {
        if (method.ExpressionBody is not null) return method.ExpressionBody.Expression;
        if (method.Body is null) return null;

        var returns = method.Body.DescendantNodes()
            .OfType<ReturnStatementSyntax>()
            .Where(statement => statement.Expression is not null)
            .ToList();

        return returns.Count == 1 ? returns[0].Expression : null;
    }

    private static string? KindOf(ISymbol symbol) => symbol switch
    {
        INamespaceSymbol => SymbolKinds.Namespace,
        INamedTypeSymbol => SymbolKinds.Type,
        IMethodSymbol => SymbolKinds.Method,
        IFieldSymbol => SymbolKinds.Field,
        IPropertySymbol => SymbolKinds.Property,
        IEventSymbol => SymbolKinds.Event,
        ILocalSymbol => SymbolKinds.Local,
        _ => null
    };
}
