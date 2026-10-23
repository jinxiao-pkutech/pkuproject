//▼ ADD 2024.01.17 Fixture@Test 夹具：03 §4 の形（グローバル名前空間の partial class）
//namespace Assets.Common.View.Script.Utilities_s3

// 03 §4: ScreenID is NOT an Enumeration subclass. It is a plain partial class with no namespace.
public partial class ScreenID
{
    public string Name { get; private set; }

    public string Id { get; private set; }

    public bool IsPopUp { get; private set; }

    public static ScreenID None { get; internal set; }

    public ScreenID(string id, string name, bool isDynamicScreen)
    {
        this.Id = id;
        this.Name = name;
    }

    public ScreenID(string id, string name, bool isDynamicScreen, bool isPopUp = false,
                    bool isCommonScreen = false, bool isAlwayDisplay = false)
    {
        this.Id = id;
        this.Name = name;
        this.IsPopUp = isPopUp;
    }

    public static readonly ScreenID SSAW0001 = new ScreenID(nameof(SSAW0001), "登録明細画面表示（従業員面）", false);

    // Declared but never put in a registry: a known source of break links (E-09).
    public static readonly ScreenID SSAW0002 = new ScreenID(nameof(SSAW0002), "登録画面（未登記）", false);

    public static readonly ScreenID SSAW0089 = new ScreenID(nameof(SSAW0089), "支払画面（従業員面）", false);

    public static readonly ScreenID SSAW0281 = new ScreenID(nameof(SSAW0281), "Edyチャージ額選択（従業員面）", false);

    public static readonly ScreenID SSAW0903 = new ScreenID(nameof(SSAW0903), "Edy現金チャージ額指定（客面残高不足）", true, true);

    public static readonly ScreenID SSAW0907 = new ScreenID(nameof(SSAW0907), "Edy現金チャージ額入力（客面残高不足）", true, true);

    public static readonly ScreenID SSAW1113 = new ScreenID(nameof(SSAW1113), "金額確定（従業員面）", false);
}
//▲ ADD 2024.01.17 Fixture@Test 夹具
