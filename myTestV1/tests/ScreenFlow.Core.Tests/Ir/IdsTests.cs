using ScreenFlow.Core.Ir;
using Xunit;

namespace ScreenFlow.Core.Tests.IrTests;

/// <summary>Stable identifier construction (02 §5.6).</summary>
public sealed class IdsTests
{
    [Fact]
    [Trait("Task", "T02")]
    public void Ir_Ids_DeterministicAndPrefixed()
    {
        var first = Ids.SymbolId("fixture", "Assembly-CSharp", "M:Fixture.Sample.Run");
        var second = Ids.SymbolId("fixture", "Assembly-CSharp", "M:Fixture.Sample.Run");

        Assert.Equal(first, second);
        Assert.StartsWith(Ids.SymbolPrefix, first, StringComparison.Ordinal);
        Assert.Equal(Ids.SymbolPrefix.Length + Ids.HashLength, first.Length);
        Assert.True(Ids.HasPrefix(first, Ids.SymbolPrefix));

        // The profile is part of the key, so two profiles can share one database without colliding.
        Assert.NotEqual(first, Ids.SymbolId("windows-editor", "Assembly-CSharp", "M:Fixture.Sample.Run"));
        Assert.NotEqual(first, Ids.SymbolId("fixture", "Other-Assembly", "M:Fixture.Sample.Run"));

        var callsite = Ids.CallsiteId(first, "Assets/Fixture/Sample.cs", 120);
        var assign = Ids.AssignId(first, "Assets/Fixture/Sample.cs", 120);
        var creation = Ids.CreationId(first, "Assets/Fixture/Sample.cs", 120);

        Assert.StartsWith(Ids.CallsitePrefix, callsite, StringComparison.Ordinal);
        Assert.StartsWith(Ids.AssignPrefix, assign, StringComparison.Ordinal);
        Assert.StartsWith(Ids.CreationPrefix, creation, StringComparison.Ordinal);

        // Same inputs, different prefix: the hash body is shared, so only the prefix distinguishes
        // the three site kinds. That is intended, and the prefixes are what make them distinct.
        Assert.Equal(callsite[Ids.CallsitePrefix.Length..], assign[Ids.AssignPrefix.Length..]);
        Assert.NotEqual(callsite, assign);
        Assert.NotEqual(assign, creation);

        // Two sites on the same line but at different offsets are different sites.
        Assert.NotEqual(callsite, Ids.CallsiteId(first, "Assets/Fixture/Sample.cs", 121));

        // A host path separator must not change an id.
        Assert.Equal(callsite, Ids.CallsiteId(first, "Assets\\Fixture\\Sample.cs", 120));

        var evidence = Ids.EvidenceId("Assets/Fixture/Sample.cs", Span.Of(10, 5, 10, 40));
        Assert.StartsWith(Ids.EvidencePrefix, evidence, StringComparison.Ordinal);
        Assert.Equal(evidence, Ids.EvidenceId("Assets/Fixture/Sample.cs", Span.Of(10, 5, 10, 40)));
        Assert.NotEqual(evidence, Ids.EvidenceId("Assets/Fixture/Sample.cs", Span.Of(10, 5, 10, 41)));
    }

    [Fact]
    [Trait("Task", "T02")]
    public void Ir_Ids_DerivedIdIgnoresPropertyOrder()
    {
        // Two payloads with the same content but a different declaration order must derive the same
        // id, or the id would depend on who wrote the object rather than on what it says.
        var ordered = new { alpha = "a", beta = 2, gamma = new[] { "x", "y" } };
        var shuffled = new { gamma = new[] { "x", "y" }, beta = 2, alpha = "a" };

        Assert.Equal(Ids.DerivedId("se_", ordered), Ids.DerivedId("se_", shuffled));
        Assert.StartsWith("se_", Ids.DerivedId("se_", ordered), StringComparison.Ordinal);

        // Array order, by contrast, is part of the value.
        var reversedArray = new { alpha = "a", beta = 2, gamma = new[] { "y", "x" } };
        Assert.NotEqual(Ids.DerivedId("se_", ordered), Ids.DerivedId("se_", reversedArray));

        Assert.Equal("{\"alpha\":\"a\",\"beta\":2,\"gamma\":[\"x\",\"y\"]}", Ids.CanonicalJson(shuffled));
    }

    [Fact]
    [Trait("Task", "T02")]
    public void Ir_Ids_LambdaAndLocalKeysNest()
    {
        const string method = "M:Fixture.Sample.Run";

        var outer = Ids.LambdaKey(method, 0);
        var inner = Ids.LambdaKey(outer, 2);

        Assert.Equal(method + Ids.LambdaMarker + "0", outer);
        Assert.Equal(method + Ids.LambdaMarker + "0" + Ids.LambdaMarker + "2", inner);
        Assert.NotEqual(
            Ids.SymbolId("fixture", "A", outer),
            Ids.SymbolId("fixture", "A", inner));

        // A local is distinguished by its declaration offset, so two same-named locals in different
        // scopes of one method stay distinct.
        var local = Ids.LocalKey(method, "path", 310);
        Assert.Equal(method + Ids.LocalMarker + "path@310", local);
        Assert.NotEqual(
            Ids.SymbolId("fixture", "A", local),
            Ids.SymbolId("fixture", "A", Ids.LocalKey(method, "path", 420)));
    }
}
