using System.Text.Json;
using ScreenFlow.Core.Ir;
using ScreenFlow.Core.Storage;
using ScreenFlow.UnityPos.Screens;

namespace ScreenFlow.UnityPos.Ui;

/// <summary>One error code an input constraint reports with (02 §9.3 step 6; 03 §13.3).</summary>
public sealed record ErrorCodeBinding(string? Name, int? Value, string Confidence);

/// <summary>
/// Input constraints and the error codes they report (02 §9.3 step 6).
/// <para>
/// The error code is three hops away from the constraint: the call passes a DTO property, the
/// property was assigned from an error-message getter, and the getter's argument is the code. The
/// fixture's comments name the codes the other way round (03 §13.3, E-22) — the code is what counts,
/// and a comment is never a parsing fact (CLAUDE.md §4).
/// </para>
/// </summary>
public sealed class ConstraintsAdapter
{
    /// <summary>Id prefix of a constraint row; derived ids are a prefix plus a hash (02 §5.6).</summary>
    public const string IdPrefix = "ic_";

    /// <summary>Emitted when a constraint's error message cannot be traced to a code.</summary>
    public const string NoErrorCodeCode = "I_CONSTRAINT_ERROR_CODE_UNRESOLVED";

    private readonly UiSection _config;

    public ConstraintsAdapter(UiSection config) => _config = config;

    public void Run(DomainContext context, ViewBinding view, IReadOnlyList<ControlBinding> controls)
    {
        if (_config.ConstraintMethods.Count == 0) return;

        var getters = Getters(context);
        var codes = ErrorCodes(context);

        foreach (var method in view.Methods)
        foreach (var call in context.Reader.CallsitesIn(method.Id)
                     .Where(call => call.Reachable)
                     .OrderBy(call => call.Id, StringComparer.Ordinal))
        {
            var target = call.TargetId is null ? null : context.Reader.Symbol(call.TargetId);
            if (target is null) continue;
            if (!_config.ConstraintMethods.TryGetValue(target.Name, out var kind)) continue;

            var control = ControlsAdapter.FieldOf(call.Receiver, view);
            var value = ConstantOf(call, _config.ConstraintValueArgIndex);
            var file = context.Reader.Symbol(call.CallerId)?.File;

            var bindings = ErrorCodesOf(context, call, getters, codes);

            if (bindings.Count == 0)
            {
                context.Diagnose(DiagRec.Info(
                    NoErrorCodeCode,
                    $"[{ControlsAdapter.Feature}] {view.Terminal}/{view.ScreenId}: the message of " +
                    $"'{target.Name}' does not trace to an error code.",
                    file,
                    call.Span.StartLine));

                Write(context, view, call, method, kind, value, control, null, file);
                continue;
            }

            // Several codes reach the same property: every one of them is recorded (02 §9.3).
            foreach (var binding in bindings) Write(context, view, call, method, kind, value, control, binding, file);
        }
    }

    private void Write(
        DomainContext context,
        ViewBinding view,
        CallsiteRec call,
        SymbolRec method,
        string kind,
        string? value,
        SymbolRec? control,
        ErrorCodeBinding? code,
        string? file)
    {
        var conditions = call.Conditions.Count == 0
            ? null
            : JsonSerializer.Serialize(call.Conditions, IrJson.Options);

        context.Writer.Add(new InputConstraintRow
        {
            Id = Ids.DerivedId(IdPrefix, new
            {
                terminal = view.Terminal,
                screen = view.ScreenId,
                callsite = call.Id,
                kind,
                code = code?.Name ?? string.Empty
            }),
            Terminal = view.Terminal,
            ScreenId = view.ScreenId,
            MethodId = method.Id,
            ControlFieldId = control?.Id,
            Kind = kind,
            Value = value,
            ErrorCodeName = code?.Name,
            ErrorCodeValue = code?.Value,
            ErrorConfidence = code?.Confidence ?? ControlsAdapter.Unresolved,
            ConditionsJson = conditions,
            EvidenceId = context.Evidence(file, call.Span)
        });
    }

    /// <summary>
    /// The error codes behind a constraint's message argument (02 §9.3 step 6): the property it
    /// names, every assignment to that property, and the getter argument inside each one.
    /// </summary>
    private IReadOnlyList<ErrorCodeBinding> ErrorCodesOf(
        DomainContext context,
        CallsiteRec call,
        IReadOnlySet<string> getters,
        IReadOnlyDictionary<string, EnumerationMember> codes)
    {
        var argument = call.Args.FirstOrDefault(a => a.ParamIndex == _config.ConstraintMessageArgIndex);
        if (argument?.Value is not MemberAccessValue message || message.SymbolId is null)
            return Array.Empty<ErrorCodeBinding>();

        var bindings = new List<ErrorCodeBinding>();

        foreach (var assign in context.Fields.AssignmentsTo(message.SymbolId))
        {
            var call2 = GetterCall(assign.Value, getters);
            if (call2 is null) continue;

            var code = CodeOf(context, call2, codes);
            if (code is not null) bindings.Add(code);
        }

        // Two assignments naming the same code are one answer; two different codes are both kept.
        var distinct = bindings
            .GroupBy(binding => binding.Name ?? binding.Value?.ToString() ?? string.Empty, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(binding => binding.Value)
            .ThenBy(binding => binding.Name, StringComparer.Ordinal)
            .ToList();

        return distinct.Count > 1
            ? distinct.Select(binding => binding with { Confidence = ControlsAdapter.Candidate }).ToList()
            : distinct;
    }

    /// <summary>
    /// The getter call inside an assigned value, through the property that reads its result —
    /// <c>OnGetErrPopupType1(code).LocalizeMsg</c> (02 §9.3 step 6).
    /// </summary>
    private static CallResultValue? GetterCall(Value value, IReadOnlySet<string> getters) => value switch
    {
        CallResultValue call when call.TargetId is not null && getters.Contains(call.TargetId) => call,
        MemberAccessValue member => GetterCall(member.Receiver, getters),
        LocalValue { Origin: not null } local => GetterCall(local.Origin, getters),
        _ => null
    };

    /// <summary>
    /// The error code a getter was called with: an enumeration member, its <c>Id</c>, or a plain
    /// number looked back up among the members (02 §9.3 step 6).
    /// </summary>
    private static ErrorCodeBinding? CodeOf(
        DomainContext context,
        CallResultValue call,
        IReadOnlyDictionary<string, EnumerationMember> codes)
    {
        var argument = call.Args.FirstOrDefault(a => a.ParamIndex == 0);
        if (argument is null) return null;

        switch (argument.Value)
        {
            case StaticMemberValue member when member.SymbolId is not null
                                               && codes.TryGetValue(member.SymbolId, out var found):
                return new ErrorCodeBinding(found.Name, found.ValueId, ControlsAdapter.Certain);

            case ConstValue { ConstantValue: not null } constant
                when constant.ConstantValue.GetValueKind() == JsonValueKind.Number:
            {
                var number = constant.ConstantValue.GetValue<int>();

                // A number can belong to more than one member (03 §5 has two codes with id 0), and
                // then neither name is certain.
                var named = codes.Values
                    .Where(member => member.ValueId == number)
                    .OrderBy(member => member.Name, StringComparer.Ordinal)
                    .ToList();

                return named.Count switch
                {
                    0 => new ErrorCodeBinding(null, number, ControlsAdapter.Candidate),
                    1 => new ErrorCodeBinding(named[0].Name, number, ControlsAdapter.Certain),
                    _ => new ErrorCodeBinding(
                        string.Join(" | ", named.Select(member => member.Name)),
                        number,
                        ControlsAdapter.Candidate)
                };
            }

            default:
                return null;
        }
    }

    /// <summary>The methods configured as error message getters (02 §4.2, <c>errorMessageGetters</c>).</summary>
    private IReadOnlySet<string> Getters(DomainContext context)
    {
        var getters = new HashSet<string>(StringComparer.Ordinal);

        foreach (var anchor in _config.ErrorMessageGetters)
        {
            var resolution = context.Anchors.Resolve(anchor);

            if (!resolution.Resolved)
            {
                context.Diagnose(DiagRec.Warning(
                    resolution.ErrorCode!,
                    $"[{ControlsAdapter.Feature}] {resolution.Message}"));

                continue;
            }

            foreach (var symbol in resolution.Symbols.Where(symbol => symbol.Kind == SymbolKinds.Method))
                getters.Add(symbol.Id);
        }

        return getters;
    }

    /// <summary>
    /// The members of the error code enumeration, as the enumerations adapter recorded them
    /// (02 §9.2). Read from the provider rather than by name, so the codes are the ones this build
    /// actually found.
    /// </summary>
    private IReadOnlyDictionary<string, EnumerationMember> ErrorCodes(DomainContext context)
    {
        var provider = context.Providers.OfType<EnumerationValueProvider>().FirstOrDefault();
        if (provider is null || _config.ErrorCodeType is null)
            return new Dictionary<string, EnumerationMember>(StringComparer.Ordinal);

        var resolution = context.Anchors.Resolve(_config.ErrorCodeType);

        if (!resolution.Resolved)
        {
            context.Diagnose(DiagRec.Warning(
                resolution.ErrorCode!,
                $"[{ControlsAdapter.Feature}] {resolution.Message}"));

            return new Dictionary<string, EnumerationMember>(StringComparer.Ordinal);
        }

        var typeId = resolution.Require().Id;

        return provider.Members
            .Where(pair => pair.Value.TypeId == typeId)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }

    /// <summary>The constraint's limit, as a constant. Null when it is not one (02 §7.3).</summary>
    private static string? ConstantOf(CallsiteRec call, int paramIndex)
    {
        var argument = call.Args.FirstOrDefault(a => a.ParamIndex == paramIndex);
        if (argument?.Value is not ConstValue { ConstantValue: not null } constant) return null;

        return constant.ConstantValue.GetValueKind() switch
        {
            JsonValueKind.String => constant.ConstantValue.GetValue<string>(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => constant.ConstantValue.ToJsonString(),
            _ => null
        };
    }
}
