using System.Text.Json.Nodes;

namespace ScreenFlow.Core.Ir;

/// <summary>
/// Structural validation of an IR record (T02 §5): required fields, enum vocabularies, id prefixes
/// and value nesting depth.
/// <para>
/// Written by hand rather than against a JSON Schema library, so the core takes no dependency for
/// it; <c>schema/ir-1.schema.json</c> is the published contract for extractor authors, and a test
/// compares the two so they cannot drift.
/// </para>
/// </summary>
public static class IrValidator
{
    /// <summary>Maximum value nesting depth (02 §5.4); deeper expressions are emitted as <c>unknown</c>.</summary>
    public const int MaxValueDepth = 6;

    /// <summary>The only IR version this core accepts.</summary>
    public const string SupportedIrVersion = "1";

    public static IReadOnlyList<string> Validate(IrRecord record)
    {
        var problems = new List<string>();

        switch (record)
        {
            case MetaRec meta: ValidateMeta(meta, problems); break;
            case FileRec file: ValidateFile(file, problems); break;
            case SymbolRec symbol: ValidateSymbol(symbol, problems); break;
            case ImplRec impl: ValidateImpl(impl, problems); break;
            case CallsiteRec callsite: ValidateCallsite(callsite, problems); break;
            case AssignRec assign: ValidateAssign(assign, problems); break;
            case CreationRec creation: ValidateCreation(creation, problems); break;
            case CfgRec cfg: ValidateCfg(cfg, problems); break;
            case MarkerRec marker: ValidateMarker(marker, problems); break;
            case DiagRec diag: ValidateDiag(diag, problems); break;
            default: problems.Add($"unknown record type {record.GetType().Name}."); break;
        }

        return problems;
    }

    public static bool IsValid(IrRecord record) => Validate(record).Count == 0;

    // ---------------------------------------------------------------- per record

    private static void ValidateMeta(MetaRec meta, List<string> problems)
    {
        if (meta.IrVersion != SupportedIrVersion)
            problems.Add($"meta.irVersion is '{meta.IrVersion}', expected '{SupportedIrVersion}'.");
        RequireText(problems, "meta.extractor", meta.Extractor);
        RequireText(problems, "meta.profileId", meta.ProfileId);
        RequireEnum(problems, "meta.derivation", meta.Derivation, Derivations.All);
    }

    private static void ValidateFile(FileRec file, List<string> problems)
    {
        RequireText(problems, "file.path", file.Path);
        RequireRelativePath(problems, "file.path", file.Path);
        RequireText(problems, "file.assembly", file.Assembly);
        RequireEnum(problems, "file.encoding", file.Encoding, Encodings.All);
        if (file.LineCount < 0) problems.Add($"file.lineCount is {file.LineCount}, expected zero or more.");
        RequireText(problems, "file.contentHash", file.ContentHash);
    }

    private static void ValidateSymbol(SymbolRec symbol, List<string> problems)
    {
        RequireId(problems, "symbol.id", symbol.Id, Ids.SymbolPrefix);
        RequireEnum(problems, "symbol.kind", symbol.Kind, SymbolKinds.All);
        RequireText(problems, "symbol.name", symbol.Name);
        RequireText(problems, "symbol.fqName", symbol.FqName);
        RequireText(problems, "symbol.assembly", symbol.Assembly);

        RequireOptionalId(problems, "symbol.containerId", symbol.ContainerId, Ids.SymbolPrefix);
        RequireOptionalId(problems, "symbol.baseTypeId", symbol.BaseTypeId, Ids.SymbolPrefix);
        RequireOptionalId(problems, "symbol.overridesId", symbol.OverridesId, Ids.SymbolPrefix);
        for (var i = 0; i < symbol.InterfaceIds.Count; i++)
            RequireId(problems, $"symbol.interfaceIds[{i}]", symbol.InterfaceIds[i], Ids.SymbolPrefix);

        if (symbol.TypeKind is not null)
            RequireEnum(problems, "symbol.typeKind", symbol.TypeKind, TypeKinds.All);

        foreach (var modifier in symbol.Modifiers)
        {
            if (!Modifiers.All.Contains(modifier, StringComparer.Ordinal))
                problems.Add($"symbol.modifiers contains '{modifier}', which is not a known modifier.");
        }

        // A source symbol without a location cannot be cited as evidence, which every fact needs.
        if (symbol.IsSource)
        {
            if (string.IsNullOrEmpty(symbol.File)) problems.Add("symbol.file is required when isSource is true.");
            if (symbol.Span is null) problems.Add("symbol.span is required when isSource is true.");
        }

        if (symbol.File is not null) RequireRelativePath(problems, "symbol.file", symbol.File);
        if (symbol.Span is not null) RequireSpan(problems, "symbol.span", symbol.Span);

        for (var i = 0; i < symbol.Parameters.Count; i++)
        {
            var parameter = symbol.Parameters[i];
            RequireText(problems, $"symbol.parameters[{i}].name", parameter.Name);
            RequireEnum(problems, $"symbol.parameters[{i}].refKind", parameter.RefKind, RefKinds.All);
            ValidateTypeRef(problems, $"symbol.parameters[{i}].type", parameter.Type);
        }

        if (symbol.TypeRef is not null) ValidateTypeRef(problems, "symbol.typeRef", symbol.TypeRef);
        if (symbol.ReturnType is not null) ValidateTypeRef(problems, "symbol.returnType", symbol.ReturnType);

        for (var i = 0; i < symbol.Attributes.Count; i++)
        {
            RequireText(problems, $"symbol.attributes[{i}].typeFqName", symbol.Attributes[i].TypeFqName);
            for (var j = 0; j < symbol.Attributes[i].Args.Count; j++)
                ValidateValue(problems, $"symbol.attributes[{i}].args[{j}]", symbol.Attributes[i].Args[j], 1);
        }

        if (symbol.Initializer is not null)
            ValidateValue(problems, "symbol.initializer", symbol.Initializer, 1);

        if ((symbol.IsLambda || symbol.IsLocalFunction) && symbol.Ordinal is null)
            problems.Add("symbol.ordinal is required for a lambda or local function.");
        if (symbol.Ordinal is < 0)
            problems.Add($"symbol.ordinal is {symbol.Ordinal}, expected zero or more.");

        for (var i = 0; i < symbol.Declarations.Count; i++)
        {
            RequireText(problems, $"symbol.declarations[{i}].file", symbol.Declarations[i].File);
            RequireSpan(problems, $"symbol.declarations[{i}].span", symbol.Declarations[i].Span);
        }
    }

    private static void ValidateImpl(ImplRec impl, List<string> problems)
    {
        RequireId(problems, "impl.typeId", impl.TypeId, Ids.SymbolPrefix);
        RequireId(problems, "impl.interfaceMemberId", impl.InterfaceMemberId, Ids.SymbolPrefix);
        RequireId(problems, "impl.implMemberId", impl.ImplMemberId, Ids.SymbolPrefix);
    }

    private static void ValidateCallsite(CallsiteRec callsite, List<string> problems)
    {
        RequireId(problems, "callsite.id", callsite.Id, Ids.CallsitePrefix);
        RequireId(problems, "callsite.callerId", callsite.CallerId, Ids.SymbolPrefix);
        RequireOptionalId(problems, "callsite.targetId", callsite.TargetId, Ids.SymbolPrefix);
        if (callsite.Order < 0) problems.Add($"callsite.order is {callsite.Order}, expected zero or more.");
        RequireSpan(problems, "callsite.span", callsite.Span);

        if (callsite.Receiver is not null) ValidateValue(problems, "callsite.receiver", callsite.Receiver, 1);
        ValidateArgs(problems, "callsite.args", callsite.Args);
        for (var i = 0; i < callsite.TypeArgs.Count; i++)
            ValidateTypeRef(problems, $"callsite.typeArgs[{i}]", callsite.TypeArgs[i]);
        ValidateConditions(problems, "callsite.conditions", callsite.Conditions);
    }

    private static void ValidateAssign(AssignRec assign, List<string> problems)
    {
        RequireId(problems, "assign.id", assign.Id, Ids.AssignPrefix);
        RequireId(problems, "assign.methodId", assign.MethodId, Ids.SymbolPrefix);
        RequireText(problems, "assign.operator", assign.Operator);
        if (assign.Order < 0) problems.Add($"assign.order is {assign.Order}, expected zero or more.");
        RequireSpan(problems, "assign.span", assign.Span);
        ValidateValue(problems, "assign.target", assign.Target, 1);
        ValidateValue(problems, "assign.value", assign.Value, 1);
        ValidateConditions(problems, "assign.conditions", assign.Conditions);
    }

    private static void ValidateCreation(CreationRec creation, List<string> problems)
    {
        RequireId(problems, "creation.id", creation.Id, Ids.CreationPrefix);
        RequireId(problems, "creation.methodId", creation.MethodId, Ids.SymbolPrefix);
        if (creation.Order < 0) problems.Add($"creation.order is {creation.Order}, expected zero or more.");
        RequireSpan(problems, "creation.span", creation.Span);

        if (creation.Value is not ObjectCreationValue)
            problems.Add($"creation.value must be of kind '{ValueKinds.ObjectCreation}', found '{creation.Value.Kind}'.");

        ValidateValue(problems, "creation.value", creation.Value, 1);
        ValidateConditions(problems, "creation.conditions", creation.Conditions);
    }

    private static void ValidateCfg(CfgRec cfg, List<string> problems)
    {
        RequireId(problems, "cfg.methodId", cfg.MethodId, Ids.SymbolPrefix);
        if (cfg.BlockCount < 0) problems.Add($"cfg.blockCount is {cfg.BlockCount}, expected zero or more.");
        for (var i = 0; i < cfg.UnreachableSpans.Count; i++)
            RequireSpan(problems, $"cfg.unreachableSpans[{i}]", cfg.UnreachableSpans[i]);
    }

    private static void ValidateMarker(MarkerRec marker, List<string> problems)
    {
        RequireText(problems, "marker.file", marker.File);
        RequireRelativePath(problems, "marker.file", marker.File);
        if (marker.Line < 1) problems.Add($"marker.line is {marker.Line}, expected 1 or more.");
        RequireEnum(problems, "marker.dir", marker.Dir, MarkerDirections.All);
        RequireText(problems, "marker.kind", marker.Kind);
    }

    private static void ValidateDiag(DiagRec diag, List<string> problems)
    {
        RequireEnum(problems, "diag.severity", diag.Severity, Severities.All);
        RequireText(problems, "diag.code", diag.Code);
        RequireText(problems, "diag.message", diag.Message);
        if (diag.Line is < 1) problems.Add($"diag.line is {diag.Line}, expected 1 or more.");
    }

    // ---------------------------------------------------------------- values

    private static void ValidateArgs(List<string> problems, string path, IReadOnlyList<Arg> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i].ParamIndex < 0)
                problems.Add($"{path}[{i}].paramIndex is {args[i].ParamIndex}, expected zero or more.");
            ValidateValue(problems, $"{path}[{i}].value", args[i].Value, 1);
        }

        // Arguments are aligned by parameter, so two of them cannot claim the same slot.
        foreach (var duplicate in args.GroupBy(a => a.ParamIndex).Where(g => g.Count() > 1))
            problems.Add($"{path} has {duplicate.Count()} arguments at paramIndex {duplicate.Key}.");
    }

    private static void ValidateConditions(List<string> problems, string path, IReadOnlyList<Condition> conditions)
    {
        for (var i = 0; i < conditions.Count; i++)
        {
            var condition = conditions[i];
            RequireEnum(problems, $"{path}[{i}].kind", condition.Kind, ConditionKinds.All);
            RequireEnum(problems, $"{path}[{i}].mode", condition.Mode, ConditionModes.Values);
            if (condition.Span is not null) RequireSpan(problems, $"{path}[{i}].span", condition.Span);

            for (var j = 0; j < condition.Tests.Count; j++)
            {
                RequireEnum(problems, $"{path}[{i}].tests[{j}].op", condition.Tests[j].Op, StringTestOps.All);
                ValidateValue(problems, $"{path}[{i}].tests[{j}].subject", condition.Tests[j].Subject, 1);
            }
        }
    }

    private static void ValidateTypeRef(List<string> problems, string path, TypeRef typeRef)
    {
        RequireText(problems, path + ".fqName", typeRef.FqName);
        RequireOptionalId(problems, path + ".id", typeRef.Id, Ids.SymbolPrefix);
        for (var i = 0; i < typeRef.Args.Count; i++)
            ValidateTypeRef(problems, $"{path}.args[{i}]", typeRef.Args[i]);
    }

    private static void ValidateValue(List<string> problems, string path, Value value, int depth)
    {
        if (depth > MaxValueDepth)
        {
            problems.Add(
                $"{path}: value nesting depth {depth} exceeds the limit of {MaxValueDepth}; " +
                $"the extractor must emit '{ValueKinds.Unknown}' instead of a deeper tree.");
            return;
        }

        if (value.Text.Length > Value.MaxTextLength)
            problems.Add($"{path}.text is {value.Text.Length} characters, limit is {Value.MaxTextLength}.");

        if (value.Type is not null) ValidateTypeRef(problems, path + ".type", value.Type);

        switch (value)
        {
            case ConstValue constant:
                if (constant.ConstantValue is JsonObject or JsonArray)
                    problems.Add($"{path}.value must be a string, number, boolean or null.");
                break;

            case StringsValue strings:
                if (strings.Values.Count == 0)
                    problems.Add($"{path}.values is empty; a value that folds to nothing is not 'strings'.");
                if (strings.Values.Count > StringsValue.MaxValues)
                {
                    problems.Add(
                        $"{path}.values has {strings.Values.Count} entries, limit is {StringsValue.MaxValues}; " +
                        $"the extractor must emit '{ValueKinds.Template}' instead of a truncated set.");
                }
                if (strings.Values.Distinct(StringComparer.Ordinal).Count() != strings.Values.Count)
                    problems.Add($"{path}.values contains duplicates; it must be de-duplicated.");
                if (!strings.Values.SequenceEqual(strings.Values.OrderBy(v => v, StringComparer.Ordinal), StringComparer.Ordinal))
                    problems.Add($"{path}.values is not sorted; sorting is what keeps derived ids stable.");
                break;

            case TemplateValue template:
                RequireText(problems, path + ".template", template.Template);
                break;

            case ParamValue param:
                RequireId(problems, path + ".methodId", param.MethodId, Ids.SymbolPrefix);
                RequireText(problems, path + ".name", param.Name);
                if (param.Index < 0) problems.Add($"{path}.index is {param.Index}, expected zero or more.");
                break;

            case LocalValue local:
                RequireText(problems, path + ".name", local.Name);
                break;

            case MethodRefValue methodRef:
                RequireId(problems, path + ".methodId", methodRef.MethodId, Ids.SymbolPrefix);
                break;

            case LambdaValue lambda:
                RequireId(problems, path + ".methodId", lambda.MethodId, Ids.SymbolPrefix);
                break;

            case CallResultValue callResult:
                RequireOptionalId(problems, path + ".callsiteId", callResult.CallsiteId, Ids.CallsitePrefix);
                RequireOptionalId(problems, path + ".targetId", callResult.TargetId, Ids.SymbolPrefix);
                ValidateArgs(problems, path + ".args", callResult.Args);
                for (var i = 0; i < callResult.TypeArgs.Count; i++)
                    ValidateTypeRef(problems, $"{path}.typeArgs[{i}]", callResult.TypeArgs[i]);
                break;

            case ObjectCreationValue creation:
                RequireOptionalId(problems, path + ".typeId", creation.TypeId, Ids.SymbolPrefix);
                RequireOptionalId(problems, path + ".ctorId", creation.CtorId, Ids.SymbolPrefix);
                ValidateArgs(problems, path + ".args", creation.Args);
                for (var i = 0; i < creation.Members.Count; i++)
                {
                    RequireText(problems, $"{path}.members[{i}].name", creation.Members[i].Name);
                    RequireOptionalId(problems, $"{path}.members[{i}].symbolId", creation.Members[i].SymbolId, Ids.SymbolPrefix);
                }
                break;

            case StaticMemberValue staticMember:
                RequireOptionalId(problems, path + ".symbolId", staticMember.SymbolId, Ids.SymbolPrefix);
                break;

            case MemberAccessValue memberAccess:
                RequireOptionalId(problems, path + ".symbolId", memberAccess.SymbolId, Ids.SymbolPrefix);
                break;

            case TypeOfValue typeOf:
                RequireOptionalId(problems, path + ".typeId", typeOf.TypeId, Ids.SymbolPrefix);
                break;

            case ThisValue thisValue:
                RequireOptionalId(problems, path + ".typeId", thisValue.TypeId, Ids.SymbolPrefix);
                break;
        }

        foreach (var child in value.Children())
            ValidateValue(problems, path + "." + child.Kind, child, depth + 1);
    }

    /// <summary>The deepest value nesting level in a tree; the root counts as 1.</summary>
    public static int DepthOf(Value value)
    {
        var deepest = 1;
        foreach (var child in value.Children())
            deepest = Math.Max(deepest, 1 + DepthOf(child));
        return deepest;
    }

    // ---------------------------------------------------------------- primitives

    private static void RequireText(List<string> problems, string path, string? value)
    {
        if (string.IsNullOrEmpty(value)) problems.Add($"{path} is required and must not be empty.");
    }

    private static void RequireEnum(List<string> problems, string path, string value, IReadOnlyList<string> allowed)
    {
        if (!allowed.Contains(value, StringComparer.Ordinal))
            problems.Add($"{path} is '{value}', expected one of {string.Join(", ", allowed)}.");
    }

    private static void RequireId(List<string> problems, string path, string value, string prefix)
    {
        if (string.IsNullOrEmpty(value)) { problems.Add($"{path} is required."); return; }
        if (!Ids.HasPrefix(value, prefix))
            problems.Add($"{path} is '{value}', expected prefix '{prefix}' and {Ids.HashLength} following characters.");
    }

    private static void RequireOptionalId(List<string> problems, string path, string? value, string prefix)
    {
        if (value is null) return;
        RequireId(problems, path, value, prefix);
    }

    private static void RequireSpan(List<string> problems, string path, Span span)
    {
        if (!span.IsValid)
            problems.Add($"{path} is not a valid span ({span.ToIdPart()}); positions are 1-based and must not run backwards.");
    }

    /// <summary>
    /// Paths in the IR are project-root relative with forward slashes (02 §11.3). An absolute path
    /// would leak the build machine's layout into every query result.
    /// </summary>
    private static void RequireRelativePath(List<string> problems, string path, string value)
    {
        if (value.Length == 0) return;
        if (value.Contains('\\'))
            problems.Add($"{path} is '{value}'; paths must use forward slashes.");
        if (value.StartsWith('/') || (value.Length > 1 && value[1] == ':'))
            problems.Add($"{path} is '{value}'; paths must be project-root relative, not absolute.");
    }
}
