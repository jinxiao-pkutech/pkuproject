using System.Text.Json.Serialization;
using ScreenFlow.Core.Config;
using ScreenFlow.Core.Ir;
using ScreenFlow.Core.Storage;
using ScreenFlow.UnityPos.Resources;

namespace ScreenFlow.UnityPos.Ui;

/// <summary>The <c>ui</c> section of <c>unitypos.json</c> (02 §4.2).</summary>
public sealed record UiSection
{
    [JsonPropertyName("controlQueryMethods")]
    public IReadOnlyList<string> ControlQueryMethods { get; init; } = Array.Empty<string>();

    [JsonPropertyName("clickEvents")]
    public IReadOnlyList<string> ClickEvents { get; init; } = Array.Empty<string>();

    [JsonPropertyName("viewEventHandlerField")] public string ViewEventHandlerField { get; init; } = "eventHandler";

    /// <summary>Presenter methods that run when the screen is displayed (02 §9.10).</summary>
    [JsonPropertyName("autoEntryMethods")]
    public IReadOnlyList<string> AutoEntryMethods { get; init; } = Array.Empty<string>();

    /// <summary>Presenter methods that receive scanner input (02 §9.10).</summary>
    [JsonPropertyName("scanEntryMethods")]
    public IReadOnlyList<string> ScanEntryMethods { get; init; } = Array.Empty<string>();

    /// <summary>The call that registers a presenter for scanner input (02 §9.10).</summary>
    [JsonPropertyName("scanRegistrationMethod")]
    public string ScanRegistrationMethod { get; init; } = "SetBaseOnScanEventHandler";

    [JsonPropertyName("textKeyProperties")]
    public IReadOnlyList<string> TextKeyProperties { get; init; } = Array.Empty<string>();

    [JsonPropertyName("initJsonDir")]
    public string InitJsonDir { get; init; } = "Assets/Common/View/Resources/ScreenInitialize";

    [JsonPropertyName("initJsonNamePattern")]
    public string InitJsonNamePattern { get; init; } = "{ViewWithoutSuffix}_Initialize.json";

    [JsonPropertyName("viewSuffix")] public string ViewSuffix { get; init; } = "_View";
    [JsonPropertyName("elementDtoSuffix")] public string ElementDtoSuffix { get; init; } = "_Element_DTO";

    /// <summary>Method name to constraint kind, e.g. <c>SetInputMaxValue</c> to <c>maxValue</c>.</summary>
    [JsonPropertyName("constraintMethods")]
    public IReadOnlyDictionary<string, string> ConstraintMethods { get; init; } = new Dictionary<string, string>();

    [JsonPropertyName("constraintValueArgIndex")] public int ConstraintValueArgIndex { get; init; }
    [JsonPropertyName("constraintMessageArgIndex")] public int ConstraintMessageArgIndex { get; init; } = 1;

    [JsonPropertyName("errorMessageGetters")]
    public IReadOnlyList<AnchorSpec> ErrorMessageGetters { get; init; } = Array.Empty<AnchorSpec>();

    [JsonPropertyName("errorCodeType")] public AnchorSpec? ErrorCodeType { get; init; }
}

/// <summary>One registered screen's view, with the source types its members come from (02 §9.3).</summary>
public sealed record ViewBinding
{
    public required string Terminal { get; init; }
    public required string ScreenId { get; init; }
    public required SymbolRec ViewType { get; init; }

    /// <summary>The view type and its source base types; a control field may be declared on either.</summary>
    public required IReadOnlyList<SymbolRec> TypeChain { get; init; }

    /// <summary>Every method of the chain, lambdas and local functions included.</summary>
    public required IReadOnlyList<SymbolRec> Methods { get; init; }

    /// <summary>Fields of the chain, by id, which is what a control is held in.</summary>
    public required IReadOnlyDictionary<string, SymbolRec> Fields { get; init; }

    public string? UxmlPath { get; init; }
}

/// <summary>One control of a screen: the field that holds it and the name it was queried by.</summary>
public sealed record ControlBinding
{
    public required SymbolRec Field { get; init; }
    public required string Name { get; init; }
    public string? ControlTypeId { get; init; }
    public string? ControlTypeName { get; init; }
    public string? UxmlPath { get; init; }
    public string? HierarchyPath { get; init; }
    public string? File { get; init; }
    public Span? Span { get; init; }
}

/// <summary>
/// Controls, their handlers, their text keys and their input constraints (02 §9.3).
/// <para>
/// This is the adapter that makes a screen readable: which buttons it has, what each one says, what
/// happens when it is pressed, and what the input rules are. Every one of those is assembled from
/// several facts — a field, a query call, a delegate assignment, a JSON key, a string table — and
/// anything that does not join up is recorded as unresolved rather than filled in.
/// </para>
/// </summary>
public sealed class ControlsAdapter : IDomainAdapter
{
    public const string Feature = "ui-bindings";

    /// <summary>Emitted when a control has no element of that name in the screen's UXML.</summary>
    public const string NoUxmlMatchCode = "I_CONTROL_NOT_IN_UXML";

    /// <summary>Emitted when a registered screen has no UXML file at all.</summary>
    public const string NoUxmlFileCode = "I_VIEW_HAS_NO_UXML";

    public const string Certain = "certain";
    public const string Candidate = "candidate";
    public const string Unresolved = "unresolved";

    public string Id => AdapterOrder.UiBindings;

    public void Run(DomainContext context)
    {
        var config = context.Section<UiSection>(context.Domain.Ui, "ui");

        if (context.Resources is null)
        {
            context.Diagnose(DiagRec.Warning(
                NoUxmlFileCode,
                $"[{Feature}] The resource readers did not run, so no control can be cross-checked " +
                "against its UXML or given a text."));
        }

        var textKeys = new TextKeyResolver(config);
        var constraints = new ConstraintsAdapter(config);

        foreach (var view in Views(context, config))
        {
            context.Ct.ThrowIfCancellationRequested();

            var controls = Controls(context, config, view);
            var lists = ListIndices(context, view, controls);

            var keys = textKeys.Resolve(context, view, controls, lists);

            WriteControls(context, view, controls, keys);
            WriteHandlers(context, view, config, controls);

            constraints.Run(context, view, controls);
        }
    }

    // ------------------------------------------------------------------ views

    /// <summary>The registered screens that name a view type (02 §9.1), each with its type chain.</summary>
    private static IEnumerable<ViewBinding> Views(DomainContext context, UiSection config)
    {
        foreach (var screen in context.Reader.Screens()
                     .Where(screen => screen.Registered && screen.ViewTypeId is not null)
                     .OrderBy(screen => screen.Terminal, StringComparer.Ordinal)
                     .ThenBy(screen => screen.ScreenId, StringComparer.Ordinal))
        {
            var viewType = context.Reader.Symbol(screen.ViewTypeId!);
            if (viewType is null) continue;

            var chain = TypeChain(context, viewType);
            var methods = Methods(context, chain);

            var fields = chain
                .SelectMany(type => context.Reader.SymbolsInContainer(type.Id))
                .Where(symbol => symbol.Kind == SymbolKinds.Field || symbol.Kind == SymbolKinds.Property)
                .GroupBy(symbol => symbol.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

            yield return new ViewBinding
            {
                Terminal = screen.Terminal,
                ScreenId = screen.ScreenId,
                ViewType = viewType,
                TypeChain = chain,
                Methods = methods,
                Fields = fields,
                UxmlPath = context.Resources?.Uxml.FileOfView(viewType.Name)
            };
        }
    }

    /// <summary>The view type and the source types it inherits from (02 §9.3; 03 §7).</summary>
    private static IReadOnlyList<SymbolRec> TypeChain(DomainContext context, SymbolRec viewType)
    {
        var chain = new List<SymbolRec>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var current = viewType; current is not null && current.IsSource; )
        {
            if (!seen.Add(current.Id)) break;
            chain.Add(current);

            current = current.BaseTypeId is null ? null : context.Reader.Symbol(current.BaseTypeId);
        }

        return chain;
    }

    private static IReadOnlyList<SymbolRec> Methods(DomainContext context, IReadOnlyList<SymbolRec> chain)
    {
        var methods = chain
            .SelectMany(type => context.Reader.SymbolsInContainer(type.Id))
            .Where(symbol => symbol.Kind == SymbolKinds.Method)
            .ToList();

        // A control is often wired inside a lambda, and the lambda is a method of its own whose
        // container is the method that created it (02 §6.6).
        var nested = methods
            .SelectMany(method => context.Reader.SymbolsInContainer(method.Id))
            .Where(symbol => symbol.Kind == SymbolKinds.Method)
            .ToList();

        return methods.Concat(nested)
            .GroupBy(symbol => symbol.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(symbol => symbol.Id, StringComparer.Ordinal)
            .ToList();
    }

    // ------------------------------------------------------------------ controls

    /// <summary>
    /// 02 §9.3 step 1: a field assigned from a control query call, with the queried name folded to
    /// a constant. A query whose name does not fold is no control: there is nothing to call it.
    /// </summary>
    private static IReadOnlyList<ControlBinding> Controls(
        DomainContext context,
        UiSection config,
        ViewBinding view)
    {
        var controls = new List<ControlBinding>();

        foreach (var field in view.Fields.Values.OrderBy(field => field.Id, StringComparer.Ordinal))
        {
            foreach (var assign in context.Reader.AssignsTargeting(field.Id))
            {
                var value = Unwrap(assign.Value);
                if (value is not CallResultValue call) continue;

                var target = call.TargetId is null ? null : context.Reader.Symbol(call.TargetId);
                if (target is null) continue;

                if (!config.ControlQueryMethods.Contains(target.Name, StringComparer.Ordinal)) continue;

                var argument = call.Args.FirstOrDefault(a => a.ParamIndex == 0);
                var folded = argument?.Value.FoldedStrings() ?? Array.Empty<string>();
                if (folded.Count != 1) continue;

                var controlType = call.TypeArgs.FirstOrDefault() ?? field.TypeRef;
                var caller = context.Reader.Symbol(assign.MethodId);

                controls.Add(new ControlBinding
                {
                    Field = field,
                    Name = folded[0],
                    ControlTypeId = controlType?.Id,
                    ControlTypeName = controlType?.FqName,
                    File = caller?.File,
                    Span = assign.Span
                });

                break;
            }
        }

        return Locate(context, view, controls);
    }

    /// <summary>
    /// Cross-checks each control against the screen's UXML (02 §9.3 step 1). A control with no
    /// matching element is still recorded — the code queries it, and that is the fact — with the
    /// mismatch reported.
    /// </summary>
    private static IReadOnlyList<ControlBinding> Locate(
        DomainContext context,
        ViewBinding view,
        IReadOnlyList<ControlBinding> controls)
    {
        if (context.Resources is null || view.UxmlPath is null)
        {
            if (controls.Count > 0 && context.Resources is not null)
            {
                context.Diagnose(DiagRec.Info(
                    NoUxmlFileCode,
                    $"[{Feature}] No UXML named '{view.ViewType.Name}.uxml' for screen " +
                    $"{view.Terminal}/{view.ScreenId}; its controls have no hierarchy path.",
                    view.ViewType.File,
                    view.ViewType.Span?.StartLine));
            }

            return controls;
        }

        var located = new List<ControlBinding>();

        foreach (var control in controls)
        {
            var matches = context.Resources.Uxml.ByName(view.UxmlPath, control.Name);

            if (matches.Count == 0)
            {
                context.Diagnose(DiagRec.Info(
                    NoUxmlMatchCode,
                    $"[{Feature}] Control '{control.Name}' of {view.Terminal}/{view.ScreenId} is " +
                    $"queried in code but has no element of that name in '{view.UxmlPath}'.",
                    control.File,
                    control.Span?.StartLine));

                located.Add(control with { UxmlPath = view.UxmlPath });
                continue;
            }

            // Several elements share the name; the path is only stated when it is unambiguous
            // (03 §14.3).
            located.Add(control with
            {
                UxmlPath = view.UxmlPath,
                HierarchyPath = matches.Count == 1 ? matches[0].HierarchyPath : null
            });
        }

        return located;
    }

    /// <summary>
    /// 02 §9.3 step 2: <c>L.Add(F)</c> in source order gives <c>L[i] = F</c>, which is what binds a
    /// button row to its labels (03 §14.2).
    /// </summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<ControlBinding>> ListIndices(
        DomainContext context,
        ViewBinding view,
        IReadOnlyList<ControlBinding> controls)
    {
        var byField = controls.ToDictionary(control => control.Field.Id, StringComparer.Ordinal);
        var lists = new Dictionary<string, List<ControlBinding>>(StringComparer.Ordinal);

        var calls = view.Methods
            .SelectMany(method => context.Reader.CallsitesIn(method.Id))
            .Where(call => call.Reachable)
            .OrderBy(call => call.CallerId, StringComparer.Ordinal)
            .ThenBy(call => call.Order);

        foreach (var call in calls)
        {
            var target = call.TargetId is null ? null : context.Reader.Symbol(call.TargetId);
            if (target?.Name != "Add") continue;

            var listField = FieldOf(call.Receiver, view);
            if (listField is null) continue;

            var argument = call.Args.FirstOrDefault(a => a.ParamIndex == 0);
            if (argument is null) continue;

            var added = FieldOf(argument.Value, view);
            if (added is null || !byField.TryGetValue(added.Id, out var control)) continue;

            if (!lists.TryGetValue(listField.Id, out var members))
            {
                members = new List<ControlBinding>();
                lists[listField.Id] = members;
            }

            members.Add(control);
        }

        return lists.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<ControlBinding>)pair.Value,
            StringComparer.Ordinal);
    }

    // ------------------------------------------------------------------ writing

    private static void WriteControls(
        DomainContext context,
        ViewBinding view,
        IReadOnlyList<ControlBinding> controls,
        IReadOnlyDictionary<string, IReadOnlyList<TextKeyBinding>> keys)
    {
        foreach (var control in controls.OrderBy(control => control.Name, StringComparer.Ordinal))
        {
            var bound = keys.TryGetValue(control.Field.Id, out var found)
                ? found
                : Array.Empty<TextKeyBinding>();

            // Several keys for one control are all kept; choosing one would be a guess (02 §9.3).
            var confidence = bound.Count switch
            {
                0 => Unresolved,
                1 => bound[0].Confidence,
                _ => Candidate
            };

            context.Writer.Add(new ControlRow
            {
                Terminal = view.Terminal,
                ScreenId = view.ScreenId,
                ControlId = control.Name,
                FieldId = control.Field.Id,
                ControlType = control.ControlTypeName,
                UxmlPath = control.UxmlPath,
                HierarchyPath = control.HierarchyPath,
                TextKey = bound.Count == 0 ? null : string.Join(", ", bound.Select(b => b.Key)),
                TextKeySource = bound.Count == 0 ? Unresolved : string.Join(", ", bound.Select(b => b.Source)),
                TextKeyConfidence = confidence,
                EvidenceId = context.Evidence(control.File, control.Span)
            });
        }
    }

    /// <summary>
    /// 02 §9.3 step 3: the handler a control's event is subscribed with, resolved through the
    /// delegate field it was assigned to (03 §9, E-19).
    /// </summary>
    private static void WriteHandlers(
        DomainContext context,
        ViewBinding view,
        UiSection config,
        IReadOnlyList<ControlBinding> controls)
    {
        var byField = controls.ToDictionary(control => control.Field.Id, StringComparer.Ordinal);

        foreach (var method in view.Methods)
        foreach (var assign in context.Reader.AssignsIn(method.Id)
                     .Where(assign => assign.Operator == AssignRec.Operators.AddAssign)
                     .OrderBy(assign => assign.Id, StringComparer.Ordinal))
        {
            if (assign.Target is not MemberAccessValue target || target.SymbolId is null) continue;

            var member = context.Reader.Symbol(target.SymbolId);
            if (member is null || !config.ClickEvents.Contains(member.Name, StringComparer.Ordinal)) continue;

            var field = FieldOf(target.Receiver, view);
            if (field is null || !byField.TryGetValue(field.Id, out var control)) continue;

            var resolved = context.Values.ResolveConcrete(assign.Value, context.Resolve(view.ViewType.Id));

            var handlers = resolved.Values
                .Select(value => value switch
                {
                    LambdaValue lambda => lambda.MethodId,
                    MethodRefValue reference => reference.MethodId,
                    _ => null
                })
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();

            foreach (var handler in handlers)
            {
                context.Writer.Add(new ControlHandlerRow
                {
                    Terminal = view.Terminal,
                    ScreenId = view.ScreenId,
                    ControlId = control.Name,
                    EventName = member.Name,
                    HandlerMethodId = handler,
                    Confidence = handlers.Count == 1 && resolved.Complete ? Certain : Candidate,
                    EvidenceId = context.Evidence(context.Reader.Symbol(assign.MethodId)?.File, assign.Span)
                });
            }
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>A local stands for what was assigned to it, which is where a control usually comes from.</summary>
    internal static Value Unwrap(Value value)
        => value is LocalValue { Origin: not null } local ? Unwrap(local.Origin) : value;

    /// <summary>
    /// The view field a value reaches through its receiver chain, or null. This is how
    /// <c>this.btnOther.clicked</c> is recognised as the <c>btnOther</c> control.
    /// </summary>
    internal static SymbolRec? FieldOf(Value? value, ViewBinding view)
    {
        switch (value)
        {
            case null:
                return null;

            case MemberAccessValue member when member.SymbolId is not null
                                               && view.Fields.TryGetValue(member.SymbolId, out var field):
                return field;

            case StaticMemberValue staticMember when staticMember.SymbolId is not null
                                                     && view.Fields.TryGetValue(staticMember.SymbolId, out var stat):
                return stat;

            case MemberAccessValue member:
                return FieldOf(member.Receiver, view);

            case ElementAccessValue element:
                return FieldOf(element.Receiver, view);

            case CallResultValue call:
                return FieldOf(call.Receiver, view);

            case LocalValue { Origin: not null } local:
                return FieldOf(local.Origin, view);

            default:
                return null;
        }
    }
}
