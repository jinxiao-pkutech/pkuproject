using ScreenFlow.Core;
using Xunit;

namespace ScreenFlow.Core.Tests;

/// <summary>
/// T00 wiring checks. The version constants are not decoration: the IR version gates whether an
/// extractor's output is accepted, and the rules version is part of the snapshot input digest, so
/// an empty one would silently make every index look interchangeable.
/// </summary>
public sealed class SkeletonTests
{
    [Fact]
    [Trait("Task", "T00")]
    public void Core_DeclaresVersionIdentities()
    {
        Assert.False(string.IsNullOrWhiteSpace(ScreenFlowVersion.IrSchema));
        Assert.False(string.IsNullOrWhiteSpace(ScreenFlowVersion.EnvelopeSchema));
        Assert.False(string.IsNullOrWhiteSpace(ScreenFlowVersion.Rules));
    }
}
