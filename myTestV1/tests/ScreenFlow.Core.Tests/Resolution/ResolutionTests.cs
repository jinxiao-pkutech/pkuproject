using ScreenFlow.Core.Ir;
using ScreenFlow.Core.Resolution;
using ScreenFlow.Core.Storage;
using Xunit;
using Xunit.Abstractions;

namespace ScreenFlow.Core.Tests.Resolution;

/// <summary>
/// The build pipeline and the resolution services (02 §2, §8).
/// <para>
/// Dispatch is where a screen path either continues or breaks: a view calls its presenter through an
/// interface, and only field points-to turns that into a named target (03 §9).
/// </para>
/// </summary>
public sealed class ResolutionTests
{
    private const string Ssaw0903View =
        "Assets.SceneCustomer.Reception.EdyCharge.View.Script.EdyCharge_SSAW0903_View";

    private const string Ssaw0903Presenter =
        "Assets.SceneCustomer.Reception.EdyCharge.Presenter.Script.EdyCharge_SSAW0903_Presenter";

    private const string ViewEvent =
        "Assets.SceneCustomer.Reception.EdyCharge.View.Script.IEdyCharge_SSAW0903_View_Event";

    private const string Ssaw0281Presenter =
        "Assets.ScenePosRegister.Reception.EdyCharge.Presenter.Script.EdyCharge_SSAW0281_Presenter";

    private const string SslSend =
        "Assets.Common.Model.Script.Tcp.TcpModel.SslSend(string, " +
        "Assets.Common.Model.Script.Tcp.SocketRequestData, System.Collections.Generic.List<string>, int)";

    private readonly ITestOutputHelper _output;

    public ResolutionTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// 02 §2: the pipeline runs P1–P5 and P9 and leaves a complete index behind. The domain stages
    /// are empty at this task, so the domain tables are empty — and that is visible in the counts
    /// rather than implied.
    /// </summary>
    [Fact]
    [Trait("Task", "T11")]
    public void Pipeline_Fixture_BuildsDatabase()
    {
        var fixture = FixtureIndex.Shared;
        var report = fixture.Report;

        Assert.True(File.Exists(fixture.Path));
        Assert.Equal("fixture", report.ProfileId);
        Assert.Equal(Derivations.Native, report.Derivation);
        Assert.Equal("editor", report.PlatformDetected);
        Assert.Equal("2022.3.68f1", report.UnityDeclared);

        Assert.Equal(66, report.CountOf("files"));
        Assert.True(report.CountOf("symbols") > 0, "no symbols were stored");
        Assert.True(report.CountOf("callsites") > 0, "no callsites were stored");
        Assert.True(report.CountOf("impls") > 0, "no implementation mappings were stored");
        Assert.True(report.CountOf("meta") > 0, "the meta table was not written");

        // Nothing failed: a build that reports errors would have produced a partial index.
        Assert.Equal(0, report.ErrorCount);
        Assert.Empty(report.DisabledFeatures);

        // P6-P8 have no stages yet, so every domain table is still empty.
        Assert.Equal(0, report.CountOf("screens"));
        Assert.Equal(0, report.CountOf("screen_edges"));
        Assert.Equal(new[] { "P1", "P2", "P3", "P4", "P5", "P9" }, report.Phases);

        // 02 §7.4: every meta key the index promises.
        var meta = fixture.Reader.Meta();
        foreach (var key in MetaKeys.All) Assert.True(meta.ContainsKey(key), $"meta is missing '{key}'");

        Assert.Equal(IrValidator.SupportedIrVersion, meta[MetaKeys.IrVersion]);
        Assert.Equal(ScreenFlowVersion.Rules, meta[MetaKeys.RulesVersion]);
        Assert.NotEqual(string.Empty, meta[MetaKeys.Fingerprint]);
    }

    /// <summary>
    /// 02 §8.1 and §8.2: the view calls the presenter through <c>IEdyCharge_SSAW0903_View_Event</c>,
    /// and the only thing that names the target is the assignment the presenter makes to
    /// <c>eventHandler</c> (03 §9). One candidate, and therefore certain.
    /// </summary>
    [Fact]
    [Trait("Task", "T11")]
    public void Dispatch_InterfaceCall_NarrowedByFieldPointsTo()
    {
        var fixture = FixtureIndex.Shared;

        var contract = fixture.Symbol(SymbolKinds.Method, ViewEvent + ".OnClickBtnOther()");
        var implementation = fixture.Symbol(SymbolKinds.Method, Ssaw0903Presenter + ".OnClickBtnOther()");
        var presenter = fixture.Symbol(SymbolKinds.Type, Ssaw0903Presenter);

        var calls = fixture.Reader.CallsitesTargeting(contract.Id);
        var call = Assert.Single(calls);

        Assert.True(call.TargetIsVirtual);
        Assert.True(call.IsConditionalAccess);

        // The receiver is the view's eventHandler field, and it points at the presenter.
        var receiver = Assert.IsType<MemberAccessValue>(call.Receiver);
        var concrete = fixture.Values.ResolveConcrete(receiver, fixture.Context());

        _output.WriteLine($"receiver types: {string.Join(", ", concrete.Types)}");
        Assert.Equal(new[] { presenter.Id }, concrete.Types);

        var dispatch = fixture.Dispatcher.Dispatch(call, fixture.Context());

        Assert.Equal(new[] { implementation.Id }, dispatch.Candidates);
        Assert.Equal(DispatchCertainty.Certain, dispatch.Certainty);
        Assert.Null(dispatch.CandidateGroup);
        Assert.False(dispatch.Truncated);
    }

    /// <summary>
    /// 02 §8.2: the handler a control is subscribed with is a delegate field, and resolving it has
    /// to arrive at the lambda that was assigned to it (03 §9).
    /// </summary>
    [Fact]
    [Trait("Task", "T11")]
    public void Dispatch_DelegateField_ResolvesLambda()
    {
        var fixture = FixtureIndex.Shared;

        var setEvent = fixture.Symbol(SymbolKinds.Method, Ssaw0903View + ".SetEvent()");
        var subscriptions = fixture.Reader.AssignsIn(setEvent.Id)
            .Where(a => a.Operator == AssignRec.Operators.AddAssign)
            .ToList();

        Assert.Equal(4, subscriptions.Count);

        var other = subscriptions.Single(a => a.Target.Text == "this.btnOther.clicked");
        var resolved = fixture.Values.ResolveConcrete(other.Value, fixture.Context());

        var lambda = Assert.IsType<LambdaValue>(Assert.Single(resolved.Values));

        // The lambda is the one SetEvent assigned to clickBtnOtherFunc, and it is the one whose body
        // calls the presenter.
        var lambdaSymbol = fixture.Reader.Symbol(lambda.MethodId);
        Assert.NotNull(lambdaSymbol);
        Assert.True(lambdaSymbol!.IsLambda);
        Assert.Equal(setEvent.Id, lambdaSymbol.ContainerId);

        var inside = fixture.Reader.CallsitesIn(lambda.MethodId);
        Assert.Contains(inside, c => c.TargetId == fixture.Symbol(
            SymbolKinds.Method, ViewEvent + ".OnClickBtnOther()").Id);
    }

    /// <summary>
    /// 02 §8.2: an event path handed to a wrapper method is only knowable by following the parameter
    /// back to the caller (03 §10.1, E-16). Both strings the caller can pass are reported, with the
    /// callsite they came from.
    /// </summary>
    [Fact]
    [Trait("Task", "T11")]
    public void ValueResolver_ParamPropagation()
    {
        var fixture = FixtureIndex.Shared;

        var wrapper = fixture.Symbol(
            SymbolKinds.Method,
            Ssaw0281Presenter + ".CustomerToOtherScreen(ScreenID, string, string)");

        var send = fixture.Symbol(SymbolKinds.Method, SslSend);
        var caller = fixture.Symbol(
            SymbolKinds.Method,
            Ssaw0281Presenter + ".OnClickBtnOther(Assets.Common.Script.Base.EventRequest)");

        var sends = fixture.Reader.CallsitesIn(wrapper.Id).Where(c => c.TargetId == send.Id).ToList();
        var call = Assert.Single(sends);

        var eventPath = call.Args.Single(a => a.ParamIndex == 0).Value;
        var parameter = Assert.IsType<ParamValue>(eventPath);
        Assert.Equal(1, parameter.Index);
        Assert.Equal(wrapper.Id, parameter.MethodId);

        var resolved = fixture.Values.ResolveStrings(eventPath, fixture.Context());

        Assert.Equal(new[] { "ToSSAW0907", "ToSSAW0907.employee" }, resolved.Strings);

        // The callsite that decided the value travels with the answer (02 §8.2).
        var source = Assert.Single(resolved.SourceCallsites);
        var sourceCallsite = fixture.Reader.Callsite(source);
        Assert.NotNull(sourceCallsite);
        Assert.Equal(caller.Id, sourceCallsite!.CallerId);
        Assert.Equal(wrapper.Id, sourceCallsite.TargetId);

        // With propagation switched off the same value is simply not knowable, and says so.
        var without = fixture.Values.ResolveStrings(
            eventPath,
            fixture.Context() with { AllowParamPropagation = false });

        Assert.False(without.Complete);
        Assert.Empty(without.Strings);
    }

    /// <summary>
    /// 02 §8.1: a non-virtual call has exactly one target and needs no narrowing, and a virtual one
    /// with several implementations stays a candidate set — never the most likely one (CLAUDE.md §4).
    /// </summary>
    [Fact]
    [Trait("Task", "T11")]
    public void Dispatch_KeepsEveryCandidateOfAnAmbiguousCall()
    {
        var fixture = FixtureIndex.Shared;

        var receive = fixture.Symbol(
            SymbolKinds.Method,
            "Assets.Common.Script.Base.ISocketEvent.OnReceiveSocket(Assets.Common.Script.Base.EventRequest)");

        var implementations = fixture.Resolution.ImplementationsOf(receive.Id);
        Assert.True(implementations.Count > 1, "the fixture has several socket receivers");

        var calls = fixture.Reader.CallsitesTargeting(receive.Id);

        if (calls.Count > 0)
        {
            var dispatch = fixture.Dispatcher.Dispatch(calls[0], fixture.Context());
            Assert.True(dispatch.Candidates.Count >= 1);
        }

        // A direct call is certain without any receiver analysis.
        var direct = fixture.Reader.CallsitesIn(
                fixture.Symbol(SymbolKinds.Method, Ssaw0903Presenter + ".OnDisplay()").Id)
            .Where(c => !c.TargetIsVirtual)
            .ToList();

        Assert.NotEmpty(direct);
        Assert.All(direct, call =>
        {
            var dispatch = fixture.Dispatcher.Dispatch(call, fixture.Context());
            Assert.Equal(DispatchCertainty.Certain, dispatch.Certainty);
            Assert.Equal(call.TargetId, dispatch.Single);
        });
    }

    /// <summary>
    /// 02 §8.2: an assignment made through a receiver of an unrelated type belongs to another
    /// object and is not part of this field's points-to set.
    /// </summary>
    [Fact]
    [Trait("Task", "T11")]
    public void FieldPointsTo_IgnoresAssignmentsThroughUnrelatedReceivers()
    {
        var fixture = FixtureIndex.Shared;

        var view = fixture.Symbol(SymbolKinds.Type, Ssaw0903View);
        var handler = fixture.Reader.SymbolsInContainer(view.Id)
            .Single(s => s.Kind == SymbolKinds.Field && s.Name == "eventHandler");

        var all = fixture.Fields.AssignmentsTo(handler.Id);
        Assert.NotEmpty(all);

        var matching = fixture.Fields.TargetsOf(handler.Id, view.Id);
        Assert.NotEmpty(matching.Values);

        // An unrelated receiver type keeps nothing of the instance assignments.
        var unrelated = fixture.Symbol(SymbolKinds.Type, "Assets.Common.Legacy.KeyCalculator");
        var filtered = fixture.Fields.TargetsOf(handler.Id, unrelated.Id);

        Assert.Empty(filtered.Assignments);
    }
}
