using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ScreenFlow.Core.Ir;

/// <summary>The <c>kind</c> discriminators of 02 §5.4, verbatim.</summary>
public static class ValueKinds
{
    public const string Const = "const";
    public const string Strings = "strings";
    public const string Template = "template";
    public const string TypeOf = "typeOf";
    public const string StaticMember = "staticMember";
    public const string MemberAccess = "memberAccess";
    public const string This = "this";
    public const string Param = "param";
    public const string Local = "local";
    public const string ElementAccess = "elementAccess";
    public const string ObjectCreation = "objectCreation";
    public const string Tuple = "tuple";
    public const string MethodRef = "methodRef";
    public const string Lambda = "lambda";
    public const string CallResult = "callResult";
    public const string Conditional = "conditional";
    public const string Unknown = "unknown";

    public static readonly IReadOnlyList<string> All = new[]
    {
        Const, Strings, Template, TypeOf, StaticMember, MemberAccess, This, Param, Local,
        ElementAccess, ObjectCreation, Tuple, MethodRef, Lambda, CallResult, Conditional, Unknown
    };
}

/// <summary>
/// Where a value came from (02 §5.4). Every argument, assignment right-hand side and initialiser is
/// one of these.
/// <para>
/// The point of the hierarchy is that a value is never flattened into a guess: a string that folds
/// to a constant says <c>const</c>, one that folds to a handful of alternatives says
/// <c>strings</c>, one that does not fold says <c>template</c> or <c>unknown</c>. Nothing in the
/// system is allowed to turn the last two into the first.
/// </para>
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(ConstValue), ValueKinds.Const)]
[JsonDerivedType(typeof(StringsValue), ValueKinds.Strings)]
[JsonDerivedType(typeof(TemplateValue), ValueKinds.Template)]
[JsonDerivedType(typeof(TypeOfValue), ValueKinds.TypeOf)]
[JsonDerivedType(typeof(StaticMemberValue), ValueKinds.StaticMember)]
[JsonDerivedType(typeof(MemberAccessValue), ValueKinds.MemberAccess)]
[JsonDerivedType(typeof(ThisValue), ValueKinds.This)]
[JsonDerivedType(typeof(ParamValue), ValueKinds.Param)]
[JsonDerivedType(typeof(LocalValue), ValueKinds.Local)]
[JsonDerivedType(typeof(ElementAccessValue), ValueKinds.ElementAccess)]
[JsonDerivedType(typeof(ObjectCreationValue), ValueKinds.ObjectCreation)]
[JsonDerivedType(typeof(TupleValue), ValueKinds.Tuple)]
[JsonDerivedType(typeof(MethodRefValue), ValueKinds.MethodRef)]
[JsonDerivedType(typeof(LambdaValue), ValueKinds.Lambda)]
[JsonDerivedType(typeof(CallResultValue), ValueKinds.CallResult)]
[JsonDerivedType(typeof(ConditionalValue), ValueKinds.Conditional)]
[JsonDerivedType(typeof(UnknownValue), ValueKinds.Unknown)]
public abstract record Value
{
    /// <summary>Source text, truncated to <see cref="MaxTextLength"/> characters (02 §5.4).</summary>
    [JsonPropertyName("text")] public string Text { get; init; } = string.Empty;

    [JsonPropertyName("type")] public TypeRef? Type { get; init; }

    public const int MaxTextLength = 200;

    /// <summary>The discriminator this value serialises with.</summary>
    [JsonIgnore] public abstract string Kind { get; }

    /// <summary>Values nested inside this one, so validation and folding can walk the tree.</summary>
    public virtual IEnumerable<Value> Children() => Array.Empty<Value>();

    /// <summary>Truncates source text to the recorded maximum.</summary>
    public static string Truncate(string text)
        => text.Length <= MaxTextLength ? text : text[..MaxTextLength];

    /// <summary>
    /// The folded string alternatives this value denotes, or empty when it does not fold. The one
    /// place in the system that answers "which literal strings can this be", and it answers with
    /// nothing rather than something plausible.
    /// </summary>
    public virtual IReadOnlyList<string> FoldedStrings() => Array.Empty<string>();
}

/// <summary>A compile-time constant: a literal, <c>nameof</c>, a <c>const</c> field, or a folded concatenation.</summary>
public sealed record ConstValue : Value
{
    [JsonIgnore] public override string Kind => ValueKinds.Const;

    /// <summary>string, number, bool or null.</summary>
    [JsonPropertyName("value")] public JsonNode? ConstantValue { get; init; }

    public override IReadOnlyList<string> FoldedStrings()
        => ConstantValue is JsonValue value && value.TryGetValue<string>(out var text)
            ? new[] { text }
            : Array.Empty<string>();

    public static ConstValue Of(string value, string? text = null)
        => new() { ConstantValue = JsonValue.Create(value), Text = Truncate(text ?? "\"" + value + "\"") };

    public static ConstValue Of(int value, string? text = null)
        => new() { ConstantValue = JsonValue.Create(value), Text = Truncate(text ?? value.ToString()) };

    public static ConstValue Of(bool value, string? text = null)
        => new() { ConstantValue = JsonValue.Create(value), Text = Truncate(text ?? (value ? "true" : "false")) };

    public static ConstValue Null(string? text = null)
        => new() { ConstantValue = null, Text = Truncate(text ?? "null") };
}

/// <summary>
/// A string that folds to a known, bounded set of alternatives (02 §5.4). At most 16; beyond that
/// the extractor emits <see cref="TemplateValue"/> instead of an incomplete set.
/// </summary>
public sealed record StringsValue : Value
{
    [JsonIgnore] public override string Kind => ValueKinds.Strings;

    [JsonPropertyName("values")] public IReadOnlyList<string> Values { get; init; } = Array.Empty<string>();

    public const int MaxValues = 16;

    public override IReadOnlyList<string> FoldedStrings() => Values;

    /// <summary>Sorted and de-duplicated, as 02 §5.4 requires, so the record is order-independent.</summary>
    public static StringsValue Of(IEnumerable<string> values, string text = "")
        => new()
        {
            Values = values.Distinct(StringComparer.Ordinal).OrderBy(v => v, StringComparer.Ordinal).ToArray(),
            Text = Truncate(text)
        };
}

/// <summary>A string whose non-constant parts are replaced by <c>{*}</c> (02 §5.4).</summary>
public sealed record TemplateValue : Value
{
    [JsonIgnore] public override string Kind => ValueKinds.Template;

    [JsonPropertyName("template")] public required string Template { get; init; }

    /// <summary>The placeholder a non-constant fragment collapses to.</summary>
    public const string Placeholder = "{*}";
}

/// <summary><c>typeof(T)</c>.</summary>
public sealed record TypeOfValue : Value
{
    [JsonIgnore] public override string Kind => ValueKinds.TypeOf;

    [JsonPropertyName("typeId")] public string? TypeId { get; init; }
}

/// <summary>
/// A static member reference, optionally with a trailing property path:
/// <c>ScreenID.SSAW0907</c> has an empty path, <c>ScreenID.SSAW0907.Id</c> has <c>["Id"]</c>.
/// </summary>
public sealed record StaticMemberValue : Value
{
    [JsonIgnore] public override string Kind => ValueKinds.StaticMember;

    [JsonPropertyName("symbolId")] public string? SymbolId { get; init; }
    [JsonPropertyName("path")] public IReadOnlyList<string> Path { get; init; } = Array.Empty<string>();
}

/// <summary>An instance member access, e.g. <c>dataIni.ButtonClose</c>.</summary>
public sealed record MemberAccessValue : Value
{
    [JsonIgnore] public override string Kind => ValueKinds.MemberAccess;

    [JsonPropertyName("receiver")] public required Value Receiver { get; init; }
    [JsonPropertyName("symbolId")] public string? SymbolId { get; init; }

    public override IEnumerable<Value> Children() => new[] { Receiver };
}

/// <summary><c>this</c>.</summary>
public sealed record ThisValue : Value
{
    [JsonIgnore] public override string Kind => ValueKinds.This;

    [JsonPropertyName("typeId")] public string? TypeId { get; init; }
}

/// <summary>A reference to a parameter of the enclosing method.</summary>
public sealed record ParamValue : Value
{
    [JsonIgnore] public override string Kind => ValueKinds.Param;

    [JsonPropertyName("methodId")] public required string MethodId { get; init; }
    [JsonPropertyName("index")] public required int Index { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
}

/// <summary>
/// A local variable. <see cref="Origin"/> is filled only when the local is assigned exactly once,
/// which is the condition under which its value is actually known (02 §5.4).
/// </summary>
public sealed record LocalValue : Value
{
    [JsonIgnore] public override string Kind => ValueKinds.Local;

    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("origin")] public Value? Origin { get; init; }
    [JsonPropertyName("isLoopIndex")] public bool IsLoopIndex { get; init; }

    /// <summary>For <c>for (i = 0; i &lt; x.Count; i++)</c>, the value of <c>x</c>.</summary>
    [JsonPropertyName("loopBound")] public Value? LoopBound { get; init; }

    public override IEnumerable<Value> Children()
    {
        if (Origin is not null) yield return Origin;
        if (LoopBound is not null) yield return LoopBound;
    }

    public override IReadOnlyList<string> FoldedStrings()
        => Origin?.FoldedStrings() ?? Array.Empty<string>();
}

/// <summary>An indexer access, e.g. <c>request.Request["EventPath"]</c>.</summary>
public sealed record ElementAccessValue : Value
{
    [JsonIgnore] public override string Kind => ValueKinds.ElementAccess;

    [JsonPropertyName("receiver")] public required Value Receiver { get; init; }
    [JsonPropertyName("index")] public required Value Index { get; init; }

    public override IEnumerable<Value> Children() => new[] { Receiver, Index };
}

/// <summary>
/// An object creation, including its object and collection initialisers. Each collection element is
/// the argument list that was passed to <c>Add</c> (02 §5.4).
/// </summary>
public sealed record ObjectCreationValue : Value
{
    [JsonIgnore] public override string Kind => ValueKinds.ObjectCreation;

    [JsonPropertyName("typeId")] public string? TypeId { get; init; }
    [JsonPropertyName("ctorId")] public string? CtorId { get; init; }
    [JsonPropertyName("args")] public IReadOnlyList<Arg> Args { get; init; } = Array.Empty<Arg>();
    [JsonPropertyName("members")] public IReadOnlyList<MemberInit> Members { get; init; } = Array.Empty<MemberInit>();
    [JsonPropertyName("elements")] public IReadOnlyList<IReadOnlyList<Value>> Elements { get; init; } = Array.Empty<IReadOnlyList<Value>>();

    public override IEnumerable<Value> Children()
        => Args.Select(a => a.Value)
            .Concat(Members.Select(m => m.Value))
            .Concat(Elements.SelectMany(e => e));
}

/// <summary>A tuple, including a target-typed <c>new(...)</c> that resolves to a ValueTuple.</summary>
public sealed record TupleValue : Value
{
    [JsonIgnore] public override string Kind => ValueKinds.Tuple;

    [JsonPropertyName("items")] public IReadOnlyList<Value> Items { get; init; } = Array.Empty<Value>();

    public override IEnumerable<Value> Children() => Items;
}

/// <summary>A method group or delegate construction, e.g. <c>new KeySeqFunction(model.X.M)</c>.</summary>
public sealed record MethodRefValue : Value
{
    [JsonIgnore] public override string Kind => ValueKinds.MethodRef;

    [JsonPropertyName("methodId")] public required string MethodId { get; init; }
    [JsonPropertyName("receiver")] public Value? Receiver { get; init; }

    public override IEnumerable<Value> Children()
    {
        if (Receiver is not null) yield return Receiver;
    }
}

/// <summary>A lambda or anonymous method; the body is its own symbol.</summary>
public sealed record LambdaValue : Value
{
    [JsonIgnore] public override string Kind => ValueKinds.Lambda;

    [JsonPropertyName("methodId")] public required string MethodId { get; init; }
}

/// <summary>The result of a call, e.g. <c>x.Query&lt;T&gt;("name")</c> or <c>GetService&lt;I&gt;()</c>.</summary>
public sealed record CallResultValue : Value
{
    [JsonIgnore] public override string Kind => ValueKinds.CallResult;

    [JsonPropertyName("callsiteId")] public string? CallsiteId { get; init; }
    [JsonPropertyName("targetId")] public string? TargetId { get; init; }
    [JsonPropertyName("receiver")] public Value? Receiver { get; init; }
    [JsonPropertyName("args")] public IReadOnlyList<Arg> Args { get; init; } = Array.Empty<Arg>();
    [JsonPropertyName("typeArgs")] public IReadOnlyList<TypeRef> TypeArgs { get; init; } = Array.Empty<TypeRef>();

    public override IEnumerable<Value> Children()
    {
        if (Receiver is not null) yield return Receiver;
        foreach (var arg in Args) yield return arg.Value;
    }
}

/// <summary>
/// A ternary whose branches did not fold into a single <see cref="StringsValue"/>. Both branches are
/// kept: which one runs is a runtime question.
/// </summary>
public sealed record ConditionalValue : Value
{
    [JsonIgnore] public override string Kind => ValueKinds.Conditional;

    [JsonPropertyName("whenTrue")] public required Value WhenTrue { get; init; }
    [JsonPropertyName("whenFalse")] public required Value WhenFalse { get; init; }

    public override IEnumerable<Value> Children() => new[] { WhenTrue, WhenFalse };

    /// <summary>Both branches are possible, so both fold in — never just one.</summary>
    public override IReadOnlyList<string> FoldedStrings()
    {
        var left = WhenTrue.FoldedStrings();
        var right = WhenFalse.FoldedStrings();
        if (left.Count == 0 || right.Count == 0) return Array.Empty<string>();
        return left.Concat(right).Distinct(StringComparer.Ordinal)
            .OrderBy(v => v, StringComparer.Ordinal).ToArray();
    }
}

/// <summary>Anything the extractor could not characterise. Deliberately carries no value.</summary>
public sealed record UnknownValue : Value
{
    [JsonIgnore] public override string Kind => ValueKinds.Unknown;

    public static UnknownValue Of(string text = "") => new() { Text = Truncate(text) };
}
