using System.Collections.Concurrent;
using System.Text.Json;
using ScreenFlow.Core.Config;
using ScreenFlow.Core.Ir;
using ScreenFlow.Core.Storage;

namespace ScreenFlow.Core.Resolution;

/// <summary>
/// The in-memory indexes dispatch needs (pipeline phase P5, 02 §2): which methods implement an
/// interface member, which override a virtual one, and how the types relate.
/// <para>
/// Built in one pass per table rather than queried per call, because dispatch is asked about every
/// callsite in the project and a per-call scan over 9,400 files' symbols would dominate the build.
/// </para>
/// </summary>
public sealed class ResolutionIndex
{
    private readonly IndexReader _reader;
    private readonly Dictionary<string, List<string>> _overriders;
    private readonly Dictionary<string, TypeNode> _types;
    private readonly ConcurrentDictionary<string, SymbolRec?> _symbols = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, HashSet<string>> _supertypes = new(StringComparer.Ordinal);

    private ResolutionIndex(
        IndexReader reader,
        Dictionary<string, List<string>> overriders,
        Dictionary<string, TypeNode> types)
    {
        _reader = reader;
        _overriders = overriders;
        _types = types;
    }

    /// <summary>One type's place in the hierarchy.</summary>
    private sealed record TypeNode(string Id, string? BaseTypeId, IReadOnlyList<string> InterfaceIds, string? TypeKind);

    public int MethodsWithOverrides => _overriders.Count;

    public int TypeCount => _types.Count;

    public static ResolutionIndex Build(IndexReader reader)
    {
        var overriders = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        using (var command = reader.Db.Command(
                   "SELECT id, json_extract(json, '$.overridesId') FROM symbols " +
                   "WHERE kind = 'method' AND json_extract(json, '$.overridesId') IS NOT NULL;"))
        using (var rows = command.ExecuteReader())
        {
            while (rows.Read())
            {
                var id = rows.GetString(0);
                var overridden = rows.GetString(1);

                if (!overriders.TryGetValue(overridden, out var list))
                {
                    list = new List<string>();
                    overriders[overridden] = list;
                }

                list.Add(id);
            }
        }

        foreach (var list in overriders.Values) list.Sort(StringComparer.Ordinal);

        var types = new Dictionary<string, TypeNode>(StringComparer.Ordinal);

        using (var command = reader.Db.Command(
                   "SELECT id, base_type_id, json_extract(json, '$.interfaceIds'), type_kind FROM symbols " +
                   "WHERE kind = 'type';"))
        using (var rows = command.ExecuteReader())
        {
            while (rows.Read())
            {
                var id = rows.GetString(0);
                var baseType = rows.IsDBNull(1) ? null : rows.GetString(1);
                var interfaces = rows.IsDBNull(2) ? Array.Empty<string>() : ParseIds(rows.GetString(2));
                var kind = rows.IsDBNull(3) ? null : rows.GetString(3);

                types[id] = new TypeNode(id, baseType, interfaces, kind);
            }
        }

        return new ResolutionIndex(reader, overriders, types);
    }

    public SymbolRec? Symbol(string id) => _symbols.GetOrAdd(id, key => _reader.Symbol(key));

    public IReadOnlyList<string> DirectOverriders(string methodId)
        => _overriders.TryGetValue(methodId, out var list) ? list : Array.Empty<string>();

    /// <summary>Every method that overrides this one, directly or through another override.</summary>
    public IReadOnlyList<string> AllOverriders(string methodId)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue(methodId);

        while (queue.Count > 0)
        {
            foreach (var overrider in DirectOverriders(queue.Dequeue()))
            {
                if (found.Add(overrider)) queue.Enqueue(overrider);
            }
        }

        return found.OrderBy(id => id, StringComparer.Ordinal).ToList();
    }

    public IReadOnlyList<string> ImplementationsOf(string interfaceMemberId)
        => _reader.ImplementationsOf(interfaceMemberId)
            .Select(impl => impl.ImplMemberId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

    public bool IsInterface(string typeId)
        => _types.TryGetValue(typeId, out var node) && node.TypeKind == TypeKinds.Interface;

    public bool IsDelegate(string typeId)
        => _types.TryGetValue(typeId, out var node) && node.TypeKind == TypeKinds.Delegate;

    /// <summary>
    /// 02 §8.2: the same type, or one derived from the other. Both directions count, because a
    /// field declared as the base type can be assigned through a reference to the derived one.
    /// </summary>
    public bool IsRelated(string left, string right)
        => string.Equals(left, right, StringComparison.Ordinal)
           || IsSubtypeOf(left, right)
           || IsSubtypeOf(right, left);

    public bool IsSubtypeOf(string subtype, string supertype) => Supertypes(subtype).Contains(supertype);

    /// <summary>Every base class and interface of a type, transitively. Cached.</summary>
    public IReadOnlySet<string> Supertypes(string typeId)
        => _supertypes.GetOrAdd(typeId, id =>
        {
            var found = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<string>();
            queue.Enqueue(id);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (!_types.TryGetValue(current, out var node)) continue;

                if (node.BaseTypeId is not null && found.Add(node.BaseTypeId)) queue.Enqueue(node.BaseTypeId);

                foreach (var contract in node.InterfaceIds)
                    if (found.Add(contract)) queue.Enqueue(contract);
            }

            return found;
        });

    /// <summary>The type a member is declared in, which is what narrowing compares against.</summary>
    public string? DeclaringTypeOf(string memberId) => Symbol(memberId)?.ContainerId;

    public bool IsAbstract(string memberId)
        => Symbol(memberId)?.Modifiers.Contains(Modifiers.Abstract, StringComparer.Ordinal) ?? false;

    private static IReadOnlyList<string> ParseIds(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }
}

/// <summary>How certain a dispatch is (02 §8.1). A candidate is never promoted to certain.</summary>
public static class DispatchCertainty
{
    public const string Certain = "certain";
    public const string Candidate = "candidate";
    public const string None = "none";

    public static readonly IReadOnlyList<string> All = new[] { Certain, Candidate, None };
}

/// <summary>The outcome of dispatching one callsite (02 §8.1).</summary>
public sealed record DispatchResult
{
    public IReadOnlyList<string> Candidates { get; init; } = Array.Empty<string>();

    public string Certainty { get; init; } = DispatchCertainty.None;

    /// <summary>Set when there is more than one candidate; all of them share it (02 §8.1).</summary>
    public string? CandidateGroup { get; init; }

    /// <summary>True when the candidate limit cut the list (02 §4.2, §8.1).</summary>
    public bool Truncated { get; init; }

    /// <summary>True when the receiver's concrete types reduced the candidate list.</summary>
    public bool Narrowed { get; init; }

    /// <summary>The candidate when there is exactly one; null otherwise.</summary>
    public string? Single => Candidates.Count == 1 ? Candidates[0] : null;

    public static readonly DispatchResult Unresolved = new();
}

/// <summary>
/// Resolves a callsite to the methods it can actually reach (02 §8.1).
/// <para>
/// A virtual or interface call has no single target in the source, and the honest answer is the
/// complete candidate set plus how certain it is. Keeping only the most likely candidate is
/// forbidden (CLAUDE.md §4): the one dropped may be the hop the user is looking for.
/// </para>
/// </summary>
public sealed class Dispatcher
{
    public const string CandidateGroupPrefix = "g_";

    private readonly IndexReader _reader;
    private readonly ResolutionIndex _index;
    private readonly ValueResolver _resolver;
    private readonly TraversalConfig _traversal;

    public Dispatcher(
        IndexReader reader,
        ResolutionIndex index,
        ValueResolver resolver,
        TraversalConfig traversal)
    {
        _reader = reader;
        _index = index;
        _resolver = resolver;
        _traversal = traversal;
    }

    public DispatchResult Dispatch(CallsiteRec callsite, ResolveContext context)
    {
        if (callsite.TargetId is null) return DispatchResult.Unresolved;

        if (!callsite.TargetIsVirtual)
        {
            return new DispatchResult
            {
                Candidates = new[] { callsite.TargetId },
                Certainty = DispatchCertainty.Certain
            };
        }

        var candidates = OpenCandidates(callsite, context);
        var narrowed = Narrow(callsite, candidates, context);

        return Finish(callsite, narrowed.Candidates, narrowed.Narrowed);
    }

    /// <summary>The candidates before the receiver is taken into account (02 §8.1).</summary>
    private IReadOnlyList<string> OpenCandidates(CallsiteRec callsite, ResolveContext context)
    {
        var targetId = callsite.TargetId!;
        var declaringType = _index.DeclaringTypeOf(targetId);

        // A delegate call runs whatever was assigned to the delegate, so the candidates come from
        // the receiver's value rather than from the type hierarchy (02 §8.1).
        if (declaringType is not null && _index.IsDelegate(declaringType))
            return DelegateCandidates(callsite, context);

        if (declaringType is not null && _index.IsInterface(declaringType))
            return _index.ImplementationsOf(targetId);

        var overriders = _index.AllOverriders(targetId);
        if (_index.IsAbstract(targetId)) return overriders;

        return new[] { targetId }
            .Concat(overriders)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
    }

    private IReadOnlyList<string> DelegateCandidates(CallsiteRec callsite, ResolveContext context)
    {
        if (callsite.Receiver is null) return Array.Empty<string>();

        var resolution = _resolver.ResolveConcrete(callsite.Receiver, context);

        return resolution.Values
            .Select(value => value switch
            {
                LambdaValue lambda => lambda.MethodId,
                MethodRefValue method => method.MethodId,
                _ => null
            })
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// 02 §8.1: narrowing by the receiver's concrete types. When nothing is known about the
    /// receiver, the full candidate set is kept — an empty narrowing result never means "no
    /// candidates".
    /// </summary>
    private (IReadOnlyList<string> Candidates, bool Narrowed) Narrow(
        CallsiteRec callsite,
        IReadOnlyList<string> candidates,
        ResolveContext context)
    {
        if (candidates.Count <= 1 || callsite.Receiver is null) return (candidates, false);

        var types = _resolver.ResolveConcrete(callsite.Receiver, context).Types;
        if (types.Count == 0) return (candidates, false);

        var kept = candidates
            .Where(candidate =>
            {
                var declaring = _index.DeclaringTypeOf(candidate);
                return declaring is not null && types.Any(type => _index.IsRelated(declaring, type));
            })
            .ToList();

        return kept.Count == 0 ? (candidates, false) : (kept, kept.Count < candidates.Count);
    }

    private DispatchResult Finish(CallsiteRec callsite, IReadOnlyList<string> candidates, bool narrowed)
    {
        var ordered = candidates.Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToList();
        var truncated = ordered.Count > _traversal.MaxCandidatesPerDispatch;

        if (truncated) ordered = ordered.Take(_traversal.MaxCandidatesPerDispatch).ToList();

        var certainty = ordered.Count switch
        {
            0 => DispatchCertainty.None,
            1 => DispatchCertainty.Certain,
            _ => DispatchCertainty.Candidate
        };

        return new DispatchResult
        {
            Candidates = ordered,
            Certainty = certainty,
            CandidateGroup = ordered.Count > 1
                ? Ids.DerivedId(CandidateGroupPrefix, new { callsite = callsite.Id, candidates = ordered })
                : null,
            Truncated = truncated,
            Narrowed = narrowed
        };
    }
}
