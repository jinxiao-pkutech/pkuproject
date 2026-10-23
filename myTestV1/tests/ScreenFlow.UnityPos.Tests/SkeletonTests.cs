using ScreenFlow.UnityPos;
using Xunit;

namespace ScreenFlow.UnityPos.Tests;

public sealed class SkeletonTests
{
    /// <summary>
    /// The adapters are driven entirely by the anchor file (CLAUDE.md §4), so the path they look it
    /// up by is part of the contract with the CLI's config resolution.
    /// </summary>
    [Fact]
    [Trait("Task", "T00")]
    public void Domain_PointsAtTheAnchorFile()
    {
        Assert.Equal("config/unitypos.json", UnityPosDomainInfo.ConfigRelativePath);
        Assert.Equal("unitypos", UnityPosDomainInfo.DomainId);
    }
}
