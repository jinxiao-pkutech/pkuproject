using ScreenFlow.Core.Ir;
using ScreenFlow.UnityPos.Resources;

namespace ScreenFlow.UnityPos.Ui;

/// <summary>One text key bound to a control, and how it was arrived at (02 §9.3 step 4).</summary>
public sealed record TextKeyBinding
{
    public required string Key { get; init; }

    /// <summary>Where it came from, e.g. <c>json:TextTitle</c> or <c>json:ButtonTitle[2]</c>.</summary>
    public required string Source { get; init; }

    public required string Confidence { get; init; }

    public string? File { get; init; }
    public Span? Span { get; init; }
}

/// <summary>
/// Resolves which localization key each control shows (02 §9.3 step 4; 03 §14).
/// <para>
/// Two shapes occur. A direct one, <c>title.LocalizationKey = dataIni.TextTitle</c>, is a fact. An
/// indexed one, <c>listButtons[i]…LocalizationKey = dataIni.ButtonTitle[i]</c>, is a pattern: it is
/// only true because the <c>Add</c> order and the array order line up, so it is recorded as a
/// candidate (02 §9.3). Both conditions of the indexed shape must hold — one alone infers nothing.
/// </para>
/// </summary>
public sealed class TextKeyResolver
{
    /// <summary>Emitted when a screen's initialize JSON is missing.</summary>
    public const string NoInitJsonCode = "I_INIT_JSON_MISSING";

    /// <summary>Emitted when a DTO property names a key the initialize JSON does not have.</summary>
    public const string NoJsonKeyCode = "I_INIT_JSON_KEY_MISSING";

    private readonly UiSection _config;

    public TextKeyResolver(UiSection config) => _config = config;

    public IReadOnlyDictionary<string, IReadOnlyList<TextKeyBinding>> Resolve(
        DomainContext context,
        ViewBinding view,
        IReadOnlyList<ControlBinding> controls,
        IReadOnlyDictionary<string, IReadOnlyList<ControlBinding>> lists)
    {
        var bindings = new Dictionary<string, List<TextKeyBinding>>(StringComparer.Ordinal);
        if (context.Resources is null) return Empty(bindings);

        var json = context.Resources.InitJson.ForView(view.ViewType.Name, _config.ViewSuffix);

        if (json is null)
        {
            context.Diagnose(DiagRec.Info(
                NoInitJsonCode,
                $"[{ControlsAdapter.Feature}] Screen {view.Terminal}/{view.ScreenId} has no " +
                $"initialize JSON for view '{view.ViewType.Name}'; its text keys are unknown.",
                view.ViewType.File,
                view.ViewType.Span?.StartLine));

            return Empty(bindings);
        }

        var byField = controls.ToDictionary(control => control.Field.Id, StringComparer.Ordinal);

        foreach (var method in view.Methods)
        foreach (var assign in context.Reader.AssignsIn(method.Id)
                     .OrderBy(assign => assign.Id, StringComparer.Ordinal))
        {
            if (assign.Target is not MemberAccessValue target || target.SymbolId is null) continue;

            var property = context.Reader.Symbol(target.SymbolId);
            if (property is null || !_config.TextKeyProperties.Contains(property.Name, StringComparer.Ordinal))
                continue;

            var file = context.Reader.Symbol(assign.MethodId)?.File;

            if (TryDirect(context, view, json.Root, assign, target, byField, file, bindings)) continue;

            TryIndexed(context, view, json.Root, assign, target, lists, file, bindings);
        }

        return Empty(bindings);
    }

    /// <summary>
    /// The direct shape: the value is a property of the screen's DTO, and that property's name is
    /// the key in the initialize JSON (02 §9.3 step 4; 03 §14.2).
    /// </summary>
    private bool TryDirect(
        DomainContext context,
        ViewBinding view,
        System.Text.Json.JsonElement json,
        AssignRec assign,
        MemberAccessValue target,
        IReadOnlyDictionary<string, ControlBinding> byField,
        string? file,
        Dictionary<string, List<TextKeyBinding>> bindings)
    {
        if (assign.Value is not MemberAccessValue source || source.SymbolId is null) return false;
        if (!IsDto(source.Receiver)) return false;

        var field = ControlsAdapter.FieldOf(target.Receiver, view);
        if (field is null || !byField.ContainsKey(field.Id)) return false;

        var property = context.Reader.Symbol(source.SymbolId);
        if (property is null) return false;

        var key = InitJsonIndex.Key(json, property.Name);

        if (key is null)
        {
            context.Diagnose(DiagRec.Info(
                NoJsonKeyCode,
                $"[{ControlsAdapter.Feature}] {view.Terminal}/{view.ScreenId}: the initialize JSON " +
                $"has no '{property.Name}' for control field '{field.Name}'.",
                file,
                assign.Span.StartLine));

            return true;
        }

        Add(bindings, field.Id, new TextKeyBinding
        {
            Key = key,
            Source = "json:" + property.Name,
            Confidence = ControlsAdapter.Certain,
            File = file,
            Span = assign.Span
        });

        return true;
    }

    /// <summary>
    /// The indexed shape (02 §9.3 step 4): the value is <c>dto.P[i]</c> where <c>i</c> is a loop
    /// index over the list <c>L</c>, and the target reaches the control through <c>L[i]</c>. Both
    /// halves are required — with only one, nothing is inferred.
    /// </summary>
    private void TryIndexed(
        DomainContext context,
        ViewBinding view,
        System.Text.Json.JsonElement json,
        AssignRec assign,
        MemberAccessValue target,
        IReadOnlyDictionary<string, IReadOnlyList<ControlBinding>> lists,
        string? file,
        Dictionary<string, List<TextKeyBinding>> bindings)
    {
        if (assign.Value is not ElementAccessValue element) return;
        if (element.Receiver is not MemberAccessValue array || array.SymbolId is null) return;
        if (!IsDto(array.Receiver)) return;
        if (element.Index is not LocalValue { IsLoopIndex: true } index) return;

        // The loop runs over the list the control was added to (03 §14.2).
        var boundList = index.LoopBound is null ? null : ControlsAdapter.FieldOf(index.LoopBound, view);
        if (boundList is null || !lists.TryGetValue(boundList.Id, out var members)) return;

        // And the target reaches its control through that same list.
        if (!ReachesThroughList(target.Receiver, boundList.Id, view)) return;

        var property = context.Reader.Symbol(array.SymbolId);
        if (property is null) return;

        for (var i = 0; i < members.Count; i++)
        {
            var key = InitJsonIndex.Key(json, property.Name, i);

            if (key is null)
            {
                context.Diagnose(DiagRec.Info(
                    NoJsonKeyCode,
                    $"[{ControlsAdapter.Feature}] {view.Terminal}/{view.ScreenId}: the initialize " +
                    $"JSON has no '{property.Name}[{i}]' for control '{members[i].Name}'.",
                    file,
                    assign.Span.StartLine));

                continue;
            }

            Add(bindings, members[i].Field.Id, new TextKeyBinding
            {
                Key = key,
                Source = $"json:{property.Name}[{i}]",

                // Inferred from the Add order lining up with the array order, which is a pattern
                // rather than a statement in the source (02 §9.3).
                Confidence = ControlsAdapter.Candidate,
                File = file,
                Span = assign.Span
            });
        }
    }

    /// <summary>Whether the receiver chain indexes into that list.</summary>
    private static bool ReachesThroughList(Value? value, string listFieldId, ViewBinding view)
        => value switch
        {
            null => false,
            ElementAccessValue element
                => ControlsAdapter.FieldOf(element.Receiver, view)?.Id == listFieldId
                   || ReachesThroughList(element.Receiver, listFieldId, view),
            MemberAccessValue member => ReachesThroughList(member.Receiver, listFieldId, view),
            CallResultValue call => ReachesThroughList(call.Receiver, listFieldId, view),
            LocalValue { Origin: not null } local => ReachesThroughList(local.Origin, listFieldId, view),
            _ => false
        };

    /// <summary>True when the value is the screen's element DTO (02 §4.2, <c>elementDtoSuffix</c>).</summary>
    private bool IsDto(Value? value)
        => value?.Type?.FqName.EndsWith(_config.ElementDtoSuffix, StringComparison.Ordinal) ?? false;

    private static void Add(
        Dictionary<string, List<TextKeyBinding>> bindings,
        string fieldId,
        TextKeyBinding binding)
    {
        if (!bindings.TryGetValue(fieldId, out var list))
        {
            list = new List<TextKeyBinding>();
            bindings[fieldId] = list;
        }

        // The same key found twice is one fact; two different keys are both kept (02 §9.3).
        if (!list.Any(existing => existing.Key == binding.Key && existing.Source == binding.Source))
            list.Add(binding);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<TextKeyBinding>> Empty(
        Dictionary<string, List<TextKeyBinding>> bindings)
        => bindings.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<TextKeyBinding>)pair.Value
                .OrderBy(binding => binding.Source, StringComparer.Ordinal)
                .ToList(),
            StringComparer.Ordinal);
}
