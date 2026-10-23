using ScreenFlow.Core.Ir;
using ScreenFlow.CSharp.Tests.Walkers;
using Xunit;

namespace ScreenFlow.CSharp.Tests.Values;

/// <summary>
/// Constant folding and value provenance (02 §5.4). Covers E-07.
/// <para>
/// These are the facts every screen hop is built on: an event path, a screen id or a key sequence
/// entry is a string or a delegate assembled in source, and the path search can only follow what is
/// folded here.
/// </para>
/// </summary>
public sealed class ValueBuilderTests
{
    private const string Ssaw0281Presenter =
        "Assets.ScenePosRegister.Reception.EdyCharge.Presenter.Script.EdyCharge_SSAW0281_Presenter";

    private const string PaymentPresenter =
        "Assets.ScenePosRegister.Payment.Presenter.Script.Payment_SSAW0089_Presenter";

    private const string SslSend =
        "Assets.Common.Model.Script.Tcp.TcpModel.SslSend(string, " +
        "Assets.Common.Model.Script.Tcp.SocketRequestData, System.Collections.Generic.List<string>, int)";

    /// <summary>02 §5.4: <c>nameof</c> and a constant concatenation are constants.</summary>
    [Fact]
    [Trait("Task", "T09")]
    public void Value_Const_NameofAndConcat()
    {
        var fixture = ExtractedFixture.Editor;

        // nameof(SSAW0001) inside the ScreenID registry (03 §4).
        var screenId = fixture.Symbol(SymbolKinds.Type, "ScreenID");
        var field = fixture.Symbols.Single(s =>
            s.Kind == SymbolKinds.Field && s.ContainerId == screenId.Id && s.Name == "SSAW0001");

        var creation = Assert.IsType<ObjectCreationValue>(field.Initializer);
        var name = Assert.IsType<ConstValue>(creation.Args.Single(a => a.ParamIndex == 0).Value);
        Assert.Equal("SSAW0001", name.ConstantValue!.GetValue<string>());
        Assert.Equal(new[] { "SSAW0001" }, name.FoldedStrings());

        // A concatenation of a const field and a literal folds to one constant, not to a set.
        var concat = Assert.IsType<ConstValue>(Workbench.FoldSnippet(ConcatSnippet, "Prefix + \".above\""));
        Assert.Equal("Common.DisplayPopup.above", concat.ConstantValue!.GetValue<string>());

        // A static readonly string whose own initialiser folds counts as a constant too (02 §5.4).
        var viaField = Assert.IsType<ConstValue>(Workbench.FoldSnippet(ConcatSnippet, "Topic + \"!\""));
        Assert.Equal("Common.DisplayPopup.above!", viaField.ConstantValue!.GetValue<string>());
    }

    /// <summary>
    /// 02 §5.4: a concatenation with a condition in it folds to the complete set of strings it can
    /// be — both of them, sorted, because the path search has to consider each (03 §13.2(b)).
    /// </summary>
    [Fact]
    [Trait("Task", "T09")]
    public void Value_Strings_ConditionalConcat()
    {
        var fixture = ExtractedFixture.Editor;

        var wrapper = fixture.Symbol(
            SymbolKinds.Method,
            Ssaw0281Presenter + ".CustomerToOtherScreen(ScreenID, string, string)");

        var wrapperCalls = fixture.Callsites.Where(c => c.TargetId == wrapper.Id).ToList();
        var callsite = Assert.Single(wrapperCalls);
        var eventPath = callsite.Args.Single(a => a.ParamIndex == 1).Value;

        var strings = Assert.IsType<StringsValue>(eventPath);
        Assert.Equal(new[] { "ToSSAW0907", "ToSSAW0907.employee" }, strings.Values);
    }

    /// <summary>
    /// 02 §5.4: an interpolated string that does not fold completely becomes a template, with only
    /// the resolvable parts stated. <c>EVRW_CATEGORY.ToLower()</c> folds; the registration number
    /// comes from a call and does not (03 §17).
    /// </summary>
    [Fact]
    [Trait("Task", "T09")]
    public void Value_Template_Interpolation()
    {
        var folded = Workbench.Editor.Fold(
            "EValueRwController.cs",
            "$\"shared-bus-{GetRegNo()}-service-ngp-device-{DeviceBrokerConst.EVRW_CATEGORY.ToLower()}-events\"");

        var template = Assert.IsType<TemplateValue>(folded);
        Assert.Equal("shared-bus-{*}-service-ngp-device-evrw-events", template.Template);
    }

    /// <summary>
    /// 02 §5.4: <c>ScreenID.SSAW0903.Id</c> is the static field plus the path <c>["Id"]</c>, so the
    /// screen it names stays matchable by symbol id rather than by the text around it.
    /// </summary>
    [Fact]
    [Trait("Task", "T09")]
    public void Value_StaticMemberPath_ScreenIdId()
    {
        var fixture = ExtractedFixture.Editor;
        var screenId = fixture.Symbol(SymbolKinds.Type, "ScreenID");
        var ssaw0903 = fixture.Symbols.Single(s =>
            s.Kind == SymbolKinds.Field && s.ContainerId == screenId.Id && s.Name == "SSAW0903");

        var folded = Workbench.Editor.Fold("Payment_SSAW0089_Presenter.cs", "ScreenID.SSAW0903.Id");

        var member = Assert.IsType<StaticMemberValue>(folded);
        Assert.Equal(ssaw0903.Id, member.SymbolId);
        Assert.Equal(new[] { "Id" }, member.Path);

        // Without the trailing property the path is empty.
        var bare = Assert.IsType<StaticMemberValue>(
            Workbench.Editor.Fold("Payment_SSAW0089_Presenter.cs", "ScreenID.SSAW0001"));

        Assert.Empty(bare.Path);
    }

    /// <summary>
    /// 02 §5.4: an object initialiser's members are recorded with the symbol they assign, which is
    /// how the TCP adapter later reads the event and target of a request (03 §10.1).
    /// </summary>
    [Fact]
    [Trait("Task", "T09")]
    public void Value_ObjectCreation_InitializerMembers()
    {
        var fixture = ExtractedFixture.Editor;
        var method = fixture.Symbol(SymbolKinds.Method, PaymentPresenter + ".OnClickBtnEdy()");

        var requests = fixture.Creations.Where(c =>
            c.MethodId == method.Id
            && c.ObjectCreation!.Type?.FqName == "Assets.Common.Model.Script.Tcp.SocketRequestData")
            .ToList();

        var creation = Assert.Single(requests);

        var value = creation.ObjectCreation!;
        Assert.Equal(new[] { "EventID", "TargetID", "Value" }, value.Members.Select(m => m.Name));

        var eventId = Assert.IsType<ConstValue>(value.Members.Single(m => m.Name == "EventID").Value);
        Assert.Equal("Common.DisplayPopup", eventId.ConstantValue!.GetValue<string>());

        var targetId = Assert.IsType<StaticMemberValue>(value.Members.Single(m => m.Name == "TargetID").Value);
        Assert.Equal(new[] { "Id" }, targetId.Path);

        // Every member names the symbol it assigns, so a renamed property cannot be mistaken for it.
        Assert.All(value.Members, m => Assert.NotNull(m.SymbolId));
    }

    /// <summary>
    /// 02 §5.4 and 03 §11.1: a collection initialiser records one element per entry, each being the
    /// arguments handed to <c>Add</c>. The commented-out entry of the fixture is not an entry
    /// (CLAUDE.md §4: a comment is never a parsing fact).
    /// </summary>
    [Fact]
    [Trait("Task", "T09")]
    public void Value_CollectionInitializer_Elements()
    {
        var table = PayCheckTable();

        Assert.Equal(2, table.Elements.Count);
        Assert.All(table.Elements, element => Assert.Single(element));
        Assert.All(table.Elements, element => Assert.IsType<ObjectCreationValue>(element[0]));

        // The neighbouring table has one live entry and one commented out (05 §3.3).
        var selectTable = Table("KtNml_EDY_CHARGE_SELECT");
        Assert.Single(selectTable.Elements);
        Assert.DoesNotContain("GaEntryOld", Flatten(selectTable), StringComparison.Ordinal);

        // An empty table is empty, not absent (03 §11.1).
        Assert.Empty(Table("KtNml_EMPTY").Elements);
    }

    /// <summary>
    /// 02 §5.4: <c>new KeySeqFunction(model.X.M)</c> is a reference to <c>M</c> with the receiver
    /// <c>model.X</c> — the key sequence table is a table of such references (03 §11.1).
    /// </summary>
    [Fact]
    [Trait("Task", "T09")]
    public void Value_MethodRef_KeySeqFunction()
    {
        var fixture = ExtractedFixture.Editor;
        var handler = fixture.Symbol(
            SymbolKinds.Method,
            "Assets.ScenePosRegister.Common.Model.Script.EdyCharge_Model.GaEntryChargeOtherFromPay" +
            "(ref TEC.SEJPOS.Model.Apl.KeySeq.KeySeqRequest)");

        var record = Assert.IsType<ObjectCreationValue>(PayCheckTable().Elements[0][0]);
        var function = Assert.IsType<MethodRefValue>(record.Args.Single(a => a.ParamIndex == 1).Value);

        Assert.Equal(handler.Id, function.MethodId);

        var receiver = Assert.IsType<MemberAccessValue>(function.Receiver);
        Assert.Equal("model.EdyCharge_Model", receiver.Text);
    }

    /// <summary>
    /// 02 §5.4: a target-typed <c>new(...)</c> on a ValueTuple is a tuple, which is the shape the
    /// screen registry of 03 §6 is written in.
    /// </summary>
    [Fact]
    [Trait("Task", "T09")]
    public void Value_Tuple_RegistryEntry()
    {
        var fixture = ExtractedFixture.Editor;

        var view = fixture.Symbol(SymbolKinds.Type, "Assets.SceneCustomer.Common.View.Script.Scene_Customer_View");
        var registry = fixture.Symbols.Single(s =>
            s.Kind == SymbolKinds.Field && s.ContainerId == view.Id && s.Name == "InitDictionary");

        var dictionary = Assert.IsType<ObjectCreationValue>(registry.Initializer);
        Assert.Equal(2, dictionary.Elements.Count);

        // A dictionary entry is Add(key, value): two values, not one pair object.
        var entry = dictionary.Elements[0];
        Assert.Equal(2, entry.Count);

        var key = Assert.IsType<StaticMemberValue>(entry[0]);
        Assert.Equal("ScreenID.SSAW0903", key.Text);

        var tuple = Assert.IsType<TupleValue>(entry[1]);
        Assert.Equal(2, tuple.Items.Count);

        var screenType = Assert.IsType<TypeOfValue>(tuple.Items[0]);
        var presenterType = Assert.IsType<TypeOfValue>(tuple.Items[1]);

        Assert.Equal(
            fixture.Symbol(
                SymbolKinds.Type,
                "Assets.SceneCustomer.Reception.EdyCharge.View.Script.EdyCharge_SSAW0903_View").Id,
            screenType.TypeId);

        Assert.Equal(
            fixture.Symbol(
                SymbolKinds.Type,
                "Assets.SceneCustomer.Reception.EdyCharge.Presenter.Script.EdyCharge_SSAW0903_Presenter").Id,
            presenterType.TypeId);
    }

    /// <summary>
    /// 02 §5.4: a local assigned once carries where its value came from; that is how an event path
    /// read out of a request dictionary stays traceable (03 §10.5).
    /// </summary>
    [Fact]
    [Trait("Task", "T09")]
    public void Value_Local_SingleAssignmentOrigin()
    {
        var fixture = ExtractedFixture.Editor;

        var receive = fixture.Symbol(
            SymbolKinds.Method,
            PaymentPresenter + ".OnReceiveSocket(Assets.Common.Script.Base.EventRequest)");

        // eventPath.Equals("PosRegister.SSAW0281.Other") — the receiver is the local.
        var equals = fixture.Callsites
            .Where(c => c.CallerId == receive.Id && c.Receiver is LocalValue)
            .OrderBy(c => c.Order)
            .ToList();

        Assert.Equal(2, equals.Count);

        var local = Assert.IsType<LocalValue>(equals[0].Receiver);
        Assert.Equal("eventPath", local.Name);
        Assert.False(local.IsLoopIndex);

        var origin = Assert.IsType<ElementAccessValue>(local.Origin);
        Assert.Equal("request.Request", origin.Receiver.Text);
        Assert.Equal("EventPath", Assert.IsType<ConstValue>(origin.Index).ConstantValue!.GetValue<string>());

        // A for-loop variable is marked as one, with the collection that bounds it (02 §5.4).
        var setView = fixture.Symbol(
            SymbolKinds.Method,
            "Assets.SceneCustomer.Reception.EdyCharge.View.Script.EdyCharge_SSAW0903_View.SetView" +
            "(Assets.Common.View.Dto.EdyCharge_SSAW0903_Element_DTO)");

        var indexed = fixture.Callsites
            .Where(c => c.CallerId == setView.Id)
            .SelectMany(Values)
            .OfType<LocalValue>()
            .Where(v => v.IsLoopIndex)
            .ToList();

        Assert.NotEmpty(indexed);
        Assert.All(indexed, v => Assert.Equal("i", v.Name));
        Assert.Contains(indexed, v => v.LoopBound is not null && v.LoopBound.Text == "listButtons");
    }

    /// <summary>
    /// E-07: the one CP932 file in the fixture (05 §3.4). A wrong encoding would not fail the build —
    /// it would silently replace every Japanese literal, so the constant itself is the check.
    /// </summary>
    [Fact]
    [Trait("Task", "T09")]
    public void Encoding_Cp932_DecodesLiteral()
    {
        var fixture = ExtractedFixture.Editor;

        var calculator = fixture.Symbol(SymbolKinds.Type, "Assets.Common.Legacy.KeyCalculator");
        var label = fixture.Symbols.Single(s =>
            s.Kind == SymbolKinds.Field && s.ContainerId == calculator.Id && s.Name == "Label");

        Assert.Equal("合計金額", label.ConstantValue!.GetValue<string>());

        var folded = Assert.IsType<ConstValue>(label.Initializer);
        Assert.Equal("合計金額", folded.ConstantValue!.GetValue<string>());
        Assert.DoesNotContain('�', folded.Text);

        var unit = fixture.Symbols.Single(s =>
            s.Kind == SymbolKinds.Field && s.ContainerId == calculator.Id && s.Name == "Unit");

        Assert.Equal("円", unit.ConstantValue!.GetValue<string>());
    }

    /// <summary>
    /// 02 §5.4: beyond <see cref="StringsValue.MaxValues"/> alternatives the value becomes a
    /// template. A truncated set would read as a complete answer and hide real screen edges.
    /// </summary>
    [Fact]
    [Trait("Task", "T09")]
    public void Value_Strings_BeyondTheLimitBecomesTemplate()
    {
        var withinLimit = Workbench.FoldSnippet(ProductSnippet, "A + B");
        Assert.Equal(4, Assert.IsType<StringsValue>(withinLimit).Values.Count);

        // 32 alternatives is past the limit, so each unresolved operand is one placeholder.
        var beyondLimit = Workbench.FoldSnippet(ProductSnippet, "A + B + C + D + E");
        Assert.Equal("{*}{*}{*}{*}{*}", Assert.IsType<TemplateValue>(beyondLimit).Template);
    }

    private const string ConcatSnippet = """
        public static class Snippet
        {
            private const string Prefix = "Common.DisplayPopup";

            private static readonly string Topic = Prefix + ".above";

            public static string Build(bool flag) => Prefix + ".above";

            public static string BuildFromField() => Topic + "!";
        }
        """;

    /// <summary>Four conditions make 32 alternatives, which is past the limit.</summary>
    private const string ProductSnippet = """
        public static class Snippet
        {
            public static string Build(bool flag)
            {
                string A = flag ? "a" : "b";
                string B = flag ? "c" : "d";
                string C = flag ? "e" : "f";
                string D = flag ? "g" : "h";
                string E = flag ? "i" : "j";
                return A + B;
            }

            public static string BuildAll(bool flag)
            {
                string A = flag ? "a" : "b";
                string B = flag ? "c" : "d";
                string C = flag ? "e" : "f";
                string D = flag ? "g" : "h";
                string E = flag ? "i" : "j";
                return A + B + C + D + E;
            }
        }
        """;

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// The <c>KtNml_EDY_PAY_CHECK</c> table as the registration reads it: the second argument of the
    /// <c>Add</c> call, which is the local whose origin is the initialised list (03 §11.1).
    /// </summary>
    private static ObjectCreationValue PayCheckTable() => Table("KtNml_EDY_PAY_CHECK");

    private static ObjectCreationValue Table(string localName)
    {
        var fixture = ExtractedFixture.Editor;

        var init = fixture.Symbol(
            SymbolKinds.Method,
            "Assets.ScenePosRegister.Common.Model.Script.NmlKty.InitEdy" +
            "(Assets.ScenePosRegister.Common.Model.Script.Scene_PosRegister_Model)");

        var locals = fixture.Callsites
            .Where(c => c.CallerId == init.Id)
            .SelectMany(c => c.Args)
            .Select(a => a.Value)
            .OfType<LocalValue>()
            .Where(v => v.Name == localName)
            .ToList();

        var table = Assert.Single(locals);
        return Assert.IsType<ObjectCreationValue>(table.Origin);
    }

    private static string Flatten(Value value)
        => value.Text + string.Concat(value.Children().Select(Flatten));

    private static IEnumerable<Value> Values(CallsiteRec callsite)
    {
        var roots = callsite.Args.Select(a => a.Value).ToList();
        if (callsite.Receiver is not null) roots.Add(callsite.Receiver);

        return roots.SelectMany(Descend);
    }

    private static IEnumerable<Value> Descend(Value value)
        => new[] { value }.Concat(value.Children().SelectMany(Descend));
}
