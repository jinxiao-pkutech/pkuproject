using Xunit;

namespace ScreenFlow.Cli.Tests;

public sealed class SkeletonTests
{
    /// <summary>
    /// The executable must be called <c>screenflow</c>: 02 §11 and the MCP service in 04 both
    /// invoke it by that name, so a renamed assembly breaks the deployment rather than the build.
    /// </summary>
    [Fact]
    [Trait("Task", "T00")]
    public void Cli_AssemblyIsNamedScreenflow()
    {
        var assembly = typeof(Program).Assembly;
        Assert.Equal("screenflow", assembly.GetName().Name);
    }
}
