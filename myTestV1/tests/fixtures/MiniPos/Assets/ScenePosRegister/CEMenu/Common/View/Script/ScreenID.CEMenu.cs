//▼ ADD 2024.01.17 Fixture@Test 夹具：ScreenID の 2 つ目の partial 宣言（E-05）
public partial class ScreenID
{
    // Only reachable from a #if UNITY_EDITOR block, and never registered (E-09, E-11).
    public static readonly ScreenID SCEW0001 = new ScreenID(nameof(SCEW0001), "CEメニュー（未登記）", false);
}
//▲ ADD 2024.01.17 Fixture@Test 夹具
