using ScreenFlow.Core.Ir;
using ScreenFlow.UnityPos.Resources;
using Xunit;
using Xunit.Abstractions;

namespace ScreenFlow.UnityPos.Tests.Resources;

/// <summary>
/// The resource readers (02 §9.9; 03 §14–§16). Covers E-21 and E-23.
/// <para>
/// These files are where every piece of screen text comes from, and the YAML line parser is the one
/// place where a quietly wrong answer is most likely: a mis-read scalar does not fail, it just
/// returns the wrong text.
/// </para>
/// </summary>
public sealed class ResourcesTests
{
    private const string CustomerCollection = "StringLocalizationTable_Customer";
    private const string PosRegisterCollection = "StringLocalizationTable_PosRegister";

    private readonly ITestOutputHelper _output;

    public ResourcesTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// E-21: every scalar form 03 §15 documents. The folded one and the escaped one are the two the
    /// real tables are full of, and both are read as the text a screen actually shows.
    /// </summary>
    [Fact]
    [Trait("Task", "T13")]
    public void Yaml_ScalarForms()
    {
        var index = Localization();

        // A double-quoted scalar folded over two lines becomes one space (02 §9.9).
        Assert.Equal(
            "チャージ額を 選択して下さい",
            index.Text(CustomerCollection, "SSAW0903_1", "ja"));

        // A bare scalar is taken as written.
        Assert.Equal("閉じる", index.Text(CustomerCollection, "SSAW0903_2", "ja"));
        Assert.Equal("1,000円", index.Text(CustomerCollection, "SSAW0903_6", "ja"));

        // A single-quoted scalar loses its quotes, not its content.
        Assert.Equal("2,000円", index.Text(CustomerCollection, "SSAW0903_7", "ja"));

        // \uXXXX escapes are decoded, and the markup is still there in the raw form (03 §15).
        Assert.Equal(
            "<line-height=48.0><align=center>その他の金額を入力<br>(1,000円単位)",
            index.Text(CustomerCollection, "SSAW0903_12", "ja"));

        // An empty scalar is null: the key exists, the text does not.
        Assert.Null(index.Text(CustomerCollection, "SSAW0907_2", "ja"));
        Assert.Contains("SSAW0907_2", index.KeysOf(CustomerCollection));

        // The remaining forms, on a hand-written table, so each one is covered on its own.
        var parsed = UnityYamlStringTable.Parse("Snippet.asset", ScalarSnippet.Split('\n'));

        var texts = parsed.Data.ToDictionary(entry => entry.Id, entry => entry.Text);
        Assert.Equal("tab\there", texts[1]);
        Assert.Equal("quote\" and backslash\\ and slash/", texts[2]);
        Assert.Equal("line\nbreak", texts[3]);
        Assert.Equal("it's quoted", texts[4]);
        Assert.Equal("joined without a space", texts[5]);
        Assert.Equal("bare folded over two lines", texts[6]);
        Assert.Null(texts[7]);
        Assert.Equal("\\q kept as written", texts[8]);
    }

    /// <summary>
    /// 03 §15: a locale table names its shared table by guid, and that is the only link. Matching
    /// the two by file name would be a guess, and one key exists in several collections.
    /// </summary>
    [Fact]
    [Trait("Task", "T13")]
    public void Yaml_SharedDataLinkedByGuid()
    {
        var index = Localization();

        var shared = index.Files.Where(file => file.IsShared).ToList();
        var locales = index.Files.Where(file => file.IsLocale).ToList();

        Assert.Equal(2, shared.Count);
        Assert.Equal(3, locales.Count);

        foreach (var file in index.Files)
            _output.WriteLine($"{file.Path} shared={file.IsShared} locale={file.LocaleCode} guid={file.CollectionGuid ?? file.SharedDataGuid}");

        // Both collections were linked, each to its own locale tables.
        Assert.Equal(
            new[] { CustomerCollection, PosRegisterCollection },
            index.Collections);

        Assert.Equal(new[] { "en", "ja" }, index.LocalesOf(CustomerCollection));
        Assert.Equal(new[] { "ja" }, index.LocalesOf(PosRegisterCollection));

        // The guid of the customer locale table is the guid the customer shared table declares.
        var customerShared = shared.Single(file => file.CollectionName == CustomerCollection);
        var customerJa = locales.Single(file => file.Path.EndsWith("_Customer_ja.asset", StringComparison.Ordinal));

        Assert.Equal(customerShared.CollectionGuid, customerJa.SharedDataGuid);
        Assert.NotEqual(
            customerShared.CollectionGuid,
            shared.Single(file => file.CollectionName == PosRegisterCollection).CollectionGuid);

        // A key only the other collection has is not found in this one (03 §15).
        Assert.Null(index.Text(CustomerCollection, "SSAW0001_1", "ja"));

        // The search order decides which collection answers (03 §15).
        var (collection, raw) = index.Lookup(
            new[] { CustomerCollection, PosRegisterCollection }, "SSAW0903_2", "ja");

        Assert.Equal(CustomerCollection, collection);
        Assert.Equal("閉じる", raw);
    }

    /// <summary>
    /// 02 §9.9 and 03 §15: the plain form is what a reader compares against. <c>&lt;br&gt;</c> is a
    /// line break; every other tag disappears; the inner spacing stays.
    /// </summary>
    [Fact]
    [Trait("Task", "T13")]
    public void RichText_StripsTagsAndBr()
    {
        Assert.Equal(
            "その他の金額を入力\n(1,000円単位)",
            RichText.Plain("<line-height=48.0><align=center>その他の金額を入力<br>(1,000円単位)"));

        Assert.Equal("The key operation is not correct", RichText.Plain("<align=right><size=18px>The key operation is not correct"));
        Assert.Equal("a\nb", RichText.Plain("a<br/>b"));
        Assert.Equal("a\nb", RichText.Plain("a<br />b"));
        Assert.Equal("a\nb", RichText.Plain("a<BR>b"));

        // A real newline is already a line break and stays one.
        Assert.Equal("a\nb", RichText.Plain("a\nb"));

        // Inner whitespace is content; only the ends are trimmed (02 §9.9).
        Assert.Equal("a  b", RichText.Plain("  <i>a  b</i>  "));

        // A lone angle bracket is text, not a tag.
        Assert.Equal("1 < 2", RichText.Plain("1 < 2"));
        Assert.Equal(string.Empty, RichText.Plain("<align=center>"));
        Assert.False(RichText.HasMarkup("plain"));
    }

    /// <summary>
    /// 03 §14.3: the same <c>name</c> occurs several times in one UXML — once per amount button —
    /// and only the hierarchy path tells them apart. Binding text by name alone would label the
    /// wrong button.
    /// </summary>
    [Fact]
    [Trait("Task", "T13")]
    public void Uxml_DuplicateNames_DistinguishedByPath()
    {
        var diagnostics = new List<DiagRec>();
        var index = UxmlIndex.Load(DomainIndex.MiniPos, "Assets/**/*.uxml", diagnostics);

        Assert.Empty(diagnostics);
        Assert.Equal(2, index.Files.Count);

        var file = index.FileOfView("EdyCharge_SSAW0903_View");
        Assert.NotNull(file);

        var moneyLabels = index.ByName(file!, "moneyLabel");
        foreach (var control in moneyLabels) _output.WriteLine(control.FullPath);

        // Three controls share the name; their paths are all different (03 §14.3).
        Assert.Equal(3, moneyLabels.Count);
        Assert.Equal(3, moneyLabels.Select(c => c.FullPath).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(moneyLabels, c => c.HierarchyPath.EndsWith("selectCharge_1000", StringComparison.Ordinal));
        Assert.Contains(moneyLabels, c => c.HierarchyPath.EndsWith("selectCharge_Other", StringComparison.Ordinal));

        // The element name is the full control type (03 §14.3).
        var other = index.ByName(file!, "MoneyOtherLabel").Single();
        Assert.Equal("Assets.Common.Script.Localization.Control.LocalizeLabel", other.ElementType);
        Assert.Contains("selectCharge_Other", other.HierarchyPath, StringComparison.Ordinal);

        // A ui:-prefixed element is a control too when it has a name; the document element is not.
        Assert.Contains(index.Controls(file!), c => c.Name == "EdyCharge_SSAW0903_View");
        Assert.DoesNotContain(index.Controls(file!), c => c.ElementType == "UXML");
        Assert.DoesNotContain(index.Controls(file!), c => c.ElementType == "Style");

        var closeButton = index.ByName(file!, "btnClose").Single();
        Assert.Equal("Assets.Common.View.Parts.BTN18_ButtonBack", closeButton.ElementType);
    }

    /// <summary>
    /// 03 §14.1: the initialize JSON of a view is found by the view's own name without its suffix,
    /// and its values are localization keys — including the array a button row is bound to.
    /// </summary>
    [Fact]
    [Trait("Task", "T13")]
    public void InitJson_LookupByViewName()
    {
        var diagnostics = new List<DiagRec>();

        var index = InitJsonIndex.Load(
            DomainIndex.MiniPos,
            "Assets/Common/View/Resources/ScreenInitialize",
            "{ViewWithoutSuffix}_Initialize.json",
            diagnostics);

        Assert.Empty(diagnostics);
        Assert.Equal(3, index.Files.Count);

        var entry = index.ForView("EdyCharge_SSAW0903_View", "_View");
        Assert.NotNull(entry);
        Assert.Equal(
            "Assets/Common/View/Resources/ScreenInitialize/EdyCharge_SSAW0903_Initialize.json",
            entry!.File);

        Assert.Equal("SSAW0903_1", InitJsonIndex.Key(entry.Root, "TextTitle"));
        Assert.Equal("SSAW0903_2", InitJsonIndex.Key(entry.Root, "ButtonClose"));

        // The button row is indexed, which is what pairs a button with its label (03 §14.2).
        Assert.Equal("SSAW0903_6", InitJsonIndex.Key(entry.Root, "ButtonTitle", 0));
        Assert.Equal("SSAW0903_7", InitJsonIndex.Key(entry.Root, "ButtonTitle", 1));
        Assert.Equal("SSAW0903_12", InitJsonIndex.Key(entry.Root, "ButtonTitle", 2));
        Assert.Null(InitJsonIndex.Key(entry.Root, "ButtonTitle", 3));

        // A property the file does not have is absent, not empty.
        Assert.Null(InitJsonIndex.Key(entry.Root, "IconLabel"));
        Assert.Equal(new[] { "ButtonClose", "ButtonTitle", "TextTitle" }, InitJsonIndex.Properties(entry.Root));

        // The key names differ per screen, so the index keeps whatever the file holds (03 §14.1).
        var ssaw0907 = index.ForView("EdyCharge_SSAW0907_View", "_View");
        Assert.NotNull(ssaw0907);
        Assert.NotEqual(
            InitJsonIndex.Properties(entry.Root),
            InitJsonIndex.Properties(ssaw0907!.Root));

        Assert.Null(index.ForView("No_Such_View", "_View"));
    }

    /// <summary>
    /// E-23: error code 22 in every locale the configuration maps, both the normal and the
    /// full-self text (03 §16). Which one a screen shows depends on the run-time mode, so both are
    /// stored and neither is chosen here.
    /// </summary>
    [Fact]
    [Trait("Task", "T13")]
    public void Messages_ErrB022()
    {
        var index = DomainIndex.Editor;

        using var command = index.Db.Command(
            "SELECT locale, text_raw, text_plain FROM messages WHERE code = 22 ORDER BY locale;");

        var rows = new Dictionary<string, (string? Raw, string? Plain)>(StringComparer.Ordinal);
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                rows[reader.GetString(0)] = (
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2));
            }
        }

        foreach (var (locale, text) in rows) _output.WriteLine($"{locale}: {text.Raw}");

        // Every locale field of the configuration that the entry carries (03 §16).
        Assert.Equal(
            new[] { "en", "ja", "ja-hira", "ja-selfpos", "ko", "zh-Hans", "zh-Hant" },
            rows.Keys.OrderBy(locale => locale, StringComparer.Ordinal));

        Assert.Equal("登録できない数です", rows["ja"].Raw);
        Assert.Equal("登録できない数です", rows["ja-selfpos"].Raw);
        Assert.Equal("とうろくできない　数です", rows["ja-hira"].Raw);

        // The markup is kept raw and removed in the plain form.
        Assert.Equal("<align=right><size=18px>The number cannot be registered", rows["en"].Raw);
        Assert.Equal("The number cannot be registered", rows["en"].Plain);

        // Error 2 and error 22 are different messages: 03 §13.3 notes the fixture's comments have
        // them the wrong way round, and the code is what decides (E-23).
        Assert.Equal("数が大きすぎます", MessageOf(index, 2, "ja"));
        Assert.NotEqual(MessageOf(index, 2, "ja"), rows["ja"].Raw);
        Assert.Equal("登録できない数です", MessageOf(index, 22, "ja"));
    }

    private static string? MessageOf(DomainIndex index, int code, string locale)
    {
        using var command = index.Db.Command(
            "SELECT text_raw FROM messages WHERE code = $code AND locale = $locale;");

        command.Parameters.AddWithValue("$code", code);
        command.Parameters.AddWithValue("$locale", locale);

        var value = command.ExecuteScalar();
        return value as string;
    }

    private static LocalizationIndex Localization()
    {
        var diagnostics = new List<DiagRec>();
        var index = LocalizationIndex.Load(DomainIndex.MiniPos, "Assets/Localization/StringTable", diagnostics);

        Assert.Empty(diagnostics);
        return index;
    }

    /// <summary>
    /// The scalar forms the fixture does not happen to contain. Written here rather than added to
    /// MiniPos, because 03 §15 describes the forms and the fixture only has to contain the ones the
    /// handbook shows.
    /// </summary>
    private const string ScalarSnippet = """
        MonoBehaviour:
          m_Name: Snippet
          m_LocaleId:
            m_Code: ja
          m_SharedData: {fileID: 11400000, guid: abc123, type: 2}
          m_TableData:
          - m_Id: 1
            m_Localized: "tab\there"
            m_Metadata:
              m_Items: []
          - m_Id: 2
            m_Localized: "quote\" and backslash\\ and slash\/"
          - m_Id: 3
            m_Localized: "line\nbreak"
          - m_Id: 4
            m_Localized: 'it''s quoted'
          - m_Id: 5
            m_Localized: "joined without \
              a space"
          - m_Id: 6
            m_Localized: bare folded
              over two lines
          - m_Id: 7
            m_Localized:
          - m_Id: 8
            m_Localized: "\q kept as written"
        """;
}
