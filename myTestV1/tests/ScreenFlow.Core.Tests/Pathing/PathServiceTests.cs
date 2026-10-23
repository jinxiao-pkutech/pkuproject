using ScreenFlow.Core.Graph;
using ScreenFlow.Core.Pathing;
using ScreenFlow.Core.Tests.Resolution;
using Xunit;
using Xunit.Abstractions;

namespace ScreenFlow.Core.Tests.Pathing;

/// <summary>
/// Path search on the MiniPos fixture (02 §11.4, §11.5). Covers E-31 … E-34.
/// </summary>
public sealed class PathServiceTests
{
    private readonly ITestOutputHelper _output;

    public PathServiceTests(ITestOutputHelper output) => _output = output;

    private static PathService Service()
    {
        var fixture = FixtureIndex.WithGraph;
        return new PathService(fixture.Reader, fixture.Domain);
    }

    /// <summary>
    /// E-31: the amount screen is reached from the charge screen, by either of the two chains the
    /// graph found, and the button that does it carries the words the operator actually reads.
    /// </summary>
    [Fact]
    [Trait("Task", "T19")]
    public void Path_0903To0907()
    {
        var result = Service().Find("SSAW0903", "SSAW0907", new PathOptions { Detail = PathDetail.Full });

        Assert.NotEmpty(result.Paths);

        var path = result.Paths[0];
        _output.WriteLine($"cost={path.Cost} hops={path.Hops.Count} confidence={path.OverallConfidence}");

        Assert.Equal(Confidences.Candidate, path.OverallConfidence);
        Assert.True(path.ReachedTarget);

        var hop = path.Hops[0];
        Assert.Equal("customer/SSAW0903", hop.From.ToString());
        Assert.Equal("customer/SSAW0907", hop.To?.ToString());
        Assert.Equal(2, hop.Edges.Count);
        Assert.All(hop.Edges, edge => Assert.Equal("selectCharge_Other", edge.Trigger.ControlId));
        Assert.All(hop.Edges, edge => Assert.NotNull(edge.Chain));

        var text = hop.Edges[0].TriggerText;
        Assert.NotNull(text);
        Assert.Equal("ja", text!.Locale);
        Assert.Equal("その他の金額を入力\n(1,000円単位)", text.Plain);

        // The breaks the chains pass are part of the answer, not a footnote (02 §11.4).
        Assert.NotEmpty(path.Breaks);
    }

    /// <summary>
    /// E-32: from the employee standby screen the route crosses to the customer terminal — the one
    /// thing a screen-name-only graph could never show.
    /// </summary>
    [Fact]
    [Trait("Task", "T19")]
    public void Path_StandbyTo0907_CrossTerminal()
    {
        var result = Service().Find("SSAW0001", "SSAW0907");

        Assert.NotEmpty(result.Paths);

        var path = result.Paths[0];
        var screens = new[] { path.Hops[0].From.ToString() }
            .Concat(path.Hops.Select(hop => hop.To?.ToString() ?? "(break)"))
            .ToList();

        _output.WriteLine(string.Join(" → ", screens));

        Assert.Equal(
            new[] { "employee/SSAW0001", "employee/SSAW0089", "customer/SSAW0903", "customer/SSAW0907" },
            screens);
    }

    /// <summary>E-33: how an operator gets to a screen in the first place (02 §11.5-7).</summary>
    [Fact]
    [Trait("Task", "T19")]
    public void Path_Prefix()
    {
        var result = Service().Prefix("SSAW0903");

        var path = Assert.Single(result.Paths);
        Assert.Equal("employee/SSAW0001", path.Hops[0].From.ToString());
        Assert.Equal("customer/SSAW0903", path.Hops[^1].To?.ToString());
    }

    /// <summary>
    /// E-34: when there is no route, the answer says how far the analysis got and why it stopped —
    /// an empty result with no explanation would be indistinguishable from a bug (02 §11.5-5, -6).
    /// </summary>
    [Fact]
    [Trait("Task", "T19")]
    public void Path_Unreachable_ReportsPartial()
    {
        var result = Service().Find("SSAW0907", "SSAW0281");

        Assert.Empty(result.Paths);
        _output.WriteLine($"partial={result.PartialPaths.Count} note={result.Diagnosis.Note}");

        Assert.True(result.PartialPaths.Count > 0 || result.Diagnosis.Note is { Length: > 0 });

        foreach (var partial in result.PartialPaths)
        {
            Assert.False(partial.ReachedTarget);
            Assert.Null(partial.Hops[^1].To);
            Assert.NotNull(partial.Hops[^1].Edges[0].EndsAtBreak);
        }
    }

    /// <summary>
    /// 02 §11.5-8: <c>summary</c> leaves the chains out. The same search with <c>full</c> must
    /// otherwise answer identically, or the two detail levels would be two different answers.
    /// </summary>
    [Fact]
    [Trait("Task", "T19")]
    public void Path_SummaryOmitsChains()
    {
        var service = Service();
        var summary = service.Find("SSAW0903", "SSAW0907");
        var full = service.Find("SSAW0903", "SSAW0907", new PathOptions { Detail = PathDetail.Full });

        Assert.All(summary.Paths[0].Hops.SelectMany(hop => hop.Edges), edge => Assert.Null(edge.Chain));
        Assert.All(full.Paths[0].Hops.SelectMany(hop => hop.Edges), edge => Assert.NotEmpty(edge.Chain!));
        Assert.Equal(
            summary.Paths.Select(path => path.PathId),
            full.Paths.Select(path => path.PathId));
    }
}
