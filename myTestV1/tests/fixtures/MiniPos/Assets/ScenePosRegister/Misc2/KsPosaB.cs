//▼ ADD 2024.01.17 Fixture@Test 夹具：同名クラスその 2（E-06）
using Assets.Common.Script.Base;

namespace Assets.ScenePosRegister.Misc2
{
    public class KsPosa
    {
        private readonly BaseScreenManager screenManager;

        public KsPosa(BaseScreenManager screenManager) => this.screenManager = screenManager;

        // Never reached from SSAW0001: only Misc.KsPosa.Go is called there (E-06).
        public void Go() => screenManager.DisplayTargetScreen(ScreenID.SSAW0281);
    }
}
//▲ ADD 2024.01.17 Fixture@Test 夹具
