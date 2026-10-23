using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ScreenFlow.Core.Config;
using ScreenFlow.Core.Ir;
using ScreenFlow.Core.Storage;
using ScreenFlow.UnityPos.Screens;

namespace ScreenFlow.UnityPos.KeySeq;

/// <summary>One registration method of the key sequence register (02 §4.2).</summary>
public sealed record RegisterMethodSpec
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("stageArgIndex")] public int StageArgIndex { get; init; }
    [JsonPropertyName("tableArgIndex")] public int TableArgIndex { get; init; } = 1;
    [JsonPropertyName("subTableArgIndex")] public int? SubTableArgIndex { get; init; }
}

/// <summary>The <c>keyseq</c> section of <c>unitypos.json</c> (02 §4.2).</summary>
public sealed record KeySeqSection
{
    [JsonPropertyName("recordType")] public required AnchorSpec RecordType { get; init; }
    [JsonPropertyName("recordPsCodeArgIndex")] public int RecordPsCodeArgIndex { get; init; }
    [JsonPropertyName("recordFuncArgIndex")] public int RecordFuncArgIndex { get; init; } = 1;
    [JsonPropertyName("recordNextStageArgIndex")] public int RecordNextStageArgIndex { get; init; } = 2;

    [JsonPropertyName("registerType")] public required AnchorSpec RegisterType { get; init; }

    [JsonPropertyName("registerMethods")]
    public IReadOnlyList<RegisterMethodSpec> RegisterMethods { get; init; } = Array.Empty<RegisterMethodSpec>();

    [JsonPropertyName("executeMethods")]
    public IReadOnlyList<AnchorSpec> ExecuteMethods { get; init; } = Array.Empty<AnchorSpec>();

    [JsonPropertyName("requestPsCodeProperty")] public string RequestPsCodeProperty { get; init; } = "PsCode";
    [JsonPropertyName("stageType")] public AnchorSpec? StageType { get; init; }
    [JsonPropertyName("psCodeType")] public AnchorSpec? PsCodeType { get; init; }
}

/// <summary>
/// The key sequence table and the points that run it (02 §9.5; 03 §11).
/// <para>
/// A key press does not call a handler directly: it looks the handler up in a table keyed by the
/// current stage and the key code, and the stage is run-time state. So the table is recorded as
/// facts, the execution point records the key code it passes, and T18 turns "which handler runs"
/// into a candidate set with a break link rather than a single edge (02 §8.3).
/// </para>
/// </summary>
public sealed class KeySeqAdapter : IDomainAdapter
{
    public const string Feature = "keyseq";
    public const string EdgeKind = "keySeqDispatch";
    public const string EdgePrefix = "de_";
    public const string TransitionPrefix = "ks_";

    /// <summary>Emitted when a registration's stage or table is not the documented shape.</summary>
    public const string RegistrationShapeCode = "I_KEYSEQ_REGISTRATION_SHAPE";

    /// <summary>Emitted when an execution point's key code is not statically known.</summary>
    public const string UnresolvedPsCodeCode = "I_KEYSEQ_PSCODE_UNRESOLVED";

    public string Id => AdapterOrder.KeySeq;

    public void Run(DomainContext context)
    {
        var config = context.Section<KeySeqSection>(context.Domain.Keyseq, "keyseq");

        var registerType = context.Anchors.Resolve(config.RegisterType);
        if (context.AnchorFailed(registerType, Id, Feature)) return;

        var enums = context.Providers.OfType<EnumerationValueProvider>().FirstOrDefault();
        var transitions = Register(context, config, registerType.Require(), enums);

        Execute(context, config, transitions, enums);
    }

    // ------------------------------------------------------------------ registration

    /// <summary>One row of a key sequence table (02 §9.5 step 1; 03 §11.1).</summary>
    private sealed record Transition(string Id, string Stage, string PsCode, string? HandlerMethodId);

    private static IReadOnlyList<Transition> Register(
        DomainContext context,
        KeySeqSection config,
        SymbolRec registerType,
        EnumerationValueProvider? enums)
    {
        var transitions = new List<Transition>();

        foreach (var spec in config.RegisterMethods)
        foreach (var method in Methods(context, registerType, spec.Name))
        foreach (var call in context.Reader.CallsitesTargeting(method.Id)
                     .Where(call => call.Reachable)
                     .OrderBy(call => call.Id, StringComparer.Ordinal))
        {
            var stage = MemberName(context, enums, call, spec.StageArgIndex);
            var table = Table(call, spec.TableArgIndex);
            var subTable = spec.SubTableArgIndex is null
                ? null
                : MemberName(context, enums, call, spec.SubTableArgIndex.Value);

            if (stage is null || table is null)
            {
                context.Diagnose(DiagRec.Info(
                    RegistrationShapeCode,
                    $"[{Feature}] A key sequence registration at " +
                    $"{Describe(context, call)} has no resolvable stage or table.",
                    context.Reader.Symbol(call.CallerId)?.File,
                    call.Span.StartLine));

                continue;
            }

            // An empty table is a registration with no rows, which is a fact in itself (03 §11.1).
            foreach (var element in table.Elements)
            {
                var record = element.OfType<ObjectCreationValue>().FirstOrDefault();
                if (record is null) continue;

                var psCode = MemberName(context, enums, record, config.RecordPsCodeArgIndex);
                if (psCode is null) continue;

                var handler = record.Args.FirstOrDefault(a => a.ParamIndex == config.RecordFuncArgIndex)?.Value
                    is MethodRefValue function
                    ? function.MethodId
                    : null;

                var nextStage = MemberName(context, enums, record, config.RecordNextStageArgIndex);

                var id = Ids.DerivedId(TransitionPrefix, new
                {
                    stage,
                    psCode,
                    handler = handler ?? string.Empty,
                    callsite = call.Id
                });

                transitions.Add(new Transition(id, stage, psCode, handler));

                context.Writer.Add(new KeySeqTransitionRow
                {
                    Id = id,
                    Stage = stage,
                    PsCode = psCode,
                    HandlerMethodId = handler,
                    NextStage = nextStage,
                    SubTable = subTable,
                    EvidenceId = context.EvidenceOf(call)
                });
            }
        }

        return transitions;
    }

    // ------------------------------------------------------------------ execution

    /// <summary>
    /// Every call that runs the key sequence (02 §9.5 step 3). The key code is what narrows the
    /// table; without it the dispatch is every row there is, which is recorded as unresolved.
    /// </summary>
    private static void Execute(
        DomainContext context,
        KeySeqSection config,
        IReadOnlyList<Transition> transitions,
        EnumerationValueProvider? enums)
    {
        foreach (var anchor in config.ExecuteMethods)
        {
            var resolution = context.Anchors.Resolve(anchor);
            if (context.AnchorFailed(resolution, AdapterOrder.KeySeq, Feature)) continue;

            foreach (var method in resolution.Symbols.Where(symbol => symbol.Kind == SymbolKinds.Method))
            foreach (var call in context.Reader.CallsitesTargeting(method.Id)
                         .Where(call => call.Reachable)
                         .OrderBy(call => call.Id, StringComparer.Ordinal))
            {
                var psCode = RequestPsCode(context, config, enums, call);

                var matched = psCode is null
                    ? Array.Empty<string>()
                    : transitions
                        .Where(transition => transition.PsCode == psCode)
                        .Select(transition => transition.Id)
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(id => id, StringComparer.Ordinal)
                        .ToArray();

                if (psCode is null)
                {
                    context.Diagnose(DiagRec.Info(
                        UnresolvedPsCodeCode,
                        $"[{Feature}] The key code of an execution at {Describe(context, call)} is " +
                        "not statically known, so no table row can be matched.",
                        context.Reader.Symbol(call.CallerId)?.File,
                        call.Span.StartLine));
                }

                var json = new JsonObject
                {
                    ["psCode"] = psCode,
                    ["transitions"] = new JsonArray(matched.Select(id => (JsonNode)id!).ToArray()),

                    // The dispatcher itself is understood by this adapter; the traversal records the
                    // candidates instead of walking into it (02 §9.5 step 4).
                    ["domainHandled"] = true
                };

                context.Writer.Add(new DomainEdgeRow
                {
                    Id = Ids.DerivedId(EdgePrefix, new { kind = EdgeKind, callsite = call.Id }),
                    Kind = EdgeKind,
                    CallsiteId = call.Id,
                    FromMethodId = call.CallerId,
                    Json = json.ToJsonString()
                });
            }
        }
    }

    /// <summary>The key code of a request argument, through a local if it was built in one.</summary>
    private static string? RequestPsCode(
        DomainContext context,
        KeySeqSection config,
        EnumerationValueProvider? enums,
        CallsiteRec call)
    {
        var argument = call.Args.FirstOrDefault(a => a.ParamIndex == 0);
        if (argument is null) return null;

        var creation = Creation(argument.Value);
        if (creation is null) return null;

        var member = creation.Members.FirstOrDefault(m => m.Name == config.RequestPsCodeProperty);
        return member is null ? null : MemberName(context, enums, member.Value);
    }

    // ------------------------------------------------------------------ helpers

    private static ObjectCreationValue? Table(CallsiteRec call, int index)
        => Creation(call.Args.FirstOrDefault(a => a.ParamIndex == index)?.Value);

    private static ObjectCreationValue? Creation(Value? value) => value switch
    {
        ObjectCreationValue creation => creation,
        LocalValue { Origin: not null } local => Creation(local.Origin),
        _ => null
    };

    private static string? MemberName(
        DomainContext context,
        EnumerationValueProvider? enums,
        CallsiteRec call,
        int index)
        => MemberName(context, enums, call.Args.FirstOrDefault(a => a.ParamIndex == index)?.Value);

    private static string? MemberName(
        DomainContext context,
        EnumerationValueProvider? enums,
        ObjectCreationValue creation,
        int index)
        => MemberName(context, enums, creation.Args.FirstOrDefault(a => a.ParamIndex == index)?.Value);

    /// <summary>
    /// The enumeration member a value names (03 §5). The member's own name is used, not its
    /// numeric id: two members can share an id, and the name is what a table is read by.
    /// </summary>
    private static string? MemberName(DomainContext context, EnumerationValueProvider? enums, Value? value)
    {
        if (value is not StaticMemberValue member || member.SymbolId is null) return null;

        var known = enums?.Members.GetValueOrDefault(member.SymbolId);
        if (known is not null) return known.Name;

        return context.Reader.Symbol(member.SymbolId)?.Name;
    }

    private static string Describe(DomainContext context, CallsiteRec call)
    {
        var caller = context.Reader.Symbol(call.CallerId);
        return caller?.File is null ? call.Id : $"{caller.File}:{call.Span.StartLine}";
    }

    /// <summary>
    /// The registration methods of that name on the register type or on a type implementing it
    /// (02 §9.5 step 1): the call usually goes through the interface.
    /// </summary>
    private static IEnumerable<SymbolRec> Methods(DomainContext context, SymbolRec registerType, string name)
        => context.Reader.SymbolsByName(name)
            .Where(symbol => symbol.Kind == SymbolKinds.Method)
            .Where(symbol => symbol.ContainerId is not null
                             && (symbol.ContainerId == registerType.Id
                                 || context.Resolution.IsSubtypeOf(symbol.ContainerId, registerType.Id)))
            .OrderBy(symbol => symbol.Id, StringComparer.Ordinal);
}
