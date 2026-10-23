using ScreenFlow.Core.Ir;
using ScreenFlow.CSharp;
using Xunit;

namespace ScreenFlow.CSharp.Tests.Walkers;

/// <summary>Interface implementation mapping (02 §5.3, §6.5) and the T08 record set as a whole.</summary>
public sealed class ImplCollectorTests
{
    private const string PresenterFq =
        "Assets.SceneCustomer.Reception.EdyCharge.Presenter.Script.EdyCharge_SSAW0903_Presenter";

    private const string ViewEventFq =
        "Assets.SceneCustomer.Reception.EdyCharge.View.Script.IEdyCharge_SSAW0903_View_Event";

    /// <summary>
    /// The presenter is reached from the view through the event interface (03 §9), so the mapping
    /// from <c>IEdyCharge_SSAW0903_View_Event.OnClickBtnOther</c> to the presenter's method is what
    /// keeps that hop from becoming a break link.
    /// </summary>
    [Fact]
    [Trait("Task", "T08")]
    public void Impl_PresenterImplementsViewEvent()
    {
        var fixture = ExtractedFixture.Editor;

        var presenter = fixture.Symbol(SymbolKinds.Type, PresenterFq);
        var contractMember = fixture.Symbol(SymbolKinds.Method, ViewEventFq + ".OnClickBtnOther()");
        var implMember = fixture.Symbol(SymbolKinds.Method, PresenterFq + ".OnClickBtnOther()");

        var mapping = fixture.Impls
            .Where(i => i.TypeId == presenter.Id && i.InterfaceMemberId == contractMember.Id)
            .ToList();

        var single = Assert.Single(mapping);
        Assert.Equal(implMember.Id, single.ImplMemberId);

        // All four handlers of the interface are mapped, not only the one under test.
        foreach (var name in new[] { "OnClickBtnClose", "OnClickBtn1000", "OnClickBtn2000", "OnClickBtnOther" })
        {
            var member = fixture.Symbol(SymbolKinds.Method, ViewEventFq + "." + name + "()");
            Assert.Contains(fixture.Impls, i => i.TypeId == presenter.Id && i.InterfaceMemberId == member.Id);
        }

        // ISocketEvent is the second interface on the same presenter (03 §10.3).
        var socketEvent = fixture.Symbol(
            SymbolKinds.Method,
            "Assets.Common.Script.Base.ISocketEvent.OnReceiveSocket(Assets.Common.Script.Base.EventRequest)");

        Assert.Contains(fixture.Impls, i => i.TypeId == presenter.Id && i.InterfaceMemberId == socketEvent.Id);
    }

    /// <summary>
    /// 02 §5.3: every id in a mapping must resolve, and a mapping is never recorded twice — a
    /// <c>partial</c> type is declared more than once but implements its interfaces once.
    /// </summary>
    [Fact]
    [Trait("Task", "T08")]
    public void Impl_RowsAreUniqueAndResolvable()
    {
        var fixture = ExtractedFixture.Editor;
        var known = fixture.Symbols.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(fixture.Impls);
        Assert.All(fixture.Impls, i => Assert.Contains(i.TypeId, known));
        Assert.All(fixture.Impls, i => Assert.Contains(i.InterfaceMemberId, known));
        Assert.All(fixture.Impls, i => Assert.Contains(i.ImplMemberId, known));

        var keys = fixture.Impls
            .Select(i => string.Join('|', i.TypeId, i.InterfaceMemberId, i.ImplMemberId))
            .ToList();

        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// 02 §5.1: the extractor emits <c>meta</c> first, and only the record kinds of 02 §5.1 — every
    /// one of them, since T10 completed the stream.
    /// </summary>
    [Fact]
    [Trait("Task", "T08")]
    public void Extractor_EmitsOnlyTheRecordKindsOfThisStage()
    {
        var fixture = ExtractedFixture.Editor;

        var meta = Assert.IsType<MetaRec>(fixture.Records[0]);
        Assert.Equal(IrValidator.SupportedIrVersion, meta.IrVersion);
        Assert.Equal(CSharpExtractor.ExtractorName, meta.Extractor);
        Assert.Equal("fixture", meta.ProfileId);
        Assert.Equal(Derivations.Native, meta.Derivation);
        Assert.Equal("2022.3.68f1", meta.UnityDeclared);

        var allowed = new[]
        {
            IrRecordKinds.Meta, IrRecordKinds.File, IrRecordKinds.Symbol, IrRecordKinds.Impl,
            IrRecordKinds.Callsite, IrRecordKinds.Assign, IrRecordKinds.Creation,
            IrRecordKinds.Cfg, IrRecordKinds.Marker, IrRecordKinds.Diag
        };

        Assert.All(fixture.Records, r => Assert.Contains(r.K, allowed));

        // Every record has to be valid against the published IR contract (02 §5).
        foreach (var record in fixture.Records)
            Assert.Empty(IrValidator.Validate(record));

        var files = fixture.Records.OfType<FileRec>().ToList();
        Assert.Equal(66, files.Count);
        Assert.Contains(files, f => f.Encoding == Encodings.Cp932);
    }
}
