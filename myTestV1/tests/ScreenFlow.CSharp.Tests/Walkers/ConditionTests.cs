using ScreenFlow.Core.Ir;
using ScreenFlow.CSharp.Walkers;
using Xunit;

namespace ScreenFlow.CSharp.Tests.Walkers;

/// <summary>
/// Guard collection (02 §5.7).
/// <para>
/// A receiver dispatches on the event path with a chain of <c>if</c>/<c>else if</c> (03 §10.5), so
/// the condition attached to a call is what says which event path leads to which screen. Getting the
/// polarity wrong would invert a whole screen graph.
/// </para>
/// </summary>
public sealed class ConditionTests
{
    private const string PaymentPresenter =
        "Assets.ScenePosRegister.Payment.Presenter.Script.Payment_SSAW0089_Presenter";

    private const string Customer0907Presenter =
        "Assets.SceneCustomer.Reception.EdyCharge.Presenter.Script.EdyCharge_SSAW0907_Presenter";

    private const string Ssaw0907View =
        "Assets.SceneCustomer.Reception.EdyCharge.View.Script.EdyCharge_SSAW0907_View";

    /// <summary>
    /// 02 §5.7: an <c>else if</c> runs only when every earlier test failed, so the earlier ones are
    /// recorded with polarity false, outermost first.
    /// </summary>
    [Fact]
    [Trait("Task", "T10")]
    public void Condition_ElseIfChain_Polarity()
    {
        var fixture = ExtractedFixture.Editor;

        var receive = fixture.Symbol(
            SymbolKinds.Method,
            PaymentPresenter + ".OnReceiveSocket(Assets.Common.Script.Base.EventRequest)");

        var amountOk = fixture.Symbol(SymbolKinds.Method, PaymentPresenter + ".OnClickBtnAmountOK()");

        var calls = fixture.Callsites
            .Where(c => c.CallerId == receive.Id && c.TargetId == amountOk.Id)
            .ToList();

        var call = Assert.Single(calls);
        Assert.Equal(2, call.Conditions.Count);

        var first = call.Conditions[0];
        Assert.Equal(ConditionKinds.If, first.Kind);
        Assert.False(first.Polarity);
        Assert.Equal(ConditionModes.All, first.Mode);
        var firstTest = Assert.Single(first.Tests);
        Assert.Equal(StringTestOps.EqualsOp, firstTest.Op);
        Assert.Equal("PosRegister.SSAW0281.Other", firstTest.Literal);

        var second = call.Conditions[1];
        Assert.Equal(ConditionKinds.If, second.Kind);
        Assert.True(second.Polarity);
        var secondTest = Assert.Single(second.Tests);
        Assert.Equal(StringTestOps.EqualsOp, secondTest.Op);
        Assert.Equal("PosRegister.SSAW1113.OK", secondTest.Literal);

        // The first branch's own call carries only the positive test.
        var other = fixture.Symbol(SymbolKinds.Method, PaymentPresenter + ".OnClickBtnOther()");
        var otherCalls = fixture.Callsites
            .Where(c => c.CallerId == receive.Id && c.TargetId == other.Id)
            .ToList();

        var otherCall = Assert.Single(otherCalls);
        var onlyCondition = Assert.Single(otherCall.Conditions);
        Assert.True(onlyCondition.Polarity);
        Assert.Equal("PosRegister.SSAW0281.Other", Assert.Single(onlyCondition.Tests).Literal);

        Assert.All(call.Conditions, c => Assert.False(c.Approximate));
    }

    /// <summary>
    /// 02 §5.7 and 03 §10.5: a receiver matches with <c>Contains</c> as well as <c>Equals</c>, and
    /// the two mean different sets of event paths — <c>Contains("ToSSAW0907")</c> also accepts
    /// <c>ToSSAW0907.employee</c>.
    /// </summary>
    [Fact]
    [Trait("Task", "T10")]
    public void Condition_Contains_And_Equals()
    {
        var fixture = ExtractedFixture.Editor;

        var receive = fixture.Symbol(
            SymbolKinds.Method,
            Customer0907Presenter + ".OnReceiveSocket(Assets.Common.Script.Base.EventRequest)");

        var setErrors = fixture.Symbol(SymbolKinds.Method, Customer0907Presenter + ".SetErrorMessages()");

        var calls = fixture.Callsites
            .Where(c => c.CallerId == receive.Id && c.TargetId == setErrors.Id)
            .OrderBy(c => c.Order)
            .ToList();

        Assert.Equal(3, calls.Count);

        // Innermost first call: Contains("ToSSAW0907") then the nested employee test.
        var nested = calls[0];
        Assert.Equal(2, nested.Conditions.Count);

        var contains = Assert.Single(nested.Conditions[0].Tests);
        Assert.Equal(StringTestOps.Contains, contains.Op);
        Assert.Equal("ToSSAW0907", contains.Literal);
        Assert.True(nested.Conditions[0].Polarity);

        // tokens.Length > 1 contributes no string test; the employee check does.
        var employee = Assert.Single(nested.Conditions[1].Tests);
        Assert.Equal(StringTestOps.Contains, employee.Op);
        Assert.Equal("employee", employee.Literal);
        Assert.Equal(ConditionModes.All, nested.Conditions[1].Mode);

        // The else branch is the Equals one, with the Contains test negated before it.
        var elseCall = calls[2];
        Assert.Equal(2, elseCall.Conditions.Count);
        Assert.False(elseCall.Conditions[0].Polarity);
        Assert.Equal("ToSSAW0907", Assert.Single(elseCall.Conditions[0].Tests).Literal);

        Assert.True(elseCall.Conditions[1].Polarity);
        var equals = Assert.Single(elseCall.Conditions[1].Tests);
        Assert.Equal(StringTestOps.EqualsOp, equals.Op);
        Assert.Equal("SSAW0903.To.SSAW0907", equals.Literal);
    }

    /// <summary>
    /// 02 §5.7 and 03 §10.5: the subject of a test is usually a local read out of the request
    /// dictionary, and the test is only usable if that origin is recorded with it.
    /// </summary>
    [Fact]
    [Trait("Task", "T10")]
    public void Condition_SubjectIsEventPathIndexer()
    {
        var fixture = ExtractedFixture.Editor;

        var receive = fixture.Symbol(
            SymbolKinds.Method,
            PaymentPresenter + ".OnReceiveSocket(Assets.Common.Script.Base.EventRequest)");

        var tests = fixture.Callsites
            .Where(c => c.CallerId == receive.Id)
            .SelectMany(c => c.Conditions)
            .SelectMany(c => c.Tests)
            .ToList();

        Assert.NotEmpty(tests);

        foreach (var test in tests)
        {
            var local = Assert.IsType<LocalValue>(test.Subject);
            Assert.Equal("eventPath", local.Name);

            var origin = Assert.IsType<ElementAccessValue>(local.Origin);
            Assert.Equal("request.Request", origin.Receiver.Text);
            Assert.Equal("EventPath", Assert.IsType<ConstValue>(origin.Index).ConstantValue!.GetValue<string>());
        }
    }

    /// <summary>
    /// 02 §5.7: in a method with <c>goto</c> the ancestor chain is not the whole story, so every
    /// condition in it is marked approximate rather than presented as complete.
    /// </summary>
    [Fact]
    [Trait("Task", "T10")]
    public void Condition_GotoMethod_Approximate()
    {
        var fixture = ExtractedFixture.Editor;

        var legacy = fixture.Symbol(
            SymbolKinds.Method,
            "Assets.ScenePosRegister.Reception.EdyCharge.Presenter.Script.EdyCharge_SSAW0281_Presenter.Legacy()");

        var cfgs = fixture.Cfgs.Where(c => c.MethodId == legacy.Id).ToList();
        var cfg = Assert.Single(cfgs);
        Assert.True(cfg.HasGoto);

        // Every other method of the fixture is free of goto, so the flag means something.
        Assert.Equal(1, fixture.Cfgs.Count(c => c.HasGoto));

        // A call guarded by an if inside a goto method: the guard is recorded and flagged.
        var (bodies, symbols) = Workbench.SnippetBodies(GotoSnippet);
        var target = symbols.Symbols.Single(s => s.FqName == "Snippet.Target()");

        var calls = bodies.Callsites.Where(c => c.TargetId == target.Id).ToList();
        var call = Assert.Single(calls);

        var condition = Assert.Single(call.Conditions);
        Assert.True(condition.Approximate);
        Assert.Equal(ConditionKinds.If, condition.Kind);
        Assert.Equal("PosRegister.SSAW0281.Other", Assert.Single(condition.Tests).Literal);
    }

    /// <summary>
    /// 02 §5.7: the guards inside a lambda are the lambda's own. The OK handler of SSAW0907 only
    /// sets the minimum when the input is non-empty and zero (03 §13.3).
    /// </summary>
    [Fact]
    [Trait("Task", "T10")]
    public void Condition_OkButtonLambda()
    {
        var fixture = ExtractedFixture.Editor;

        var setEvent = fixture.Symbol(SymbolKinds.Method, Ssaw0907View + ".SetEvent()");
        var lambdas = fixture.Symbols
            .Where(s => s.IsLambda && s.ContainerId == setEvent.Id)
            .OrderBy(s => s.Ordinal)
            .ToList();

        Assert.Equal(2, lambdas.Count);

        var setMinValue = fixture.Symbol(
            SymbolKinds.Method,
            "Assets.Common.View.Parts.TenKeyControl.SetInputMinValue(int, string)");

        var calls = fixture.Callsites
            .Where(c => c.CallerId == lambdas[0].Id && c.TargetId == setMinValue.Id)
            .ToList();

        var call = Assert.Single(calls);
        Assert.Equal(2, call.Conditions.Count);

        // string.IsNullOrWhiteSpace(inputValue) is the else side.
        Assert.False(call.Conditions[0].Polarity);
        Assert.Contains("IsNullOrWhiteSpace", call.Conditions[0].Text, StringComparison.Ordinal);
        Assert.Empty(call.Conditions[0].Tests);

        // Convert.ToInt32(inputValue) == 0 is the taken side, and is not a string test.
        Assert.True(call.Conditions[1].Polarity);
        Assert.Contains("Convert.ToInt32(inputValue) == 0", call.Conditions[1].Text, StringComparison.Ordinal);
        Assert.Empty(call.Conditions[1].Tests);
    }

    /// <summary>
    /// 02 §5.7: the remaining test shapes the handbook lists (03 §10.5 notes that any of them may
    /// occur), including a switch, whose default is the negation of every other label.
    /// </summary>
    [Fact]
    [Trait("Task", "T10")]
    public void Condition_AllDocumentedTestShapes()
    {
        var (bodies, symbols) = Workbench.SnippetBodies(ShapesSnippet);

        var tests = Tests(bodies, symbols, "Snippet.Equality(string)");
        Assert.Equal(
            new[]
            {
                (StringTestOps.EqualsOp, "a"),
                (StringTestOps.EqualsOp, "b"),
                (StringTestOps.EqualsOp, "c"),
                (StringTestOps.NotEquals, "d"),
                (StringTestOps.NotEquals, "e")
            },
            tests);

        Assert.Equal(
            new[]
            {
                (StringTestOps.Contains, "f"),
                (StringTestOps.StartsWith, "g"),
                (StringTestOps.EndsWith, "h")
            },
            Tests(bodies, symbols, "Snippet.Fragments(string)"));

        // A negated compound keeps its text and contributes no tests.
        Assert.Empty(Tests(bodies, symbols, "Snippet.NegatedCompound(string)"));

        // Or means any of the tests is enough.
        var either = Conditions(bodies, symbols, "Snippet.Either(string)");
        Assert.Equal(ConditionModes.Any, Assert.Single(either).Mode);

        var switchCase = Conditions(bodies, symbols, "Snippet.SwitchCase(string)");
        var caseCondition = Assert.Single(switchCase);
        Assert.Equal(ConditionKinds.SwitchCase, caseCondition.Kind);
        Assert.Equal(StringTestOps.SwitchLabel, Assert.Single(caseCondition.Tests).Op);
        Assert.Equal("x", Assert.Single(caseCondition.Tests).Literal);

        var switchDefault = Conditions(bodies, symbols, "Snippet.SwitchDefault(string)");
        var defaultCondition = Assert.Single(switchDefault);
        Assert.Equal(ConditionKinds.SwitchDefault, defaultCondition.Kind);
        Assert.Equal(
            new[] { (StringTestOps.NotEquals, "x"), (StringTestOps.NotEquals, "y") },
            defaultCondition.Tests.Select(t => (t.Op, t.Literal)).ToArray());

        // The guarded call itself carries only the if.
        var guarded = Conditions(bodies, symbols, "Snippet.LogicalRight(string)");
        Assert.Equal(ConditionKinds.If, Assert.Single(guarded).Kind);

        // The right operand of && is a call too, and it runs only when the left one allowed it.
        var caller = symbols.Symbols.Single(s => s.FqName == "Snippet.LogicalRight(string)");
        var shortCircuited = bodies.Callsites
            .Where(c => c.CallerId == caller.Id)
            .SelectMany(c => c.Conditions)
            .Where(c => c.Kind == ConditionKinds.LogicalRight)
            .ToList();

        var logicalRight = Assert.Single(shortCircuited);
        Assert.Equal("s.Length > 0", logicalRight.Text);
        Assert.True(logicalRight.Polarity);
    }

    private static (string Op, string Literal)[] Tests(
        BodyResult bodies,
        SymbolCollector symbols,
        string fqName)
        => Conditions(bodies, symbols, fqName)
            .SelectMany(c => c.Tests)
            .Select(t => (t.Op, t.Literal))
            .ToArray();

    /// <summary>
    /// The guards on the <c>Hit()</c> calls of one snippet method. Filtered to that one target
    /// because the tests inside a condition are themselves calls, and those calls carry guards of
    /// their own — the right operand of an <c>&amp;&amp;</c> is guarded by its left operand.
    /// </summary>
    private static IReadOnlyList<Condition> Conditions(
        BodyResult bodies,
        SymbolCollector symbols,
        string fqName)
    {
        var caller = symbols.Symbols.Single(s => s.FqName == fqName);
        var hit = symbols.Symbols.Single(s => s.FqName == "Snippet.Hit()");

        return bodies.Callsites
            .Where(c => c.CallerId == caller.Id && c.TargetId == hit.Id)
            .OrderBy(c => c.Order)
            .SelectMany(c => c.Conditions)
            .ToList();
    }

    private const string GotoSnippet = """
        public static class Snippet
        {
            public static void Target() { }

            public static void Run(string eventPath)
            {
                int i = 0;
            start:
                i++;
                if (i < 2) { goto start; }

                if (eventPath.Equals("PosRegister.SSAW0281.Other"))
                {
                    Target();
                }
            }
        }
        """;

    private const string ShapesSnippet = """
        public static class Snippet
        {
            public static void Hit() { }

            public static void Equality(string s = "")
            {
                if (s.Equals("a")) Hit();
                if (s == "b") Hit();
                if ("c".Equals(s)) Hit();
                if (s != "d") Hit();
                if (!s.Equals("e")) Hit();
            }

            public static void Fragments(string s = "")
            {
                if (s.Contains("f")) Hit();
                if (s.StartsWith("g")) Hit();
                if (s.EndsWith("h")) Hit();
            }

            public static void NegatedCompound(string s = "")
            {
                if (!(s.Equals("i") && s.Equals("j"))) Hit();
            }

            public static void Either(string s = "")
            {
                if (s.Equals("k") || s.Equals("l")) Hit();
            }

            public static void SwitchCase(string s = "")
            {
                switch (s)
                {
                    case "x":
                        Hit();
                        break;
                    case "y":
                        break;
                }
            }

            public static void SwitchDefault(string s = "")
            {
                switch (s)
                {
                    case "x":
                        break;
                    case "y":
                        break;
                    default:
                        Hit();
                        break;
                }
            }

            public static void LogicalRight(string s = "")
            {
                if (s.Length > 0 && s.Equals("m")) Hit();
            }
        }
        """;
}
