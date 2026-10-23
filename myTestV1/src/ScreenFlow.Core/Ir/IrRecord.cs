using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ScreenFlow.Core.Ir;

/// <summary>The ten record kinds of 02 §5.1, verbatim.</summary>
public static class IrRecordKinds
{
    public const string Meta = "meta";
    public const string File = "file";
    public const string Symbol = "symbol";
    public const string Impl = "impl";
    public const string Callsite = "callsite";
    public const string Assign = "assign";
    public const string Creation = "creation";
    public const string Cfg = "cfg";
    public const string Marker = "marker";
    public const string Diag = "diag";

    public static readonly IReadOnlyList<string> All = new[]
    {
        Meta, File, Symbol, Impl, Callsite, Assign, Creation, Cfg, Marker, Diag
    };
}

public static class SymbolKinds
{
    public const string Namespace = "namespace";
    public const string Type = "type";
    public const string Method = "method";
    public const string Field = "field";
    public const string Property = "property";
    public const string Event = "event";
    public const string Local = "local";

    public static readonly IReadOnlyList<string> All = new[]
    {
        Namespace, Type, Method, Field, Property, Event, Local
    };
}

public static class TypeKinds
{
    public const string Class = "class";
    public const string Interface = "interface";
    public const string Struct = "struct";
    public const string Enum = "enum";
    public const string Delegate = "delegate";
    public const string Record = "record";

    public static readonly IReadOnlyList<string> All = new[]
    {
        Class, Interface, Struct, Enum, Delegate, Record
    };
}

public static class Modifiers
{
    public const string Static = "static";
    public const string Abstract = "abstract";
    public const string Virtual = "virtual";
    public const string Override = "override";
    public const string Sealed = "sealed";
    public const string Async = "async";
    public const string Partial = "partial";
    public const string Readonly = "readonly";
    public const string Const = "const";
    public const string Extern = "extern";

    public static readonly IReadOnlyList<string> All = new[]
    {
        Static, Abstract, Virtual, Override, Sealed, Async, Partial, Readonly, Const, Extern
    };
}

public static class Encodings
{
    public const string Utf8Bom = "utf-8-bom";
    public const string Utf8 = "utf-8";
    public const string Cp932 = "cp932";

    public static readonly IReadOnlyList<string> All = new[] { Utf8Bom, Utf8, Cp932 };
}

public static class Derivations
{
    /// <summary>The Unity cache matches the platform the profile declares.</summary>
    public const string Native = "native";

    /// <summary>The cache was produced for another platform; every query result carries the flag.</summary>
    public const string Derived = "derived";

    public static readonly IReadOnlyList<string> All = new[] { Native, Derived };
}

public static class Severities
{
    public const string Error = "error";
    public const string Warning = "warning";
    public const string Info = "info";

    public static readonly IReadOnlyList<string> All = new[] { Error, Warning, Info };
}

public static class MarkerDirections
{
    public const string Begin = "▼";
    public const string End = "▲";

    public static readonly IReadOnlyList<string> All = new[] { Begin, End };
}

/// <summary>
/// One IR record (02 §5). The wire form is <c>{"k":"&lt;kind&gt;","v":{…}}</c>, so the
/// discriminator lives in the envelope rather than inside the payload; <see cref="IrJson"/> owns
/// that mapping.
/// </summary>
public abstract record IrRecord
{
    /// <summary>The envelope's <c>k</c> value for this record.</summary>
    [JsonIgnore] public abstract string K { get; }
}

/// <summary>Run metadata. Exactly one per extraction, emitted first (02 §5.8).</summary>
public sealed record MetaRec : IrRecord
{
    [JsonIgnore] public override string K => IrRecordKinds.Meta;

    [JsonPropertyName("irVersion")] public required string IrVersion { get; init; }
    [JsonPropertyName("extractor")] public required string Extractor { get; init; }
    [JsonPropertyName("profileId")] public required string ProfileId { get; init; }

    /// <summary>Version from <c>ProjectSettings/ProjectVersion.txt</c>.</summary>
    [JsonPropertyName("unityDeclared")] public string? UnityDeclared { get; init; }

    /// <summary>Identity of the compile cache the response files came from.</summary>
    [JsonPropertyName("unityCache")] public string? UnityCache { get; init; }

    [JsonPropertyName("platformDetected")] public string? PlatformDetected { get; init; }
    [JsonPropertyName("derivation")] public required string Derivation { get; init; }
}

/// <summary>One analysed source file (02 §5.8).</summary>
public sealed record FileRec : IrRecord
{
    [JsonIgnore] public override string K => IrRecordKinds.File;

    [JsonPropertyName("path")] public required string Path { get; init; }
    [JsonPropertyName("assembly")] public required string Assembly { get; init; }
    [JsonPropertyName("encoding")] public required string Encoding { get; init; }
    [JsonPropertyName("lineCount")] public required int LineCount { get; init; }
    [JsonPropertyName("contentHash")] public required string ContentHash { get; init; }
}

/// <summary>A declared or referenced symbol (02 §5.2).</summary>
public sealed record SymbolRec : IrRecord
{
    [JsonIgnore] public override string K => IrRecordKinds.Symbol;

    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("kind")] public required string Kind { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }

    /// <summary>Fully qualified; for a method it includes the parameter type signature.</summary>
    [JsonPropertyName("fqName")] public required string FqName { get; init; }

    /// <summary>Containing type or method. A lambda's container is the enclosing method.</summary>
    [JsonPropertyName("containerId")] public string? ContainerId { get; init; }

    [JsonPropertyName("namespace")] public string? Namespace { get; init; }
    [JsonPropertyName("assembly")] public required string Assembly { get; init; }

    /// <summary>True when declared in source; false for a symbol only referenced from metadata.</summary>
    [JsonPropertyName("isSource")] public required bool IsSource { get; init; }

    [JsonPropertyName("file")] public string? File { get; init; }
    [JsonPropertyName("span")] public Span? Span { get; init; }
    [JsonPropertyName("typeKind")] public string? TypeKind { get; init; }
    [JsonPropertyName("baseTypeId")] public string? BaseTypeId { get; init; }
    [JsonPropertyName("interfaceIds")] public IReadOnlyList<string> InterfaceIds { get; init; } = Array.Empty<string>();
    [JsonPropertyName("modifiers")] public IReadOnlyList<string> Modifiers { get; init; } = Array.Empty<string>();
    [JsonPropertyName("typeRef")] public TypeRef? TypeRef { get; init; }
    [JsonPropertyName("returnType")] public TypeRef? ReturnType { get; init; }
    [JsonPropertyName("parameters")] public IReadOnlyList<ParameterInfo> Parameters { get; init; } = Array.Empty<ParameterInfo>();
    [JsonPropertyName("typeParameters")] public IReadOnlyList<string> TypeParameters { get; init; } = Array.Empty<string>();
    [JsonPropertyName("attributes")] public IReadOnlyList<AttributeInfo> Attributes { get; init; } = Array.Empty<AttributeInfo>();

    /// <summary>Field or property initialiser; an initialiser never produces an <c>assign</c>.</summary>
    [JsonPropertyName("initializer")] public Value? Initializer { get; init; }

    [JsonPropertyName("constantValue")] public JsonNode? ConstantValue { get; init; }
    [JsonPropertyName("overridesId")] public string? OverridesId { get; init; }
    [JsonPropertyName("isLambda")] public bool IsLambda { get; init; }
    [JsonPropertyName("isLocalFunction")] public bool IsLocalFunction { get; init; }

    /// <summary>Source-order index of a lambda or local function within its enclosing method.</summary>
    [JsonPropertyName("ordinal")] public int? Ordinal { get; init; }

    /// <summary>False when the symbol could not be fully bound. Never silently promoted.</summary>
    [JsonPropertyName("isCompilerCertain")] public bool IsCompilerCertain { get; init; } = true;

    /// <summary>Every declaration site; a <c>partial</c> type has more than one.</summary>
    [JsonPropertyName("declarations")] public IReadOnlyList<DeclarationInfo> Declarations { get; init; } = Array.Empty<DeclarationInfo>();
}

/// <summary>
/// An interface implementation mapping (02 §5.3), one per interface member of each source type.
/// The implementing member may live on a base class, which is why this is recorded rather than
/// inferred from the type's own members.
/// </summary>
public sealed record ImplRec : IrRecord
{
    [JsonIgnore] public override string K => IrRecordKinds.Impl;

    [JsonPropertyName("typeId")] public required string TypeId { get; init; }
    [JsonPropertyName("interfaceMemberId")] public required string InterfaceMemberId { get; init; }
    [JsonPropertyName("implMemberId")] public required string ImplMemberId { get; init; }
}

/// <summary>A call site (02 §5.5).</summary>
public sealed record CallsiteRec : IrRecord
{
    [JsonIgnore] public override string K => IrRecordKinds.Callsite;

    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("callerId")] public required string CallerId { get; init; }
    [JsonPropertyName("targetId")] public string? TargetId { get; init; }

    /// <summary>Target is an interface member, a virtual method or an abstract method.</summary>
    [JsonPropertyName("targetIsVirtual")] public bool TargetIsVirtual { get; init; }

    [JsonPropertyName("receiver")] public Value? Receiver { get; init; }
    [JsonPropertyName("typeArgs")] public IReadOnlyList<TypeRef> TypeArgs { get; init; } = Array.Empty<TypeRef>();
    [JsonPropertyName("args")] public IReadOnlyList<Arg> Args { get; init; } = Array.Empty<Arg>();
    [JsonPropertyName("isAwaited")] public bool IsAwaited { get; init; }
    [JsonPropertyName("isConditionalAccess")] public bool IsConditionalAccess { get; init; }
    [JsonPropertyName("conditions")] public IReadOnlyList<Condition> Conditions { get; init; } = Array.Empty<Condition>();

    /// <summary>CFG reachability within the method.</summary>
    [JsonPropertyName("reachable")] public bool Reachable { get; init; } = true;

    /// <summary>Source order within the caller, 0-based.</summary>
    [JsonPropertyName("order")] public required int Order { get; init; }

    [JsonPropertyName("span")] public required Span Span { get; init; }
}

/// <summary>
/// An assignment (02 §5.5), including event subscription such as <c>x.clicked += h</c>, where the
/// target is a <see cref="MemberAccessValue"/> pointing at the event symbol.
/// </summary>
public sealed record AssignRec : IrRecord
{
    [JsonIgnore] public override string K => IrRecordKinds.Assign;

    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("methodId")] public required string MethodId { get; init; }
    [JsonPropertyName("target")] public required Value Target { get; init; }

    /// <summary><c>=</c>, <c>+=</c>, <c>-=</c>, <c>??=</c> or another compound operator.</summary>
    [JsonPropertyName("operator")] public required string Operator { get; init; }

    [JsonPropertyName("value")] public required Value Value { get; init; }
    [JsonPropertyName("conditions")] public IReadOnlyList<Condition> Conditions { get; init; } = Array.Empty<Condition>();
    [JsonPropertyName("reachable")] public bool Reachable { get; init; } = true;
    [JsonPropertyName("order")] public required int Order { get; init; }
    [JsonPropertyName("span")] public required Span Span { get; init; }

    public static class Operators
    {
        public const string Assign = "=";
        public const string AddAssign = "+=";
        public const string SubtractAssign = "-=";
        public const string CoalesceAssign = "??=";
    }
}

/// <summary>
/// An object creation (02 §5.5). Recorded once per creation expression even when the same value
/// already appears inside another record, so that "who created what" is answerable directly.
/// </summary>
public sealed record CreationRec : IrRecord
{
    [JsonIgnore] public override string K => IrRecordKinds.Creation;

    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("methodId")] public required string MethodId { get; init; }
    /// <summary>
    /// Declared as <see cref="Ir.Value"/> so the polymorphic <c>kind</c> discriminator is written,
    /// which keeps the wire form identical to any other value. The validator enforces that it is in
    /// fact an <see cref="ObjectCreationValue"/>.
    /// </summary>
    [JsonPropertyName("value")] public required Value Value { get; init; }

    /// <summary>The creation this record describes, or null when the value is of the wrong kind.</summary>
    [JsonIgnore] public ObjectCreationValue? ObjectCreation => Value as ObjectCreationValue;
    [JsonPropertyName("conditions")] public IReadOnlyList<Condition> Conditions { get; init; } = Array.Empty<Condition>();
    [JsonPropertyName("reachable")] public bool Reachable { get; init; } = true;
    [JsonPropertyName("order")] public required int Order { get; init; }
    [JsonPropertyName("span")] public required Span Span { get; init; }
}

/// <summary>Per-method control flow summary (02 §5.8).</summary>
public sealed record CfgRec : IrRecord
{
    [JsonIgnore] public override string K => IrRecordKinds.Cfg;

    [JsonPropertyName("methodId")] public required string MethodId { get; init; }
    [JsonPropertyName("blockCount")] public required int BlockCount { get; init; }

    /// <summary>When true, every condition collected in this method is marked approximate.</summary>
    [JsonPropertyName("hasGoto")] public bool HasGoto { get; init; }

    [JsonPropertyName("unreachableSpans")] public IReadOnlyList<Span> UnreachableSpans { get; init; } = Array.Empty<Span>();
}

/// <summary>
/// A retained change marker (02 §5.8). Reporting metadata only: comment text is never a parsing
/// fact, and the date is stored as written rather than validated.
/// </summary>
public sealed record MarkerRec : IrRecord
{
    [JsonIgnore] public override string K => IrRecordKinds.Marker;

    [JsonPropertyName("file")] public required string File { get; init; }
    [JsonPropertyName("line")] public required int Line { get; init; }
    [JsonPropertyName("dir")] public required string Dir { get; init; }
    [JsonPropertyName("kind")] public required string Kind { get; init; }
    [JsonPropertyName("date")] public string? Date { get; init; }
    [JsonPropertyName("author")] public string? Author { get; init; }
    [JsonPropertyName("issueRefs")] public IReadOnlyList<string> IssueRefs { get; init; } = Array.Empty<string>();
    [JsonPropertyName("text")] public required string Text { get; init; }
}

/// <summary>A diagnostic, carried in the record stream rather than out of band (02 §5.1).</summary>
public sealed record DiagRec : IrRecord
{
    [JsonIgnore] public override string K => IrRecordKinds.Diag;

    [JsonPropertyName("severity")] public required string Severity { get; init; }
    [JsonPropertyName("code")] public required string Code { get; init; }
    [JsonPropertyName("message")] public required string Message { get; init; }
    [JsonPropertyName("file")] public string? File { get; init; }
    [JsonPropertyName("line")] public int? Line { get; init; }

    public static DiagRec Info(string code, string message, string? file = null, int? line = null)
        => new() { Severity = Severities.Info, Code = code, Message = message, File = file, Line = line };

    public static DiagRec Warning(string code, string message, string? file = null, int? line = null)
        => new() { Severity = Severities.Warning, Code = code, Message = message, File = file, Line = line };

    public static DiagRec Error(string code, string message, string? file = null, int? line = null)
        => new() { Severity = Severities.Error, Code = code, Message = message, File = file, Line = line };
}
