using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using ScreenFlow.Core.Ir;
using ScreenFlow.CSharp.Compilation;
using ScreenFlow.CSharp.Values;

namespace ScreenFlow.CSharp.Walkers;

/// <summary>
/// What is known about the locals of one body (02 §5.4).
/// <para>
/// A local is only worth a value if its assignments are all visible, so this collects every one of
/// them — the declaration initialiser and each later assignment — rather than the first.
/// </para>
/// </summary>
public sealed class LocalFlow
{
    private readonly Dictionary<ISymbol, List<IOperation>> _assignments = new(SymbolEqualityComparer.Default);
    private readonly HashSet<ISymbol> _loopIndexes = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<ISymbol, IOperation> _loopBounds = new(SymbolEqualityComparer.Default);

    /// <summary>
    /// Collects the flow of one body, lambdas included: a lambda reads the locals of the method
    /// around it, so splitting them would lose the value of every captured variable.
    /// </summary>
    public static LocalFlow Of(IOperation body)
    {
        var flow = new LocalFlow();

        foreach (var operation in Descendants(body))
        {
            switch (operation)
            {
                case IVariableDeclaratorOperation { Initializer: not null } declarator:
                    flow.Add(declarator.Symbol, declarator.Initializer.Value);
                    break;

                case ISimpleAssignmentOperation { Target: ILocalReferenceOperation target } assignment:
                    flow.Add(target.Local, assignment.Value);
                    break;

                case IForLoopOperation loop:
                    flow.ReadLoop(loop);
                    break;
            }
        }

        return flow;
    }

    public IReadOnlyList<IOperation> AssignmentsOf(ISymbol local)
        => _assignments.TryGetValue(local, out var assignments)
            ? assignments
            : Array.Empty<IOperation>();

    public bool IsLoopIndex(ISymbol local) => _loopIndexes.Contains(local);

    public IOperation? LoopBound(ISymbol local)
        => _loopBounds.TryGetValue(local, out var bound) ? bound : null;

    /// <summary>Every operation of a tree, in source order.</summary>
    public static IEnumerable<IOperation> Descendants(IOperation root)
    {
        yield return root;

        foreach (var child in root.ChildOperations)
        foreach (var descendant in Descendants(child))
            yield return descendant;
    }

    private void Add(ISymbol local, IOperation value)
    {
        if (!_assignments.TryGetValue(local, out var assignments))
        {
            assignments = new List<IOperation>();
            _assignments[local] = assignments;
        }

        assignments.Add(value);
    }

    /// <summary>
    /// A <c>for</c> variable and what bounds it: <c>i &lt; x.Count</c> and <c>i &lt; x.Length</c> name
    /// <c>x</c> (02 §5.4), which is what connects a loop index to the collection it walks.
    /// </summary>
    private void ReadLoop(IForLoopOperation loop)
    {
        foreach (var operation in loop.Before.SelectMany(Descendants))
        {
            if (operation is IVariableDeclaratorOperation declarator) _loopIndexes.Add(declarator.Symbol);
        }

        if (loop.Condition is not IBinaryOperation
            {
                OperatorKind: BinaryOperatorKind.LessThan or BinaryOperatorKind.LessThanOrEqual
            } condition)
        {
            return;
        }

        if (Unwrap(condition.LeftOperand) is not ILocalReferenceOperation index) return;

        _loopIndexes.Add(index.Local);

        if (Unwrap(condition.RightOperand) is IPropertyReferenceOperation
            {
                Instance: not null
            } bound
            && bound.Property.Name is "Count" or "Length")
        {
            _loopBounds[index.Local] = bound.Instance;
        }
    }

    private static IOperation Unwrap(IOperation operation) => operation switch
    {
        IParenthesizedOperation parenthesized => Unwrap(parenthesized.Operand),
        IConversionOperation conversion => Unwrap(conversion.Operand),
        _ => operation
    };
}

/// <summary>The records one assembly's method bodies produce (02 §5.5, §5.8).</summary>
public sealed record BodyResult
{
    public IReadOnlyList<CallsiteRec> Callsites { get; init; } = Array.Empty<CallsiteRec>();
    public IReadOnlyList<AssignRec> Assigns { get; init; } = Array.Empty<AssignRec>();
    public IReadOnlyList<CreationRec> Creations { get; init; } = Array.Empty<CreationRec>();
    public IReadOnlyList<CfgRec> Cfgs { get; init; } = Array.Empty<CfgRec>();
    public IReadOnlyList<DiagRec> Diagnostics { get; init; } = Array.Empty<DiagRec>();

    public IEnumerable<IrRecord> Records
        => Callsites.Cast<IrRecord>().Concat(Assigns).Concat(Creations).Concat(Cfgs).Concat(Diagnostics);
}

/// <summary>
/// Walks method bodies and emits callsites, assignments and creations (02 §5.5, §6.6).
/// <para>
/// A call inside a lambda belongs to the lambda, not to the method around it: the lambda may run
/// later, or never, and attributing its calls to the enclosing method would claim a hop that the
/// code does not make at that point (03 §13.2, §19).
/// </para>
/// </summary>
public sealed class BodyWalker
{
    private readonly SymbolCollector _symbols;

    public BodyWalker(SymbolCollector symbols) => _symbols = symbols;

    public BodyResult Walk(CompiledAssembly assembly, CancellationToken ct = default)
    {
        var values = new ValueBuilder(_symbols, assembly.Compilation);
        var perTree = new ConcurrentBag<BodyResult>();

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = _symbols.Parallelism,
            CancellationToken = ct
        };

        Parallel.ForEach(assembly.Compilation.SyntaxTrees.ToList(), options, tree =>
        {
            var path = assembly.TreePaths.TryGetValue(tree, out var relative)
                ? relative
                : Ids.Normalize(tree.FilePath);

            perTree.Add(WalkTree(assembly, tree, path, values, ct));
        });

        return new BodyResult
        {
            Callsites = perTree.SelectMany(r => r.Callsites)
                .OrderBy(r => r.Id, StringComparer.Ordinal).ToList(),
            Assigns = perTree.SelectMany(r => r.Assigns)
                .OrderBy(r => r.Id, StringComparer.Ordinal).ToList(),
            Creations = perTree.SelectMany(r => r.Creations)
                .OrderBy(r => r.Id, StringComparer.Ordinal).ToList(),
            Cfgs = perTree.SelectMany(r => r.Cfgs)
                .OrderBy(r => r.MethodId, StringComparer.Ordinal).ToList(),
            Diagnostics = perTree.SelectMany(r => r.Diagnostics)
                .OrderBy(r => r.Message, StringComparer.Ordinal).ToList()
        };
    }

    /// <summary>One site to record, together with the symbol whose code it is part of.</summary>
    private sealed record Site(string OwnerId, IOperation Operation);

    private BodyResult WalkTree(
        CompiledAssembly assembly,
        SyntaxTree tree,
        string path,
        ValueBuilder values,
        CancellationToken ct)
    {
        var model = assembly.Compilation.GetSemanticModel(tree);
        var conditions = new ConditionCollector(values);
        var callsites = new List<CallsiteRec>();
        var assigns = new List<AssignRec>();
        var creations = new List<CreationRec>();
        var cfgs = new List<CfgRec>();
        var diagnostics = new List<DiagRec>();

        foreach (var declaration in tree.GetRoot(ct).DescendantNodes())
        {
            ct.ThrowIfCancellationRequested();

            var owner = BodyOwner(model, declaration, ct);
            if (owner is null) continue;

            var body = model.GetOperation(declaration, ct);
            if (body is null) continue;

            var ownerId = _symbols.IdOf(owner);
            var flow = LocalFlow.Of(body);
            var hasGoto = ConditionCollector.IsApproximate(declaration);
            var reachability = ReachabilityAnalyzer.Analyze(body);

            if (reachability.Available)
            {
                cfgs.Add(reachability.ToRecord(ownerId, hasGoto));
            }
            else
            {
                diagnostics.Add(DiagRec.Info(
                    ReachabilityAnalyzer.CfgUnavailableCode,
                    "No control flow graph for " + SymbolKeys.FqName(owner) +
                    "; every site in it is recorded as reachable.",
                    path,
                    SymbolCollector.SpanOf(declaration).StartLine));
            }

            var sites = new List<Site>();
            foreach (var child in body.ChildOperations) Collect(child, ownerId, sites);

            Emit(
                sites, path, model, declaration, flow, values, conditions, reachability, hasGoto,
                callsites, assigns, creations);
        }

        return new BodyResult
        {
            Callsites = callsites,
            Assigns = assigns,
            Creations = creations,
            Cfgs = cfgs,
            Diagnostics = diagnostics
        };
    }

    /// <summary>
    /// The symbol a body belongs to, or null when the node is not a body. Lambdas and local
    /// functions are not listed here: they are reached from the body that contains them, so that
    /// their sites are attributed to them and removed from their container.
    /// </summary>
    private static ISymbol? BodyOwner(SemanticModel model, SyntaxNode node, CancellationToken ct)
        => node switch
        {
            BaseMethodDeclarationSyntax method when method.Body is not null || method.ExpressionBody is not null
                => model.GetDeclaredSymbol(method, ct),
            AccessorDeclarationSyntax accessor when accessor.Body is not null || accessor.ExpressionBody is not null
                => model.GetDeclaredSymbol(accessor, ct),
            _ => null
        };

    private void Collect(IOperation operation, string ownerId, List<Site> sites)
    {
        switch (operation)
        {
            case IAnonymousFunctionOperation lambda:
            {
                var lambdaId = _symbols.IdOf(lambda.Symbol);
                foreach (var child in lambda.ChildOperations) Collect(child, lambdaId, sites);
                return;
            }

            case ILocalFunctionOperation localFunction:
            {
                var localId = _symbols.IdOf(localFunction.Symbol);
                foreach (var child in localFunction.ChildOperations) Collect(child, localId, sites);
                return;
            }
        }

        if (IsSite(operation)) sites.Add(new Site(ownerId, operation));

        foreach (var child in operation.ChildOperations) Collect(child, ownerId, sites);
    }

    private static bool IsSite(IOperation operation) => operation
        is IInvocationOperation
        or IObjectCreationOperation
        or ISimpleAssignmentOperation
        or ICompoundAssignmentOperation
        or ICoalesceAssignmentOperation
        or IEventAssignmentOperation;

    private void Emit(
        List<Site> sites,
        string path,
        SemanticModel model,
        SyntaxNode boundary,
        LocalFlow flow,
        ValueBuilder values,
        ConditionCollector conditions,
        ReachabilityAnalyzer reachability,
        bool approximate,
        List<CallsiteRec> callsites,
        List<AssignRec> assigns,
        List<CreationRec> creations)
    {
        foreach (var group in sites.GroupBy(s => s.OwnerId, StringComparer.Ordinal))
        {
            var ordered = group.OrderBy(s => s.Operation.Syntax.SpanStart).ToList();
            var context = new ValueContext
            {
                OwnerId = group.Key,
                File = path,
                Locals = flow,
                Model = model
            };

            // order is the source position within one owner (02 §5.5), so it is assigned after the
            // whole body is collected rather than as the tree is walked.
            for (var order = 0; order < ordered.Count; order++)
            {
                var operation = ordered[order].Operation;
                var span = SymbolCollector.SpanOf(operation.Syntax);
                var start = operation.Syntax.SpanStart;
                var guards = conditions.Collect(operation.Syntax, boundary, context, approximate);
                var reachable = reachability.IsReachable(operation.Syntax);

                switch (operation)
                {
                    case IInvocationOperation invocation:
                        callsites.Add(Callsite(
                            invocation, context, path, ValueBuilder.CallStart(invocation), order, span,
                            values, guards, reachable));
                        break;

                    case IObjectCreationOperation creation:
                    {
                        // A target-typed new that resolves to a ValueTuple is a tuple value
                        // (02 §5.4), and a tuple is not an object creation, so it is recorded only
                        // inside the value that contains it.
                        if (values.Build(creation, context) is not ObjectCreationValue value) break;

                        creations.Add(new CreationRec
                        {
                            Id = Ids.CreationId(context.OwnerId, path, start),
                            MethodId = context.OwnerId,
                            Value = value,
                            Conditions = guards,
                            Reachable = reachable,
                            Order = order,
                            Span = span
                        });
                        break;
                    }

                    default:
                        assigns.Add(Assign(
                            operation, context, path, start, order, span, values, guards, reachable));
                        break;
                }
            }
        }
    }

    private CallsiteRec Callsite(
        IInvocationOperation invocation,
        ValueContext context,
        string path,
        int start,
        int order,
        Span span,
        ValueBuilder values,
        IReadOnlyList<Condition> guards,
        bool reachable)
    {
        var target = ValueBuilder.TargetOf(invocation);

        return new CallsiteRec
        {
            Id = Ids.CallsiteId(context.OwnerId, path, start),
            CallerId = context.OwnerId,
            TargetId = _symbols.IdOf(target),
            TargetIsVirtual = IsVirtualTarget(target),
            Receiver = invocation.Instance is null ? null : values.Build(invocation.Instance, context),
            TypeArgs = invocation.TargetMethod.TypeArguments
                .Select(a => _symbols.TypeRefFor(a))
                .OfType<TypeRef>()
                .ToList(),
            Args = values.ArgsOf(invocation.Arguments, context),
            IsAwaited = IsAwaited(invocation),
            IsConditionalAccess = invocation.Instance is IConditionalAccessInstanceOperation,
            Conditions = guards,
            Reachable = reachable,
            Order = order,
            Span = span
        };
    }

    private static AssignRec Assign(
        IOperation operation,
        ValueContext context,
        string path,
        int start,
        int order,
        Span span,
        ValueBuilder values,
        IReadOnlyList<Condition> guards,
        bool reachable)
    {
        var (target, @operator, value) = operation switch
        {
            ISimpleAssignmentOperation simple
                => (simple.Target, AssignRec.Operators.Assign, simple.Value),
            ICoalesceAssignmentOperation coalesce
                => (coalesce.Target, AssignRec.Operators.CoalesceAssign, coalesce.Value),
            ICompoundAssignmentOperation compound
                => (compound.Target, CompoundOperator(compound.OperatorKind), compound.Value),
            IEventAssignmentOperation @event
                => (@event.EventReference, @event.Adds
                    ? AssignRec.Operators.AddAssign
                    : AssignRec.Operators.SubtractAssign, @event.HandlerValue),
            _ => (operation, AssignRec.Operators.Assign, operation)
        };

        return new AssignRec
        {
            Id = Ids.AssignId(context.OwnerId, path, start),
            MethodId = context.OwnerId,
            Target = values.Build(target, context),
            Operator = @operator,
            Value = values.Build(value, context),
            Conditions = guards,
            Reachable = reachable,
            Order = order,
            Span = span
        };
    }

    private static string CompoundOperator(BinaryOperatorKind kind) => kind switch
    {
        BinaryOperatorKind.Add => AssignRec.Operators.AddAssign,
        BinaryOperatorKind.Subtract => AssignRec.Operators.SubtractAssign,
        BinaryOperatorKind.Multiply => "*=",
        BinaryOperatorKind.Divide => "/=",
        BinaryOperatorKind.Remainder => "%=",
        BinaryOperatorKind.And => "&=",
        BinaryOperatorKind.Or => "|=",
        BinaryOperatorKind.ExclusiveOr => "^=",
        BinaryOperatorKind.LeftShift => "<<=",
        BinaryOperatorKind.RightShift => ">>=",
        _ => AssignRec.Operators.Assign
    };

    /// <summary>
    /// 02 §5.5: a target that can be replaced at run time. A delegate <c>Invoke</c> counts, because
    /// what it runs is whatever was assigned to the delegate.
    /// </summary>
    public static bool IsVirtualTarget(IMethodSymbol method)
        => method.IsVirtual
           || method.IsAbstract
           || method.IsOverride
           || method.ContainingType?.TypeKind is TypeKind.Interface or TypeKind.Delegate;

    private static bool IsAwaited(IOperation operation)
    {
        for (var current = operation.Parent; current is not null; current = current.Parent)
        {
            switch (current)
            {
                case IAwaitOperation:
                    return true;
                case IConversionOperation or IParenthesizedOperation:
                    continue;
                default:
                    return false;
            }
        }

        return false;
    }
}
