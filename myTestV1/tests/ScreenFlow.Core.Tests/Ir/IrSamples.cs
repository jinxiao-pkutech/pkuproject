using System.Text.Json.Nodes;
using ScreenFlow.Core.Ir;

namespace ScreenFlow.Core.Tests.IrTests;

/// <summary>
/// One well-formed example of every record kind and every value kind. Kept in one place so the
/// round-trip, validation and schema tests all exercise the same material, and so a new kind added
/// to the model without an example here shows up as a failing coverage assertion.
/// </summary>
internal static class IrSamples
{
    public const string Profile = "fixture";
    public const string Assembly = "Assembly-CSharp";
    public const string File = "Assets/Fixture/Sample.cs";

    public static string SymbolId(string key) => Ids.SymbolId(Profile, Assembly, key);

    public static readonly string TypeId = SymbolId("T:Fixture.Sample");
    public static readonly string MethodId = SymbolId("M:Fixture.Sample.Run");
    public static readonly string FieldId = SymbolId("F:Fixture.Sample.Field");
    public static readonly string LambdaId = SymbolId(Ids.LambdaKey("M:Fixture.Sample.Run", 0));
    public static readonly string CallsiteId = Ids.CallsiteId(MethodId, File, 120);
    public static readonly string AssignId = Ids.AssignId(MethodId, File, 160);
    public static readonly string CreationId = Ids.CreationId(MethodId, File, 200);

    private static Span SampleSpan => Span.Of(10, 5, 10, 40);

    /// <summary>Every value kind of 02 §5.4, keyed by its discriminator.</summary>
    public static IReadOnlyDictionary<string, Value> AllValues { get; } = BuildValues();

    private static Dictionary<string, Value> BuildValues()
    {
        var receiver = new ThisValue { TypeId = TypeId, Text = "this" };

        return new Dictionary<string, Value>(StringComparer.Ordinal)
        {
            [ValueKinds.Const] = ConstValue.Of("PosRegister.SSAW0281.Other"),
            [ValueKinds.Strings] = StringsValue.Of(
                new[] { "ToSSAW0907.employee", "ToSSAW0907" },
                "\"ToSSAW0907\" + (c ? \"\" : \".employee\")"),
            [ValueKinds.Template] = new TemplateValue
            {
                Template = "shared-bus-{*}-events",
                Text = "$\"shared-bus-{GetRegNo()}-events\""
            },
            [ValueKinds.TypeOf] = new TypeOfValue { TypeId = TypeId, Text = "typeof(SSAW0001_View)" },
            [ValueKinds.StaticMember] = new StaticMemberValue
            {
                SymbolId = FieldId,
                Path = new[] { "Id" },
                Text = "ScreenID.SSAW0907.Id"
            },
            [ValueKinds.MemberAccess] = new MemberAccessValue
            {
                Receiver = receiver,
                SymbolId = FieldId,
                Text = "this.model"
            },
            [ValueKinds.This] = receiver,
            [ValueKinds.Param] = new ParamValue
            {
                MethodId = MethodId, Index = 1, Name = "eventPath", Text = "eventPath"
            },
            [ValueKinds.Local] = new LocalValue
            {
                Name = "path",
                Origin = ConstValue.Of("Common.Display"),
                IsLoopIndex = false,
                Text = "path"
            },
            [ValueKinds.ElementAccess] = new ElementAccessValue
            {
                Receiver = receiver,
                Index = ConstValue.Of("EventPath"),
                Text = "request.Request[\"EventPath\"]"
            },
            [ValueKinds.ObjectCreation] = SampleObjectCreation,
            [ValueKinds.Tuple] = new TupleValue
            {
                Items = new Value[]
                {
                    new TypeOfValue { TypeId = TypeId, Text = "typeof(V)" },
                    new TypeOfValue { TypeId = TypeId, Text = "typeof(P)" }
                },
                Text = "(typeof(V), typeof(P))"
            },
            [ValueKinds.MethodRef] = new MethodRefValue
            {
                MethodId = MethodId, Receiver = receiver, Text = "model.X.M"
            },
            [ValueKinds.Lambda] = new LambdaValue { MethodId = LambdaId, Text = "() => { }" },
            [ValueKinds.CallResult] = new CallResultValue
            {
                CallsiteId = CallsiteId,
                TargetId = MethodId,
                Receiver = receiver,
                Args = new[]
                {
                    new Arg { ParamIndex = 0, ParamName = "name", Value = ConstValue.Of("BtnOk") }
                },
                TypeArgs = new[] { TypeRef.Named("UnityEngine.UIElements.Button") },
                Text = "root.Q<Button>(\"BtnOk\")"
            },
            [ValueKinds.Conditional] = new ConditionalValue
            {
                WhenTrue = ConstValue.Of("a"),
                WhenFalse = ConstValue.Of("b"),
                Text = "c ? a : b"
            },
            [ValueKinds.Unknown] = UnknownValue.Of("GetSomethingOpaque()")
        };
    }

    public static ObjectCreationValue SampleObjectCreation => new()
    {
        TypeId = TypeId,
        CtorId = MethodId,
        Args = new[] { new Arg { ParamIndex = 0, ParamName = "id", Value = ConstValue.Of(25) } },
        Members = new[]
        {
            new MemberInit { Name = "EventID", SymbolId = FieldId, Value = ConstValue.Of("Common.Display") }
        },
        Elements = new[]
        {
            new Value[] { ConstValue.Of("key"), ConstValue.Of(1) }
        },
        Text = "new SocketRequestData(25){ EventID = \"Common.Display\" }"
    };

    public static Condition SampleCondition => new()
    {
        Kind = ConditionKinds.If,
        Polarity = true,
        Mode = ConditionModes.All,
        Text = "eventPath.Equals(\"PosRegister.SSAW0281.Other\")",
        Tests = new[]
        {
            new StringTest
            {
                Subject = new ParamValue { MethodId = MethodId, Index = 1, Name = "eventPath", Text = "eventPath" },
                Op = StringTestOps.EqualsOp,
                Literal = "PosRegister.SSAW0281.Other"
            }
        },
        Span = SampleSpan
    };

    /// <summary>One record of every kind of 02 §5.1, keyed by its discriminator.</summary>
    public static IReadOnlyDictionary<string, IrRecord> AllRecords { get; } = BuildRecords();

    private static Dictionary<string, IrRecord> BuildRecords() => new(StringComparer.Ordinal)
    {
        [IrRecordKinds.Meta] = new MetaRec
        {
            IrVersion = IrValidator.SupportedIrVersion,
            Extractor = "csharp-roslyn",
            ProfileId = Profile,
            UnityDeclared = "2022.3.68f1",
            UnityCache = "Library/Bee/artifacts/1900b0aE.dag",
            PlatformDetected = "android",
            Derivation = Derivations.Native
        },
        [IrRecordKinds.File] = new FileRec
        {
            Path = File,
            Assembly = Assembly,
            Encoding = Encodings.Cp932,
            LineCount = 42,
            ContentHash = Ids.Sha256Hex("sample")
        },
        [IrRecordKinds.Symbol] = new SymbolRec
        {
            Id = MethodId,
            Kind = SymbolKinds.Method,
            Name = "Run",
            FqName = "Fixture.Sample.Run(System.String)",
            ContainerId = TypeId,
            Namespace = "Fixture",
            Assembly = Assembly,
            IsSource = true,
            File = File,
            Span = SampleSpan,
            Modifiers = new[] { Modifiers.Async, Modifiers.Static },
            ReturnType = TypeRef.Named("System.Threading.Tasks.Task"),
            Parameters = new[]
            {
                new ParameterInfo { Name = "eventPath", Type = TypeRef.Named("System.String"), RefKind = RefKinds.None }
            },
            TypeParameters = new[] { "T" },
            Attributes = new[]
            {
                new AttributeInfo
                {
                    TypeFqName = "System.ObsoleteAttribute",
                    Args = new Value[] { ConstValue.Of("use Run2") }
                }
            },
            ConstantValue = JsonValue.Create("const-payload"),
            OverridesId = SymbolId("M:Fixture.Base.Run"),
            IsCompilerCertain = true,
            Declarations = new[] { new DeclarationInfo { File = File, Span = SampleSpan } }
        },
        [IrRecordKinds.Impl] = new ImplRec
        {
            TypeId = TypeId,
            InterfaceMemberId = SymbolId("M:Fixture.IRunner.Run"),
            ImplMemberId = MethodId
        },
        [IrRecordKinds.Callsite] = new CallsiteRec
        {
            Id = CallsiteId,
            CallerId = MethodId,
            TargetId = SymbolId("M:Fixture.TcpModel.SslSend"),
            TargetIsVirtual = true,
            Receiver = new ThisValue { TypeId = TypeId, Text = "this" },
            TypeArgs = new[] { TypeRef.Named("System.String") },
            Args = new[]
            {
                new Arg { ParamIndex = 0, ParamName = "eventPath", Value = ConstValue.Of("Common.Display") }
            },
            IsAwaited = true,
            IsConditionalAccess = false,
            Conditions = new[] { SampleCondition },
            Reachable = true,
            Order = 3,
            Span = SampleSpan
        },
        [IrRecordKinds.Assign] = new AssignRec
        {
            Id = AssignId,
            MethodId = MethodId,
            Target = new MemberAccessValue
            {
                Receiver = new ThisValue { TypeId = TypeId, Text = "this" },
                SymbolId = FieldId,
                Text = "btnOk.clicked"
            },
            Operator = AssignRec.Operators.AddAssign,
            Value = new MethodRefValue { MethodId = MethodId, Text = "OnClickBtnOk" },
            Conditions = Array.Empty<Condition>(),
            Reachable = true,
            Order = 4,
            Span = SampleSpan
        },
        [IrRecordKinds.Creation] = new CreationRec
        {
            Id = CreationId,
            MethodId = MethodId,
            Value = SampleObjectCreation,
            Conditions = new[] { SampleCondition },
            Reachable = false,
            Order = 5,
            Span = SampleSpan
        },
        [IrRecordKinds.Cfg] = new CfgRec
        {
            MethodId = MethodId,
            BlockCount = 12,
            HasGoto = true,
            UnreachableSpans = new[] { SampleSpan }
        },
        [IrRecordKinds.Marker] = new MarkerRec
        {
            File = File,
            Line = 7,
            Dir = MarkerDirections.Begin,
            Kind = "ADD",
            Date = "2024/01/09",
            Author = "someone",
            IssueRefs = new[] { "SEJ-1234" },
            Text = "対応追加"
        },
        [IrRecordKinds.Diag] = DiagRec.Warning("W_SAMPLE", "sample diagnostic", File, 7)
    };
}
