using ScreenFlow.Core.Ir;
using Xunit;

namespace ScreenFlow.CSharp.Tests.Walkers;

/// <summary>
/// Method body extraction (02 §5.5, §6.6). Covers the first half of E-17.
/// </summary>
public sealed class BodyWalkerTests
{
    private const string CustomerPresenter =
        "Assets.SceneCustomer.Reception.EdyCharge.Presenter.Script.EdyCharge_SSAW0903_Presenter";

    private const string PaymentPresenter =
        "Assets.ScenePosRegister.Payment.Presenter.Script.Payment_SSAW0089_Presenter";

    private const string Ssaw0903View =
        "Assets.SceneCustomer.Reception.EdyCharge.View.Script.EdyCharge_SSAW0903_View";

    private const string SslSend =
        "Assets.Common.Model.Script.Tcp.TcpModel.SslSend(string, " +
        "Assets.Common.Model.Script.Tcp.SocketRequestData, System.Collections.Generic.List<string>, int)";

    /// <summary>
    /// 02 §6.6: a call inside a lambda belongs to the lambda. The queued send of 03 §13.2[1] runs
    /// when the queue runs it, so attributing it to <c>OnClickBtnOther</c> would claim a hop the
    /// click itself does not make.
    /// </summary>
    [Fact]
    [Trait("Task", "T09")]
    public void Body_LambdaCallsBelongToLambda()
    {
        var fixture = ExtractedFixture.Editor;

        var click = fixture.Symbol(SymbolKinds.Method, CustomerPresenter + ".OnClickBtnOther()");
        var lambdas = fixture.Symbols.Where(s => s.IsLambda && s.ContainerId == click.Id).ToList();
        var lambda = Assert.Single(lambdas);
        var send = fixture.Symbol(SymbolKinds.Method, SslSend);

        var sends = fixture.Callsites.Where(c => c.TargetId == send.Id).ToList();

        // The send is the lambda's, not the click handler's.
        Assert.Contains(sends, c => c.CallerId == lambda.Id);
        Assert.DoesNotContain(sends, c => c.CallerId == click.Id);

        var oneArgSends = sends.Where(c => c.CallerId == lambda.Id && c.Args.Count == 1).ToList();
        var queued = Assert.Single(oneArgSends);

        var path = Assert.IsType<ConstValue>(queued.Args[0].Value);
        Assert.Equal("PosRegister.SSAW0281.Other", path.ConstantValue!.GetValue<string>());

        // Optional parameters that were not written do not appear at all (02 §5.4).
        Assert.Equal(0, queued.Args[0].ParamIndex);
        Assert.True(queued.IsAwaited);

        // The click handler still owns the call that creates the queue entry, and that entry's
        // first argument is the lambda itself (03 §19).
        var addQueue = fixture.Callsites
            .Where(c => c.CallerId == click.Id)
            .SelectMany(c => c.Args)
            .Select(a => a.Value)
            .ToList();

        Assert.Contains(addQueue.OfType<ObjectCreationValue>()
            .SelectMany(v => v.Args)
            .Select(a => a.Value)
            .OfType<LambdaValue>(), v => v.MethodId == lambda.Id);
    }

    /// <summary>
    /// 02 §5.5: an event subscription is an assignment whose target names the event symbol. This is
    /// the link from a control to its handler (03 §9), so it has to be a fact and not a call.
    /// </summary>
    [Fact]
    [Trait("Task", "T09")]
    public void Body_EventSubscription_IsAssign()
    {
        var fixture = ExtractedFixture.Editor;

        var setEvent = fixture.Symbol(SymbolKinds.Method, Ssaw0903View + ".SetEvent()");
        var resetEvent = fixture.Symbol(SymbolKinds.Method, Ssaw0903View + ".ResetEvent()");

        var subscriptions = fixture.Assigns
            .Where(a => a.MethodId == setEvent.Id && a.Operator == AssignRec.Operators.AddAssign)
            .ToList();

        Assert.Equal(4, subscriptions.Count);

        var otherSubscriptions = subscriptions
            .Where(a => a.Target.Text == "this.btnOther.clicked")
            .ToList();

        var other = Assert.Single(otherSubscriptions);
        var target = Assert.IsType<MemberAccessValue>(other.Target);
        Assert.NotNull(target.SymbolId);

        var clicked = fixture.Symbols.Single(s => s.Id == target.SymbolId);
        Assert.Equal(SymbolKinds.Event, clicked.Kind);
        Assert.Equal("clicked", clicked.Name);

        // The handler is the delegate field the view assigned earlier in the same method.
        var handler = Assert.IsType<MemberAccessValue>(other.Value);
        Assert.Equal("clickBtnOtherFunc", handler.Text);

        // Unsubscription is the same fact with the opposite operator.
        var removals = fixture.Assigns
            .Where(a => a.MethodId == resetEvent.Id && a.Operator == AssignRec.Operators.SubtractAssign)
            .ToList();

        Assert.Equal(4, removals.Count);

        // The four handler fields are assigned with plain "=" in SetEvent.
        var handlerAssignments = fixture.Assigns
            .Where(a => a.MethodId == setEvent.Id && a.Operator == AssignRec.Operators.Assign)
            .ToList();

        Assert.Equal(4, handlerAssignments.Count);
        Assert.All(handlerAssignments, a => Assert.IsType<LambdaValue>(a.Value));
    }

    /// <summary>
    /// E-17 (first half): the commented-out <c>SocketRequestData</c> of 05 §3.3 must produce no
    /// creation. A comment is never a parsing fact (CLAUDE.md §4), and this one would otherwise add
    /// a transition to SSAW0907 that the code does not make.
    /// </summary>
    [Fact]
    [Trait("Task", "T09")]
    public void Body_CommentedCode_NoCreation()
    {
        var fixture = ExtractedFixture.Editor;
        var method = fixture.Symbol(SymbolKinds.Method, PaymentPresenter + ".OnClickBtnEdy()");

        var creations = fixture.Creations.Where(c => c.MethodId == method.Id).ToList();

        // Exactly one request is created here; the commented one is not a second.
        var requests = creations
            .Where(c => c.ObjectCreation!.Type?.FqName == "Assets.Common.Model.Script.Tcp.SocketRequestData")
            .ToList();

        var request = Assert.Single(requests);

        Assert.Equal("SSAW0903", ScreenFieldName(request.ObjectCreation!));
        Assert.All(creations, c => Assert.DoesNotContain("SSAW0907", Flatten(c.Value), StringComparison.Ordinal));

        // Nothing in this method's calls mentions the commented screen either.
        var calls = fixture.Callsites.Where(c => c.CallerId == method.Id).ToList();
        Assert.All(
            calls.SelectMany(c => c.Args).Select(a => a.Value),
            value => Assert.DoesNotContain("SSAW0907", Flatten(value), StringComparison.Ordinal));
    }

    /// <summary>
    /// 02 §5.5: <c>order</c> is the source position within one owner, and every site id is unique,
    /// because an id collision would make two different facts indistinguishable (02 §5.6).
    /// </summary>
    [Fact]
    [Trait("Task", "T09")]
    public void Body_SitesAreOrderedAndUniquelyIdentified()
    {
        var fixture = ExtractedFixture.Editor;

        var ids = fixture.Callsites.Select(c => c.Id)
            .Concat(fixture.Assigns.Select(a => a.Id))
            .Concat(fixture.Creations.Select(c => c.Id))
            .ToList();

        Assert.NotEmpty(ids);
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(fixture.Callsites, c => Assert.True(Ids.HasPrefix(c.Id, Ids.CallsitePrefix)));
        Assert.All(fixture.Assigns, a => Assert.True(Ids.HasPrefix(a.Id, Ids.AssignPrefix)));
        Assert.All(fixture.Creations, c => Assert.True(Ids.HasPrefix(c.Id, Ids.CreationPrefix)));

        // Within one owner the orders are 0..n-1 over all three record kinds together.
        var owners = fixture.Callsites.Select(c => (c.CallerId, c.Order, (int?)c.Span.StartLine))
            .Concat(fixture.Assigns.Select(a => (a.MethodId, a.Order, (int?)a.Span.StartLine)))
            .Concat(fixture.Creations.Select(c => (c.MethodId, c.Order, (int?)c.Span.StartLine)))
            .GroupBy(s => s.Item1, StringComparer.Ordinal);

        foreach (var owner in owners)
        {
            var orders = owner.Select(s => s.Order).OrderBy(o => o).ToList();
            Assert.Equal(Enumerable.Range(0, orders.Count), orders);
        }

        // A call target always resolves to a symbol the index contains.
        var known = fixture.Symbols.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        Assert.All(fixture.Callsites, c => Assert.Contains(c.TargetId!, known));
    }

    /// <summary>
    /// 02 §5.5: an interface call is marked virtual, because what runs is decided at run time and
    /// the next hop has to come from the implementation map rather than from the call.
    /// </summary>
    [Fact]
    [Trait("Task", "T09")]
    public void Body_InterfaceCallsAreVirtual()
    {
        var fixture = ExtractedFixture.Editor;

        var register = fixture.Symbol(
            SymbolKinds.Method,
            "TEC.SEJPOS.Model.Apl.KeySeq.IKeySeqRegister.Add(TEC.SEJPOS.Model.Apl.KeySeq.StageNo, " +
            "System.Collections.Generic.List<TEC.SEJPOS.Model.Apl.KeySeq.KeySeqTableRecord>)");

        var calls = fixture.Callsites.Where(c => c.TargetId == register.Id).ToList();

        Assert.Equal(3, calls.Count);
        Assert.All(calls, c => Assert.True(c.TargetIsVirtual));

        // A conditional delegate invocation is both conditional and virtual (03 §17).
        var invokes = fixture.Callsites.Where(c => c.IsConditionalAccess).ToList();
        Assert.NotEmpty(invokes);
        Assert.Contains(invokes, c => c.TargetIsVirtual);
    }

    private static string ScreenFieldName(ObjectCreationValue creation)
    {
        var target = creation.Members.Single(m => m.Name == "TargetID").Value;
        var member = Assert.IsType<StaticMemberValue>(target);
        return member.Text.Split('.')[1];
    }

    private static string Flatten(Value value)
        => value.Text + string.Concat(value.Children().Select(Flatten));
}
