using ScreenFlow.Core.Config;
using ScreenFlow.Core.Ir;
using ScreenFlow.CSharp.Tests.Fixture;
using ScreenFlow.CSharp.Walkers;
using Xunit;

namespace ScreenFlow.CSharp.Tests.Walkers;

/// <summary>Change marker extraction (02 §5.8, §6.7; 03 §1.1). Covers E-08.</summary>
public sealed class MarkerScannerTests
{
    private static MarkerScanner Scanner()
    {
        var domain = ConfigLoader.LoadDomain(Path.Combine(FixtureLayout.RepoRoot, "config", "unitypos.json"));
        return new MarkerScanner(domain.ChangeMarkers);
    }

    /// <summary>E-08: at least one ▼ADD marker with its date and author.</summary>
    [Fact]
    [Trait("Task", "T08")]
    public void Markers_Extracted()
    {
        var markers = ExtractedFixture.Editor.Markers;

        Assert.NotEmpty(markers);

        var adds = markers.Where(m => m.Dir == MarkerDirections.Begin && m.Kind == "ADD").ToList();
        Assert.NotEmpty(adds);
        Assert.Contains(adds, m => m.Date == "2024.01.17" && m.Author == "Fixture@Test");

        // The ▼ on the first line and the ▲ on the last line of ScreenID.cs are both markers.
        var screenId = markers
            .Where(m => m.File == "Assets/Common/View/Script/Utilities_s3/ScreenID.cs")
            .OrderBy(m => m.Line)
            .ToList();

        Assert.Equal(2, screenId.Count);
        Assert.Equal(MarkerDirections.Begin, screenId[0].Dir);
        Assert.Equal(1, screenId[0].Line);
        Assert.Equal(MarkerDirections.End, screenId[1].Dir);
        Assert.StartsWith("//", screenId[0].Text, StringComparison.Ordinal);

        // A plain comment is not a marker: the commented-out namespace on line 2 must not appear.
        Assert.DoesNotContain(screenId, m => m.Line == 2);
    }

    /// <summary>
    /// 03 §1.1 lists the marker forms as they really occur, spacing and casing included. Each one
    /// has to parse, because a marker that fails to parse silently loses the only record of who
    /// changed a line.
    /// </summary>
    [Theory]
    [Trait("Task", "T08")]
    [InlineData("//▼ ADD 2024.01.17 A.Takagi@DoSystem 次世代システムSTEP3対応：Edyチャージ",
        "▼", "ADD", "2024.01.17", "A.Takagi@DoSystem")]
    [InlineData("// ▼ADD 2025.02.04 R.Yabuta@TSS 次世代システムSTEP3対応：#8051 スレッド干渉調査用ログ",
        "▼", "ADD", "2025.02.04", "R.Yabuta@TSS")]
    [InlineData("//▼ CHG 2024.08.26 MaiNTT31@FPT gitID#972",
        "▼", "CHG", "2024.08.26", "MaiNTT31@FPT")]
    [InlineData("// ▼ UPD 2026.01.04 Zhao,Daniel@Neu 次世代システムステップ３対応：Errorログ出力強化",
        "▼", "UPD", "2026.01.04", "Zhao,Daniel@Neu")]
    [InlineData("//▼ DEL 2024.07.05 M.Ueda@TSS 次世代システムSTEP3対応 ISSUE#2716",
        "▼", "DEL", "2024.07.05", "M.Ueda@TSS")]
    [InlineData("//▼ MOD 2026.04.27 wangfei@Neusoft rework",
        "▼", "MOD", "2026.04.27", "wangfei@Neusoft")]
    [InlineData("//▲ ADD 2024.01.17 A.Takagi@DoSystem end",
        "▲", "ADD", "2024.01.17", "A.Takagi@DoSystem")]
    public void Markers_ParseDocumentedForms(string line, string dir, string kind, string date, string author)
    {
        var marker = Scanner().ParseLine("f.cs", 7, line);

        Assert.NotNull(marker);
        Assert.Equal(dir, marker!.Dir);
        Assert.Equal(kind, marker.Kind);
        Assert.Equal(date, marker.Date);
        Assert.Equal(author, marker.Author);
        Assert.Equal(7, marker.Line);
        Assert.Equal(line.Trim(), marker.Text);
    }

    /// <summary>
    /// 03 §1.1: the author is not always a mail address, and an illegal date is kept as written
    /// (<c>2025.15.04</c> exists in the real project). Neither is repaired.
    /// </summary>
    [Fact]
    [Trait("Task", "T08")]
    public void Markers_UnusualDateAndMissingAuthorAreKeptAsWritten()
    {
        var scanner = Scanner();

        var illegalDate = scanner.ParseLine("f.cs", 1, "//▼ CHG 2025.15.04 M.Ueda@TSS 修正");
        Assert.Equal("2025.15.04", illegalDate!.Date);

        var noAuthor = scanner.ParseLine(
            "f.cs", 1, "//▼ ADD 2026.9.25 (PKU)xiaoyang2 yu [総合(3)#2995] [T090-3239]【決済】");

        Assert.Equal("2026.9.25", noAuthor!.Date);
        Assert.Null(noAuthor.Author);
    }

    /// <summary>02 §6.7 and 03 §1.1: every documented issue reference form.</summary>
    [Fact]
    [Trait("Task", "T08")]
    public void Markers_IssueRefsCoverTheDocumentedForms()
    {
        Assert.Equal(new[] { "#8051" }, MarkerScanner.IssueRefsOf("2025.02.04 R.Yabuta@TSS 対応：#8051 ログ"));
        Assert.Equal(new[] { "gitID#972" }, MarkerScanner.IssueRefsOf("2024.08.26 MaiNTT31@FPT gitID#972"));
        Assert.Equal(new[] { "ISSUE#2716" }, MarkerScanner.IssueRefsOf("2024.07.05 M.Ueda@TSS 対応 ISSUE#2716"));
        Assert.Equal(
            new[] { "総合(3)#2995", "T090-3239" },
            MarkerScanner.IssueRefsOf("2026.9.25 (PKU)xiaoyang2 yu [総合(3)#2995] [T090-3239]【決済】"));
        Assert.Equal(
            new[] { "GSEJPOS25-1909" },
            MarkerScanner.IssueRefsOf("2024.07.05 M.Ueda@TSS GSEJPOS25-1909"));
        Assert.Empty(MarkerScanner.IssueRefsOf("2024.01.17 Fixture@Test 03 §4 の形"));
    }

    /// <summary>
    /// A marker is metadata, never a parsing fact (CLAUDE.md §4): a line that merely mentions one
    /// inside code is not a marker, and a comment without the direction glyph is not either.
    /// </summary>
    [Fact]
    [Trait("Task", "T08")]
    public void Markers_NonMarkerLinesAreNotMarkers()
    {
        var scanner = Scanner();

        Assert.Null(scanner.ParseLine("f.cs", 1, "// ADD 2024.01.17 A.Takagi@DoSystem"));
        Assert.Null(scanner.ParseLine("f.cs", 1, "//▼ FIXUP 2024.01.17 A.Takagi@DoSystem"));
        Assert.Null(scanner.ParseLine("f.cs", 1, "var text = \"//▼ ADD 2024.01.17 A.Takagi@DoSystem\";"));
    }
}
