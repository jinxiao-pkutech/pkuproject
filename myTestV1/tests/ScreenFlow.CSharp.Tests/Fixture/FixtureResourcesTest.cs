using System.Text;
using System.Text.Json;
using System.Xml;
using Xunit;

namespace ScreenFlow.CSharp.Tests.Fixture;

/// <summary>
/// Checks that the fixture's resource files exist and are in the serialised forms 03 §14–§16
/// describe. Only presence and basic well-formedness are asserted here; the readers that interpret
/// them are built in T13, and asserting their meaning now would be asserting code that does not
/// exist yet (T05 §5).
/// </summary>
public sealed class FixtureResourcesTest
{
    [Fact]
    [Trait("Task", "T05")]
    public void Fixture_ResourcesPresent()
    {
        AssertUxml("Assets/SceneCustomer/Reception/EdyCharge/View/Screen/EdyCharge_SSAW0903_View.uxml");
        AssertUxml("Assets/SceneCustomer/Reception/EdyCharge/View/Screen/EdyCharge_SSAW0907_View.uxml");

        AssertJson("Assets/Common/View/Resources/ScreenInitialize/EdyCharge_SSAW0903_Initialize.json", JsonValueKind.Object);
        AssertJson("Assets/Common/View/Resources/ScreenInitialize/EdyCharge_SSAW0907_Initialize.json", JsonValueKind.Object);
        AssertJson("Assets/Common/View/Resources/ScreenInitialize/SSAW0001_Initialize.json", JsonValueKind.Object);
        AssertJson("Assets/StreamingAssets/ErrMsg/ErrMsgType1_Initialize.json", JsonValueKind.Array);

        AssertYaml("Assets/Localization/StringTable/Customer/StringLocalizationTable_Customer Shared Data.asset");
        AssertYaml("Assets/Localization/StringTable/Customer/StringLocalizationTable_Customer_ja.asset");
        AssertYaml("Assets/Localization/StringTable/Customer/StringLocalizationTable_Customer_en.asset");
        AssertYaml("Assets/Localization/StringTable/PosRegister/StringLocalizationTable_PosRegister Shared Data.asset");
        AssertYaml("Assets/Localization/StringTable/PosRegister/StringLocalizationTable_PosRegister_ja.asset");
    }

    /// <summary>
    /// The shared table's collection guid and each language table's <c>m_SharedData.guid</c> have to
    /// agree, because that is the only thing linking a key to its text (03 §15).
    /// </summary>
    [Fact]
    [Trait("Task", "T05")]
    public void Fixture_StringTableGuidsLink()
    {
        foreach (var collection in new[] { "Customer", "PosRegister" })
        {
            var sharedText = ReadFixtureText(
                $"Assets/Localization/StringTable/{collection}/StringLocalizationTable_{collection} Shared Data.asset");

            var guid = Between(sharedText, "m_TableCollectionNameGuidString: ", "\n");
            Assert.False(string.IsNullOrWhiteSpace(guid), $"{collection}: no collection guid");

            var languageFiles = Directory.EnumerateFiles(
                    Path.Combine(FixtureLayout.AssetsRoot, "Localization", "StringTable", collection),
                    "*_??.asset")
                .ToList();
            Assert.NotEmpty(languageFiles);

            foreach (var file in languageFiles)
            {
                var text = File.ReadAllText(file);
                Assert.Contains($"guid: {guid}", text, StringComparison.Ordinal);
            }
        }
    }

    /// <summary>
    /// 05 §3.3 requires the ja table to carry all five scalar forms. They exist so the reader in T13
    /// is forced to handle the real serialised shapes rather than one convenient one.
    /// </summary>
    [Fact]
    [Trait("Task", "T05")]
    public void Fixture_JaTableCarriesEveryScalarForm()
    {
        var text = ReadFixtureText("Assets/Localization/StringTable/Customer/StringLocalizationTable_Customer_ja.asset");
        var backslash = ((char)92).ToString();

        // Double quoted with \uXXXX escapes, still escaped on disk.
        Assert.Contains($"\"<line-height=48.0><align=center>{backslash}u305D", text, StringComparison.Ordinal);
        Assert.DoesNotContain("その他の金額を入力<br>", text, StringComparison.Ordinal);

        // Bare, single quoted, folded over two lines, and empty.
        Assert.Contains("m_Localized: 1,000円", text, StringComparison.Ordinal);
        Assert.Contains("m_Localized: '2,000円'", text, StringComparison.Ordinal);
        Assert.Contains("m_Localized: \"チャージ額を\n      選択して下さい\"", text.Replace("\r\n", "\n"), StringComparison.Ordinal);
        Assert.Matches(@"m_Localized:\s*\n\s+m_Metadata:", text.Replace("\r\n", "\n"));
    }

    [Fact]
    [Trait("Task", "T05")]
    public void Fixture_ErrorMessagesCoverTheAcceptanceCodes()
    {
        using var document = JsonDocument.Parse(
            ReadFixtureText("Assets/StreamingAssets/ErrMsg/ErrMsgType1_Initialize.json"));

        var codes = document.RootElement.EnumerateArray()
            .Select(e => e.GetProperty("ErrCode").GetInt32())
            .ToList();

        Assert.Equal(new[] { 1, 2, 22, 24 }, codes);

        // ERRB022's text is taken verbatim from 03 §16 and is what E-23 asserts later.
        var twentyTwo = document.RootElement.EnumerateArray()
            .Single(e => e.GetProperty("ErrCode").GetInt32() == 22);
        Assert.Equal("登録できない数です", twentyTwo.GetProperty("Msg").GetString());
        Assert.Equal("登録できない数です", twentyTwo.GetProperty("SelfPosMsg").GetString());
        Assert.Equal("<align=right><size=18px>The number cannot be registered", twentyTwo.GetProperty("SelfPosMsg_EN").GetString());
    }

    [Fact]
    [Trait("Task", "T05")]
    public void Fixture_Key01_IsCp932()
    {
        var path = FixtureLayout.Cp932FilePath;
        Assert.True(File.Exists(path), $"{path} is missing; run python tools/make-cp932.py");

        var bytes = File.ReadAllBytes(path);

        // Not valid UTF-8: that is the whole point of this file (05 §3.4).
        Assert.Throws<DecoderFallbackException>(() =>
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes));

        // CP932 decodes it, and the Japanese literal survives intact.
        var text = FixtureLayout.Cp932.GetString(bytes);
        Assert.Contains("public const string Label = \"合計金額\";", text, StringComparison.Ordinal);
        Assert.Contains("合計金額のラベル", text, StringComparison.Ordinal);

        // The detector picks CP932 on its own, which is what the extractor will rely on.
        Assert.Equal(FixtureLayout.Cp932.CodePage, FixtureLayout.DetectEncoding(bytes).CodePage);

        // Every other fixture source stays UTF-8, so the fallback is exercised by exactly one file.
        var nonUtf8 = FixtureLayout.SourceFiles()
            .Where(p => FixtureLayout.DetectEncoding(File.ReadAllBytes(p)).CodePage == FixtureLayout.Cp932.CodePage)
            .Select(Path.GetFileName)
            .ToList();
        Assert.Equal(new[] { FixtureLayout.Cp932FileName }, nonUtf8);
    }

    private static void AssertUxml(string relative)
    {
        var path = FixturePath(relative);
        Assert.True(File.Exists(path), $"{relative} is missing");

        // Hardened reader: a UXML must not be able to pull in an external entity (02 §9.9 policy).
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersFromEntities = 0
        };

        using var stream = File.OpenRead(path);
        using var reader = XmlReader.Create(stream, settings);
        var elements = 0;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element) elements++;
        }

        Assert.True(elements > 3, $"{relative} has only {elements} element(s)");
    }

    private static void AssertJson(string relative, JsonValueKind expected)
    {
        var path = FixturePath(relative);
        Assert.True(File.Exists(path), $"{relative} is missing");

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(expected, document.RootElement.ValueKind);
    }

    private static void AssertYaml(string relative)
    {
        var path = FixturePath(relative);
        Assert.True(File.Exists(path), $"{relative} is missing");

        var lines = File.ReadAllLines(path);
        Assert.True(lines.Length > 5, $"{relative} is too short to be a table");
        Assert.Equal("%YAML 1.1", lines[0]);
        Assert.Equal("%TAG !u! tag:unity3d.com,2011:", lines[1]);
        Assert.StartsWith("--- !u!114 &11400000", lines[2], StringComparison.Ordinal);
    }

    private static string FixturePath(string relative)
        => Path.Combine(FixtureLayout.MiniPos, relative.Replace('/', Path.DirectorySeparatorChar));

    private static string ReadFixtureText(string relative) => File.ReadAllText(FixturePath(relative));

    private static string Between(string text, string start, string end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        if (from < 0) return string.Empty;
        from += start.Length;
        var to = text.IndexOf(end, from, StringComparison.Ordinal);
        return to < 0 ? text[from..].Trim() : text[from..to].Trim();
    }
}
