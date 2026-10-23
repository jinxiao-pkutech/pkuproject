//▼ ADD 2024.01.17 Fixture@Test 夹具：同名クラスその 1（E-06）
using Assets.Common.Script.Base;

namespace Assets.ScenePosRegister.Misc
{
    // Same simple name as Assets.ScenePosRegister.Misc2.KsPosa. Resolution must go by symbol id,
    // never by simple name (CLAUDE.md §4).
    public class KsPosa
    {
        private readonly BaseScreenManager screenManager;

        public KsPosa(BaseScreenManager screenManager) => this.screenManager = screenManager;

        public void Go() => screenManager.DisplayTargetScreen(ScreenID.SSAW0089);
    }
}
//▲ ADD 2024.01.17 Fixture@Test 夹具
