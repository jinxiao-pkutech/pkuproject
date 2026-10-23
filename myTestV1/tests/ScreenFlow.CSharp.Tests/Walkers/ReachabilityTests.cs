using ScreenFlow.Core.Ir;
using Xunit;

namespace ScreenFlow.CSharp.Tests.Walkers;

/// <summary>
/// Control flow reachability (02 §5.8, §6.8). Covers the unreachable half of E-10.
/// </summary>
public sealed class ReachabilityTests
{
    private const string Ssaw0281Presenter =
        "Assets.ScenePosRegister.Reception.EdyCharge.Presenter.Script.EdyCharge_SSAW0281_Presenter";

    /// <summary>
    /// E-10: the navigation after <c>return</c> in <c>Legacy</c> cannot run. It stays in the index
    /// as an unreachable fact — dropping it would hide that the code exists, and keeping it
    /// reachable would invent a transition to SSAW0002.
    /// </summary>
    [Fact]
    [Trait("Task", "T10")]
    public void Reachability_CodeAfterReturn_Unreachable()
    {
        var fixture = ExtractedFixture.Editor;

        var legacy = fixture.Symbol(SymbolKinds.Method, Ssaw0281Presenter + ".Legacy()");
        var display = fixture.Symbol(
            SymbolKinds.Method,
            "Assets.Common.Script.Base.BaseScreenManager.DisplayTargetScreen(ScreenID, bool, bool)");

        var calls = fixture.Callsites
            .Where(c => c.CallerId == legacy.Id && c.TargetId == display.Id)
            .ToList();

        var call = Assert.Single(calls);
        Assert.False(call.Reachable);

        // The same call is reachable where it is actually used, so the flag is about this site.
        var reachableElsewhere = fixture.Callsites
            .Where(c => c.TargetId == display.Id && c.CallerId != legacy.Id)
            .ToList();

        Assert.NotEmpty(reachableElsewhere);
        Assert.All(reachableElsewhere, c => Assert.True(c.Reachable));

        // The cfg record names the span, so a report can point at the dead code.
        var cfgs = fixture.Cfgs.Where(c => c.MethodId == legacy.Id).ToList();
        var cfg = Assert.Single(cfgs);
        Assert.NotEmpty(cfg.UnreachableSpans);
        Assert.Contains(cfg.UnreachableSpans, s => s.Contains(call.Span));
        Assert.True(cfg.BlockCount > 0);
    }

    /// <summary>
    /// 02 §5.8: every analysed body gets a cfg record, and everything else in the fixture is
    /// reachable — the fixture has exactly one piece of dead code (05 §3.3).
    /// </summary>
    [Fact]
    [Trait("Task", "T10")]
    public void Reachability_OnlyTheDeadCodeIsUnreachable()
    {
        var fixture = ExtractedFixture.Editor;

        var unreachable = fixture.Callsites.Where(c => !c.Reachable)
            .Select(c => c.Span)
            .Concat(fixture.Assigns.Where(a => !a.Reachable).Select(a => a.Span))
            .Concat(fixture.Creations.Where(c => !c.Reachable).Select(c => c.Span))
            .ToList();

        Assert.Single(unreachable);

        Assert.NotEmpty(fixture.Cfgs);
        Assert.All(fixture.Cfgs, c => Assert.True(c.BlockCount > 0));

        // One cfg record per analysed body, never two for the same one.
        var methodIds = fixture.Cfgs.Select(c => c.MethodId).ToList();
        Assert.Equal(methodIds.Count, methodIds.Distinct(StringComparer.Ordinal).Count());

        // No graph failed to build, so nothing was assumed reachable (02 §6.8).
        Assert.DoesNotContain(
            fixture.Diagnostics,
            d => d.Code == ScreenFlow.CSharp.Walkers.ReachabilityAnalyzer.CfgUnavailableCode);
    }

    /// <summary>
    /// 02 §6.8: a lambda has a control flow graph of its own, so dead code inside one is found
    /// even though the lambda itself is reachable.
    /// </summary>
    [Fact]
    [Trait("Task", "T10")]
    public void Reachability_DeadCodeInsideALambda()
    {
        var (bodies, symbols) = Workbench.SnippetBodies(LambdaSnippet);

        var dead = symbols.Symbols.Single(s => s.FqName == "Snippet.Dead()");
        var alive = symbols.Symbols.Single(s => s.FqName == "Snippet.Alive()");

        var deadCalls = bodies.Callsites.Where(c => c.TargetId == dead.Id).ToList();
        var aliveCalls = bodies.Callsites.Where(c => c.TargetId == alive.Id).ToList();

        Assert.False(Assert.Single(deadCalls).Reachable);
        Assert.True(Assert.Single(aliveCalls).Reachable);
    }

    private const string LambdaSnippet = """
        using System;

        public static class Snippet
        {
            public static void Dead() { }

            public static void Alive() { }

            public static Action Build()
            {
                return () =>
                {
                    Alive();
                    return;
        #pragma warning disable CS0162
                    Dead();
        #pragma warning restore CS0162
                };
            }
        }
        """;
}
