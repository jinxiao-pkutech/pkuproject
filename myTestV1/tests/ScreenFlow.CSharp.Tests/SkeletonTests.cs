using ScreenFlow.CSharp;
using Xunit;

namespace ScreenFlow.CSharp.Tests;

public sealed class SkeletonTests
{
    /// <summary>
    /// The extractor and the core must agree on the IR version. If they drift, the core would
    /// reject every record the extractor emits, so the agreement is asserted at build time rather
    /// than discovered during an index build.
    /// </summary>
    [Fact]
    [Trait("Task", "T00")]
    public void Extractor_AgreesWithCoreOnIrVersion()
    {
        Assert.Equal(Core.ScreenFlowVersion.IrSchema, CSharpExtractorInfo.IrSchemaVersion);
        Assert.Equal("csharp-roslyn", CSharpExtractorInfo.ExtractorId);
    }
}
