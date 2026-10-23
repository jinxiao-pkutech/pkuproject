using System.Text.Json.Serialization;

namespace ScreenFlow.Core.Ir;

/// <summary>A source span, 1-based in both line and column (02 §5.2).</summary>
public sealed record Span
{
    [JsonPropertyName("sl")] public required int StartLine { get; init; }
    [JsonPropertyName("sc")] public required int StartColumn { get; init; }
    [JsonPropertyName("el")] public required int EndLine { get; init; }
    [JsonPropertyName("ec")] public required int EndColumn { get; init; }

    public static Span Of(int startLine, int startColumn, int endLine, int endColumn)
        => new() { StartLine = startLine, StartColumn = startColumn, EndLine = endLine, EndColumn = endColumn };

    /// <summary>The <c>sl:sc-el:ec</c> form used by the evidence id (02 §5.6).</summary>
    public string ToIdPart() => $"{StartLine}:{StartColumn}-{EndLine}:{EndColumn}";

    public bool IsValid => StartLine >= 1 && StartColumn >= 1 && EndLine >= StartLine
                           && (EndLine > StartLine || EndColumn >= StartColumn);

    /// <summary>True when this span fully contains <paramref name="inner"/>.</summary>
    public bool Contains(Span inner)
    {
        var startsBefore = StartLine < inner.StartLine
                           || (StartLine == inner.StartLine && StartColumn <= inner.StartColumn);
        var endsAfter = EndLine > inner.EndLine
                        || (EndLine == inner.EndLine && EndColumn >= inner.EndColumn);
        return startsBefore && endsAfter;
    }
}

/// <summary>
/// A type reference (02 §5.2). <see cref="Id"/> is null for a type the extractor could not bind to
/// a symbol, in which case only the name is known — and that is said rather than guessed around.
/// </summary>
public sealed record TypeRef
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("fqName")] public required string FqName { get; init; }
    [JsonPropertyName("args")] public IReadOnlyList<TypeRef> Args { get; init; } = Array.Empty<TypeRef>();

    public static TypeRef Named(string fqName, string? id = null)
        => new() { FqName = fqName, Id = id };
}

/// <summary>Parameter passing mode.</summary>
public static class RefKinds
{
    public const string None = "none";
    public const string Ref = "ref";
    public const string Out = "out";
    public const string In = "in";

    public static readonly IReadOnlyList<string> All = new[] { None, Ref, Out, In };
}

/// <summary>A method parameter as recorded on a <c>symbol</c> record (02 §5.2).</summary>
public sealed record ParameterInfo
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("type")] public required TypeRef Type { get; init; }
    [JsonPropertyName("refKind")] public string RefKind { get; init; } = RefKinds.None;
}

/// <summary>An attribute applied to a symbol (02 §5.2).</summary>
public sealed record AttributeInfo
{
    [JsonPropertyName("typeFqName")] public required string TypeFqName { get; init; }
    [JsonPropertyName("args")] public IReadOnlyList<Value> Args { get; init; } = Array.Empty<Value>();
}

/// <summary>One declaration site of a symbol; a <c>partial</c> type has several (02 §5.2).</summary>
public sealed record DeclarationInfo
{
    [JsonPropertyName("file")] public required string File { get; init; }
    [JsonPropertyName("span")] public required Span Span { get; init; }
}

/// <summary>
/// An argument, aligned by <em>parameter</em> rather than by source position (02 §5.4): named
/// arguments are placed at their parameter's index, and an omitted optional parameter simply does
/// not appear.
/// </summary>
public sealed record Arg
{
    [JsonPropertyName("paramName")] public string? ParamName { get; init; }
    [JsonPropertyName("paramIndex")] public required int ParamIndex { get; init; }
    [JsonPropertyName("value")] public required Value Value { get; init; }
}

/// <summary>A member assigned by an object or collection initialiser (02 §5.4, objectCreation).</summary>
public sealed record MemberInit
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("symbolId")] public string? SymbolId { get; init; }
    [JsonPropertyName("value")] public required Value Value { get; init; }
}

/// <summary>String comparison operators collected into <see cref="StringTest"/> (02 §5.7).</summary>
public static class StringTestOps
{
    public const string EqualsOp = "equals";
    public const string NotEquals = "notEquals";
    public const string Contains = "contains";
    public const string StartsWith = "startsWith";
    public const string EndsWith = "endsWith";
    public const string SwitchLabel = "switchLabel";

    public static readonly IReadOnlyList<string> All = new[]
    {
        EqualsOp, NotEquals, Contains, StartsWith, EndsWith, SwitchLabel
    };
}

/// <summary>
/// One string comparison found inside a guard (02 §5.7). Only string tests are collected, because
/// those are what identify an event path, a topic or a screen id at a branch.
/// </summary>
public sealed record StringTest
{
    [JsonPropertyName("subject")] public required Value Subject { get; init; }
    [JsonPropertyName("op")] public required string Op { get; init; }
    [JsonPropertyName("literal")] public required string Literal { get; init; }
}

public static class ConditionKinds
{
    public const string If = "if";
    public const string SwitchCase = "switchCase";
    public const string SwitchDefault = "switchDefault";
    public const string Ternary = "ternary";
    public const string Loop = "loop";
    public const string Catch = "catch";
    public const string LogicalRight = "logicalRight";

    public static readonly IReadOnlyList<string> All = new[]
    {
        If, SwitchCase, SwitchDefault, Ternary, Loop, Catch, LogicalRight
    };
}

public static class ConditionModes
{
    /// <summary><c>A &amp;&amp; B</c>, or a single condition.</summary>
    public const string All = "all";

    /// <summary><c>A || B</c>.</summary>
    public const string Any = "any";

    public static readonly IReadOnlyList<string> Values = new[] { All, Any };
}

/// <summary>
/// A guard governing a callsite, assignment or creation (02 §5.7).
/// <para>
/// <see cref="Approximate"/> is set for every condition in a method containing <c>goto</c> or a
/// label: the structured ancestor chain is then incomplete, and saying so is the alternative to
/// presenting a partial guard set as the whole truth.
/// </para>
/// </summary>
public sealed record Condition
{
    [JsonPropertyName("kind")] public required string Kind { get; init; }
    [JsonPropertyName("polarity")] public bool Polarity { get; init; } = true;
    [JsonPropertyName("mode")] public string Mode { get; init; } = ConditionModes.All;
    [JsonPropertyName("text")] public required string Text { get; init; }
    [JsonPropertyName("tests")] public IReadOnlyList<StringTest> Tests { get; init; } = Array.Empty<StringTest>();
    [JsonPropertyName("span")] public Span? Span { get; init; }
    [JsonPropertyName("approximate")] public bool Approximate { get; init; }
}
