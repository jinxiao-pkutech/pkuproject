using System.Reflection;
using Xunit;

namespace ScreenFlow.Architecture.Tests;

/// <summary>
/// Enforces the dependency rules of 02 §1.1. These are the tests that keep the core reusable: the
/// moment Core or UnityPos can see Roslyn, swapping in an extractor for another language stops
/// being a matter of writing one new project.
/// </summary>
public sealed class ArchitectureTests
{
    private const string RoslynPrefix = "Microsoft.CodeAnalysis";

    private static readonly Assembly CoreAssembly = typeof(Core.ScreenFlowVersion).Assembly;
    private static readonly Assembly UnityPosAssembly = typeof(UnityPos.UnityPosDomainInfo).Assembly;

    [Fact]
    [Trait("Task", "T00")]
    public void Architecture_CoreHasNoRoslyn()
    {
        AssertNoRoslyn(CoreAssembly);
        AssertNoRoslyn(UnityPosAssembly);
    }

    [Fact]
    [Trait("Task", "T00")]
    public void Architecture_UnityPosHasNoCSharpExtractor()
    {
        var offenders = UnityPosAssembly.GetReferencedAssemblies()
            .Where(reference => string.Equals(reference.Name, "ScreenFlow.CSharp", StringComparison.Ordinal))
            .Select(reference => reference.FullName)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"{UnityPosAssembly.GetName().Name} must not reference ScreenFlow.CSharp (02 §1.1). Found: " +
            string.Join(", ", offenders));
    }

    [Fact]
    [Trait("Task", "T00")]
    public void Architecture_CoreDoesNotDependOnDomainOrExtractor()
    {
        var forbidden = new[] { "ScreenFlow.CSharp", "ScreenFlow.UnityPos", "ScreenFlow.Cli" };

        var offenders = CoreAssembly.GetReferencedAssemblies()
            .Where(reference => reference.Name is not null && forbidden.Contains(reference.Name, StringComparer.Ordinal))
            .Select(reference => reference.Name!)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"ScreenFlow.Core must not depend on the extractor, the domain adapters or the CLI (02 §1.1). Found: " +
            string.Join(", ", offenders));
    }

    private static void AssertNoRoslyn(Assembly assembly)
    {
        var offenders = assembly.GetReferencedAssemblies()
            .Where(reference => reference.Name?.StartsWith(RoslynPrefix, StringComparison.Ordinal) == true)
            .Select(reference => reference.Name!)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"{assembly.GetName().Name} must not reference {RoslynPrefix}.* (02 §1.1). Found: " +
            string.Join(", ", offenders));
    }
}
