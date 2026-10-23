using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;
using ScreenFlow.Core.Ir;

namespace ScreenFlow.CSharp.Walkers;

/// <summary>
/// Marks the sites the compiler proves cannot run (02 §5.8, §6.8).
/// <para>
/// Unreachable code is a real source of phantom screen edges: the fixture's <c>Legacy</c> method has
/// a navigation after <c>return</c> (E-10). Recording it as unreachable keeps the fact while keeping
/// it out of the path search — deleting it would hide that the code is there at all.
/// </para>
/// </summary>
public sealed class ReachabilityAnalyzer
{
    /// <summary>Emitted when the graph cannot be built; every site is then treated as reachable.</summary>
    public const string CfgUnavailableCode = "I_CFG_UNAVAILABLE";

    private readonly HashSet<SyntaxNode> _unreachable = new();
    private readonly List<Span> _unreachableSpans = new();

    private ReachabilityAnalyzer(int blockCount, bool available)
    {
        BlockCount = blockCount;
        Available = available;
    }

    /// <summary>Blocks of the body's own graph; nested functions have graphs of their own.</summary>
    public int BlockCount { get; private set; }

    /// <summary>False when the graph could not be built, which makes every site reachable.</summary>
    public bool Available { get; }

    public IReadOnlyList<Span> UnreachableSpans
        => _unreachableSpans
            .OrderBy(s => s.StartLine)
            .ThenBy(s => s.StartColumn)
            .ToList();

    public bool IsReachable(SyntaxNode site) => !_unreachable.Contains(site);

    /// <summary>
    /// Analyses one body. A graph that cannot be built is reported rather than guessed at: the
    /// alternative would be marking live code unreachable and silently losing a screen edge.
    /// </summary>
    public static ReachabilityAnalyzer Analyze(IOperation body)
    {
        ControlFlowGraph? graph;

        try
        {
            graph = Create(body);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new ReachabilityAnalyzer(0, available: false);
        }

        if (graph is null) return new ReachabilityAnalyzer(0, available: false);

        var analyzer = new ReachabilityAnalyzer(graph.Blocks.Length, available: true);
        analyzer.Walk(graph);
        return analyzer;
    }

    private static ControlFlowGraph? Create(IOperation body) => body switch
    {
        IMethodBodyOperation method => ControlFlowGraph.Create(method),
        IConstructorBodyOperation constructor => ControlFlowGraph.Create(constructor),
        IBlockOperation block => ControlFlowGraph.Create(block),
        IFieldInitializerOperation field => ControlFlowGraph.Create(field),
        IPropertyInitializerOperation property => ControlFlowGraph.Create(property),
        _ => null
    };

    private void Walk(ControlFlowGraph graph)
    {
        foreach (var block in graph.Blocks)
        {
            foreach (var operation in Operations(block))
            {
                if (!block.IsReachable) MarkUnreachable(operation);

                // A lambda has its own graph, reachable or not on its own terms (T10 §5).
                foreach (var nested in LocalFlow.Descendants(operation))
                {
                    if (nested is IFlowAnonymousFunctionOperation lambda)
                        Walk(graph.GetAnonymousFunctionControlFlowGraph(lambda));
                }
            }
        }

        foreach (var local in graph.LocalFunctions)
            Walk(graph.GetLocalFunctionControlFlowGraph(local));
    }

    private static IEnumerable<IOperation> Operations(BasicBlock block)
    {
        foreach (var operation in block.Operations) yield return operation;
        if (block.BranchValue is not null) yield return block.BranchValue;
    }

    private void MarkUnreachable(IOperation operation)
    {
        _unreachableSpans.Add(SymbolCollector.SpanOf(operation.Syntax));

        // The whole subtree is unreachable, and it is the inner nodes that carry the sites.
        foreach (var nested in LocalFlow.Descendants(operation))
            _unreachable.Add(nested.Syntax);
    }

    /// <summary>The record for one body (02 §5.8).</summary>
    public CfgRec ToRecord(string methodId, bool hasGoto)
        => new()
        {
            MethodId = methodId,
            BlockCount = BlockCount,
            HasGoto = hasGoto,
            UnreachableSpans = UnreachableSpans
        };
}
