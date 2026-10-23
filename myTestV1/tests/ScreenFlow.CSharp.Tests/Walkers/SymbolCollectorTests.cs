using ScreenFlow.Core.Ir;
using Xunit;
using Xunit.Abstractions;

namespace ScreenFlow.CSharp.Tests.Walkers;

/// <summary>Symbol extraction (02 §5.2, §5.6, §6.5). Covers E-05.</summary>
public sealed class SymbolCollectorTests
{
    private const string ScreenIdFile = "Assets/Common/View/Script/Utilities_s3/ScreenID.cs";
    private const string ScreenIdCeMenuFile = "Assets/ScenePosRegister/CEMenu/Common/View/Script/ScreenID.CEMenu.cs";

    private const string Ssaw0903View =
        "Assets.SceneCustomer.Reception.EdyCharge.View.Script.EdyCharge_SSAW0903_View";

    private readonly ITestOutputHelper _output;

    public SymbolCollectorTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// E-05: <c>ScreenID</c> is declared in two files (03 §4) and must be one symbol with both
    /// declarations, not two symbols that a later query would have to guess between.
    /// </summary>
    [Fact]
    [Trait("Task", "T08")]
    public void Symbols_ScreenID_PartialMerged()
    {
        var fixture = ExtractedFixture.Editor;

        // ScreenID sits in the global namespace, so its fully qualified name is its name (03 §4).
        var screenId = fixture.Symbol(SymbolKinds.Type, "ScreenID");

        Assert.Equal(TypeKinds.Class, screenId.TypeKind);
        Assert.Null(screenId.Namespace);
        Assert.Contains(Modifiers.Partial, screenId.Modifiers);

        var files = screenId.Declarations.Select(d => d.File).ToList();
        Assert.Equal(2, screenId.Declarations.Count);
        Assert.Contains(ScreenIdFile, files);
        Assert.Contains(ScreenIdCeMenuFile, files);

        // file/span is the first declaration (02 §5.2); declarations are ordered by file then line.
        Assert.Equal(screenId.Declarations[0].File, screenId.File);
        Assert.Equal(screenId.Declarations[0].Span, screenId.Span);

        // The static ScreenID fields are members of that one symbol, including the one declared in
        // the second file (E-11's SCEW0001).
        var fields = fixture.Symbols
            .Where(s => s.Kind == SymbolKinds.Field && s.ContainerId == screenId.Id)
            .ToList();

        Assert.Contains(fields, f => f.Name == "SSAW0903" && f.File == ScreenIdFile);
        Assert.Contains(fields, f => f.Name == "SCEW0001" && f.File == ScreenIdCeMenuFile);

        // The initialiser is the folded value (02 §6.5): the domain adapters read the screen
        // registry out of exactly these static initialisers, so an unresolved one would hide a screen.
        var ssaw0903 = fields.Single(f => f.Name == "SSAW0903");
        var initializer = Assert.IsType<ObjectCreationValue>(ssaw0903.Initializer);
        Assert.Equal(screenId.Id, initializer.TypeId);
        Assert.Contains("nameof(SSAW0903)", initializer.Text, StringComparison.Ordinal);

        // nameof folds to the screen id itself (02 §5.4).
        var first = initializer.Args.Single(a => a.ParamIndex == 0);
        Assert.Equal("SSAW0903", Assert.IsType<ConstValue>(first.Value).ConstantValue!.GetValue<string>());
    }

    /// <summary>
    /// 02 §5.6: an id is a hash of profile, assembly and symbol key, so two runs over unchanged
    /// sources must produce the same ids — otherwise no two indexes could ever be compared.
    /// </summary>
    [Fact]
    [Trait("Task", "T08")]
    public void Symbols_Ids_StableAcrossRuns()
    {
        var first = ExtractedFixture.Editor;
        var second = ExtractedFixture.Create("fixture");

        var firstIds = first.Symbols.Select(s => s.Id).OrderBy(i => i, StringComparer.Ordinal).ToList();
        var secondIds = second.Symbols.Select(s => s.Id).OrderBy(i => i, StringComparer.Ordinal).ToList();

        Assert.Equal(firstIds, secondIds);
        Assert.Equal(firstIds.Count, firstIds.Distinct(StringComparer.Ordinal).Count());
        Assert.All(firstIds, id => Assert.True(Ids.HasPrefix(id, Ids.SymbolPrefix), $"'{id}' is not a symbol id"));

        // The records themselves must be identical too, not just their ids: an id that stayed while
        // its content moved would be an undetected change (02 §5.6, E_ID_COLLISION).
        var firstById = first.Symbols.ToDictionary(s => s.Id, StringComparer.Ordinal);

        // Reported together and by symbol, so that a difference names what moved rather than
        // leaving two long JSON strings to compare by eye.
        var differences = second.Symbols
            .Where(symbol => Ids.CanonicalJson(firstById[symbol.Id]) != Ids.CanonicalJson(symbol))
            .Select(symbol => string.Join(
                Environment.NewLine,
                $"{symbol.Kind} {symbol.FqName}",
                $"  first:  {Ids.CanonicalJson(firstById[symbol.Id])}",
                $"  second: {Ids.CanonicalJson(symbol)}"))
            .ToList();

        Assert.True(
            differences.Count == 0,
            $"{differences.Count} symbol record(s) differ between two runs:" + Environment.NewLine +
            string.Join(Environment.NewLine, differences.Take(3)));
    }

    /// <summary>
    /// 02 §5.6: the ordinal is a lambda's only stable identity, and it is the source order within
    /// the containing method. <c>SetEvent</c> registers four handlers in order (03 §9).
    /// </summary>
    [Fact]
    [Trait("Task", "T08")]
    public void Symbols_LambdaOrdinals()
    {
        var fixture = ExtractedFixture.Editor;
        var setEvent = fixture.Symbol(SymbolKinds.Method, Ssaw0903View + ".SetEvent()");

        var lambdas = fixture.Symbols
            .Where(s => s.IsLambda && s.ContainerId == setEvent.Id)
            .OrderBy(s => s.Span!.StartLine)
            .ThenBy(s => s.Span!.StartColumn)
            .ToList();

        foreach (var lambda in lambdas)
            _output.WriteLine($"{lambda.Ordinal} @ {lambda.Span!.StartLine}:{lambda.Span.StartColumn} {lambda.FqName}");

        Assert.Equal(4, lambdas.Count);
        Assert.Equal(new int?[] { 0, 1, 2, 3 }, lambdas.Select(l => l.Ordinal).ToArray());
        Assert.All(lambdas, l => Assert.Equal(SymbolKinds.Method, l.Kind));
        Assert.All(lambdas, l => Assert.False(l.IsLocalFunction));
        Assert.All(lambdas, l => Assert.Equal(setEvent.Id, l.ContainerId));

        // The ordinal is part of the key, so the four ids differ even though the bodies are alike.
        Assert.Equal(4, lambdas.Select(l => l.Id).Distinct(StringComparer.Ordinal).Count());

        // A lambda in another method starts counting again at 0.
        var onClickBtnOther = fixture.Symbol(
            SymbolKinds.Method,
            "Assets.SceneCustomer.Reception.EdyCharge.Presenter.Script.EdyCharge_SSAW0903_Presenter.OnClickBtnOther()");

        var queued = fixture.Symbols.Where(s => s.IsLambda && s.ContainerId == onClickBtnOther.Id).ToList();
        Assert.Equal(new int?[] { 0 }, queued.Select(l => l.Ordinal).ToArray());
    }

    /// <summary>
    /// 02 §6.5: a metadata symbol appears once it is referenced, and only then. The fixture's own
    /// types are source symbols; what they are built on comes from the BCL.
    /// </summary>
    [Fact]
    [Trait("Task", "T08")]
    public void Symbols_ExternalSymbolsAreRecordedOnceAndNotAsSource()
    {
        var fixture = ExtractedFixture.Editor;

        var external = fixture.Symbols.Where(s => !s.IsSource).ToList();
        Assert.NotEmpty(external);
        Assert.All(external, s => Assert.Null(s.File));
        Assert.All(external, s => Assert.Null(s.Span));
        Assert.All(external, s => Assert.Empty(s.Declarations));

        // Special types keep the name they are written with (SymbolDisplayMiscellaneousOptions
        // .UseSpecialTypes), so System.Object is "object" and System.String is "string".
        Assert.Contains(external, s => s.FqName == "object" && s.Namespace == "System");
        Assert.Contains(external, s => s.FqName == "System.IComparable");
        Assert.Equal(
            external.Count,
            external.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count());

        // Enumeration implements IComparable, which is metadata: the base type and interface of a
        // source type must resolve to ids the index actually contains.
        var enumeration = fixture.Symbol(SymbolKinds.Type, "Assets.Common.Script.Base.Enumeration");
        var known = fixture.Symbols.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);

        Assert.NotNull(enumeration.BaseTypeId);
        Assert.Contains(enumeration.BaseTypeId!, known);
        Assert.NotEmpty(enumeration.InterfaceIds);
        Assert.All(enumeration.InterfaceIds, id => Assert.Contains(id, known));
    }

    /// <summary>
    /// 02 §5.2: the fixture compiles with zero errors (E-04), so every symbol in it binds. A false
    /// here would mean the extractor is guessing somewhere it should be reporting.
    /// </summary>
    [Fact]
    [Trait("Task", "T08")]
    public void Symbols_FixtureSymbolsAreCompilerCertain()
    {
        var uncertain = ExtractedFixture.Editor.Symbols.Where(s => !s.IsCompilerCertain).ToList();

        foreach (var symbol in uncertain.Take(10))
            _output.WriteLine($"{symbol.Kind} {symbol.FqName} ({symbol.File})");

        Assert.Empty(uncertain);
    }

    /// <summary>
    /// 02 §5.2: a method carries its signature and a field its type, because that is what makes two
    /// overloads tellable apart in a report.
    /// </summary>
    [Fact]
    [Trait("Task", "T08")]
    public void Symbols_MemberShapesAreRecorded()
    {
        var fixture = ExtractedFixture.Editor;

        var setView = fixture.Symbol(
            SymbolKinds.Method,
            Ssaw0903View + ".SetView(Assets.Common.View.Dto.EdyCharge_SSAW0903_Element_DTO)");

        Assert.Equal("void", setView.ReturnType!.FqName);
        var parameter = Assert.Single(setView.Parameters);
        Assert.Equal("dataIni", parameter.Name);
        Assert.Equal("Assets.Common.View.Dto.EdyCharge_SSAW0903_Element_DTO", parameter.Type.FqName);
        Assert.Equal(RefKinds.None, parameter.RefKind);

        var moneyLabel = fixture.Symbols.Single(s =>
            s.Kind == SymbolKinds.Field && s.ContainerId == fixture.Symbol(SymbolKinds.Type, Ssaw0903View).Id
            && s.Name == "moneyLabel");

        Assert.Contains(Modifiers.Const, moneyLabel.Modifiers);
        Assert.Equal("moneyLabel", moneyLabel.ConstantValue!.GetValue<string>());
        Assert.Equal("string", moneyLabel.TypeRef!.FqName);

        // A generic field keeps its definition's id plus the argument, so List<Button> is not
        // confused with List<string>.
        var listButtons = fixture.Symbols.Single(s =>
            s.Kind == SymbolKinds.Field && s.ContainerId == fixture.Symbol(SymbolKinds.Type, Ssaw0903View).Id
            && s.Name == "listButtons");

        Assert.Equal("System.Collections.Generic.List<UnityEngine.UIElements.Button>", listButtons.TypeRef!.FqName);
        var argument = Assert.Single(listButtons.TypeRef.Args);
        Assert.Equal("UnityEngine.UIElements.Button", argument.FqName);
    }

    /// <summary>02 §5.2: an overriding method points at what it overrides, so a hop can continue.</summary>
    [Fact]
    [Trait("Task", "T08")]
    public void Symbols_OverrideIsLinkedToItsBase()
    {
        var fixture = ExtractedFixture.Editor;

        var getScreenControl = fixture.Symbol(SymbolKinds.Method, Ssaw0903View + ".GetScreenControl()");
        var baseMethod = fixture.Symbol(SymbolKinds.Method, "Assets.Common.Script.Base.BaseScreen.GetScreenControl()");

        Assert.Contains(Modifiers.Override, getScreenControl.Modifiers);
        Assert.Equal(baseMethod.Id, getScreenControl.OverridesId);
    }
}
