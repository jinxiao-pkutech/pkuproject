using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ScreenFlow.Core.Ir;
using ScreenFlow.CSharp.Compilation;

namespace ScreenFlow.CSharp.Walkers;

/// <summary>
/// Collects the interface implementation map (02 §5.3, §6.5).
/// <para>
/// This is recorded rather than inferred because the implementing member may live on a base class,
/// and because an interface call is the point where a screen path usually continues: without the
/// map, a call to <c>IEdyCharge_SSAW0903_View_Event.OnClickBtnOther</c> has no next hop.
/// </para>
/// </summary>
public sealed class ImplCollector
{
    public IReadOnlyList<ImplRec> Collect(
        CompiledAssembly assembly,
        SymbolCollector symbols,
        CancellationToken ct = default)
    {
        var records = new ConcurrentDictionary<string, ImplRec>(StringComparer.Ordinal);
        var seenTypes = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = symbols.Parallelism,
            CancellationToken = ct
        };

        Parallel.ForEach(
            assembly.Compilation.SyntaxTrees.ToList(),
            options,
            tree => CollectTree(assembly, tree, symbols, records, seenTypes, ct));

        return records.Values
            .OrderBy(r => r.TypeId, StringComparer.Ordinal)
            .ThenBy(r => r.InterfaceMemberId, StringComparer.Ordinal)
            .ThenBy(r => r.ImplMemberId, StringComparer.Ordinal)
            .ToList();
    }

    private static void CollectTree(
        CompiledAssembly assembly,
        SyntaxTree tree,
        SymbolCollector symbols,
        ConcurrentDictionary<string, ImplRec> records,
        ConcurrentDictionary<string, byte> seenTypes,
        CancellationToken ct)
    {
        var model = assembly.Compilation.GetSemanticModel(tree);

        foreach (var declaration in tree.GetRoot(ct).DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            ct.ThrowIfCancellationRequested();

            if (model.GetDeclaredSymbol(declaration, ct) is not INamedTypeSymbol type) continue;
            if (type.TypeKind is not (TypeKind.Class or TypeKind.Struct)) continue;

            var typeId = symbols.IdOf(type);

            // A partial type is declared more than once; its implementation map is the same each
            // time, so the second declaration adds nothing.
            if (!seenTypes.TryAdd(typeId, 0)) continue;

            foreach (var contract in type.AllInterfaces)
            {
                foreach (var member in contract.GetMembers())
                {
                    ct.ThrowIfCancellationRequested();

                    var implementation = type.FindImplementationForInterfaceMember(member);
                    if (implementation is null) continue;

                    var record = new ImplRec
                    {
                        TypeId = typeId,
                        InterfaceMemberId = symbols.IdOf(member),
                        ImplMemberId = symbols.IdOf(implementation)
                    };

                    records.TryAdd(
                        string.Concat(record.TypeId, "|", record.InterfaceMemberId, "|", record.ImplMemberId),
                        record);
                }
            }
        }
    }
}
