using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.CodeAnalysis;
using ScreenFlow.Core.Ir;

namespace ScreenFlow.CSharp.Walkers;

/// <summary>
/// Symbol keys and the stable ids derived from them (02 §5.6).
/// <para>
/// A key is what makes an id reproducible: the documentation comment id for anything the compiler
/// names, and an explicit extension for the things it does not — lambdas, local functions and local
/// variables. Short names are never part of a key, because the real project has 125 duplicate
/// simple type names and matching on one would attribute facts to the wrong type (CLAUDE.md §4).
/// </para>
/// </summary>
public sealed class SymbolKeys
{
    /// <summary>The key of one lambda or local function, and its source-order ordinal.</summary>
    public sealed record NestedFunctionKey(string Key, int Ordinal, string ContainerKey);

    /// <summary>Name recorded for a lambda, which has no name of its own.</summary>
    public const string LambdaName = "<lambda>";

    private readonly ConcurrentDictionary<string, NestedFunctionKey> _nested = new(StringComparer.Ordinal);

    public SymbolKeys(string profileId) => ProfileId = profileId;

    /// <summary>Part of every symbol id, so two profiles never share one (02 §5.6).</summary>
    public string ProfileId { get; }

    /// <summary>
    /// Display format for <c>fqName</c> (02 §5.2): fully qualified, and for a method including the
    /// parameter type signature, which is what distinguishes two overloads in a report.
    /// </summary>
    public static readonly SymbolDisplayFormat FqFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters
                       | SymbolDisplayMemberOptions.IncludeContainingType
                       | SymbolDisplayMemberOptions.IncludeExplicitInterface,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType
                          | SymbolDisplayParameterOptions.IncludeParamsRefOut,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    public static string FqName(ISymbol symbol)
    {
        var text = symbol.ToDisplayString(FqFormat);
        return string.IsNullOrEmpty(text) ? symbol.Name : text;
    }

    /// <summary>
    /// The documentation comment id, or the fully qualified display string when the symbol has none
    /// (02 §5.6). Roslyn returns <c>"!"</c>-prefixed ids for symbols it could not name completely;
    /// those fall back as well, so an id is never built from a half-formed key.
    /// </summary>
    public static string DocCommentKey(ISymbol symbol)
    {
        var id = symbol.GetDocumentationCommentId();
        return string.IsNullOrEmpty(id) || id.StartsWith('!')
            ? symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            : id;
    }

    public static string AssemblyOf(ISymbol symbol)
        => symbol.OriginalDefinition.ContainingAssembly?.Name ?? symbol.ContainingAssembly?.Name ?? string.Empty;

    public static bool IsNestedFunction(ISymbol symbol)
        => symbol is IMethodSymbol method
           && method.MethodKind is MethodKind.AnonymousFunction or MethodKind.LocalFunction;

    /// <summary>
    /// Where a nested function sits in the file. Used as the registration key rather than the
    /// syntax node itself so that a lookup works across trees and threads.
    /// </summary>
    public static string SyntaxSlot(SyntaxNode node)
        => string.Concat(
            Ids.Normalize(node.SyntaxTree.FilePath), "|",
            node.SpanStart.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// Records the key of one lambda or local function. The ordinal is assigned by walking the
    /// container in source order, which is the only thing that makes the key reproducible — the
    /// compiler's own name for a lambda is a display string shared by all of them.
    /// </summary>
    public NestedFunctionKey Register(SyntaxNode node, string containerKey, int ordinal)
        => _nested.GetOrAdd(
            SyntaxSlot(node),
            new NestedFunctionKey(Ids.LambdaKey(containerKey, ordinal), ordinal, containerKey));

    public NestedFunctionKey? NestedFunction(SyntaxNode node)
        => _nested.TryGetValue(SyntaxSlot(node), out var entry) ? entry : null;

    /// <summary>The key of a symbol, or null when it is a nested function that was never registered.</summary>
    public string? KeyOrNull(ISymbol symbol)
    {
        switch (symbol)
        {
            case IMethodSymbol method when IsNestedFunction(method):
            {
                var node = FirstSyntax(method);
                return node is null ? null : NestedFunction(node)?.Key;
            }

            case ILocalSymbol local:
            {
                var container = local.ContainingSymbol is null ? null : KeyOrNull(local.ContainingSymbol);
                if (container is null) return null;

                var node = FirstSyntax(local);
                return Ids.LocalKey(container, local.Name, node?.SpanStart ?? 0);
            }

            default:
                // The definition, never a constructed instantiation: List<Button> and List<string>
                // are one symbol with different type arguments (02 §5.2), and a generic method is
                // one symbol however it was called.
                return DocCommentKey(symbol.OriginalDefinition);
        }
    }

    /// <summary>
    /// The key of a symbol. A nested function whose ordinal was never assigned falls back to its
    /// source position, which still identifies it uniquely and reproducibly; the caller is expected
    /// to have registered it, and <see cref="KeyOrNull"/> says whether that happened.
    /// </summary>
    public string KeyOf(ISymbol symbol)
        => KeyOrNull(symbol) ?? PositionalFallbackKey(symbol);

    public string IdOf(ISymbol symbol) => Ids.SymbolId(ProfileId, AssemblyOf(symbol), KeyOf(symbol));

    public string IdFromKey(string assembly, string key) => Ids.SymbolId(ProfileId, assembly, key);

    public static SyntaxNode? FirstSyntax(ISymbol symbol)
        => symbol.DeclaringSyntaxReferences.Length == 0
            ? null
            : symbol.DeclaringSyntaxReferences[0].GetSyntax();

    private string PositionalFallbackKey(ISymbol symbol)
    {
        var container = symbol.ContainingSymbol is null ? string.Empty : KeyOf(symbol.ContainingSymbol);
        var node = FirstSyntax(symbol);
        var offset = node?.SpanStart ?? 0;
        var path = node is null ? string.Empty : Ids.Normalize(node.SyntaxTree.FilePath);

        return string.Concat(
            container, Ids.LambdaMarker, "@", path, ":",
            offset.ToString(CultureInfo.InvariantCulture));
    }
}
