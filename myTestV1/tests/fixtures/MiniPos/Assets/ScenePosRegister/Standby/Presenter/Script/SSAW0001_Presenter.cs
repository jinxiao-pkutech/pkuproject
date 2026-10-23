//▼ ADD 2024.01.17 Fixture@Test 夹具
using Assets.Common.Script.Base;
using Assets.ScenePosRegister.Common.Model.Script;
using Assets.ScenePosRegister.Common.View.Script;
using Assets.ScenePosRegister.Standby.View.Script;
using Mono.Data.Sqlite;
using UnityEngine;

namespace Assets.ScenePosRegister.Standby.Presenter.Script
{
    public class SSAW0001_Presenter : BasePresenter, ISSAW0001_View_Event
    {
        private readonly Scene_PosRegister_View view;

        private readonly Scene_PosRegister_Model model;

        private readonly BaseScreenManager screenManager;

        private SSAW0001_View SSAW0001_View;

        public SSAW0001_Presenter(Scene_PosRegister_View view, Scene_PosRegister_Model model, BaseScreenManager screenManager)
        {
            this.view = view;
            this.model = model;
            this.screenManager = screenManager;
        }

        public void OnInstanced()
        {
            this.SSAW0001_View = this.view.GetGenericScreen<SSAW0001_View>(ScreenID.SSAW0001);
            this.SSAW0001_View.eventHandler = this;
        }

        public void OnClickBtnPay()
        {
            screenManager.DisplayTargetScreen(ScreenID.SSAW0089);
        }

        public void OnDisplay()
        {
            // NativeInterop boundary (E-25).
            new AndroidJavaObject("jp.sej.pos.Beeper").Call("beep");

            LoadItems();

            // Only Misc.KsPosa is reached from here (E-06).
            new Assets.ScenePosRegister.Misc.KsPosa(screenManager).Go();

#if TeamUI
            // Never defined in any real configuration, so this must not appear (E-10).
            screenManager.DisplayTargetScreen(ScreenID.SSAW0002);
#endif

#if UNITY_EDITOR
            // Present under the editor profile only (E-11).
            screenManager.DisplayTargetScreen(ScreenID.SCEW0001);
#endif

            // screenManager.DisplayTargetScreen(ScreenID.SSAW0002);   // commented out: must not appear (E-10)
        }

        private void LoadItems()
        {
            object conn = new SqliteConnection("Data Source=pos.db");

            // DatabaseMediated boundary: the table name comes out of the SQL literal (E-25).
            SQLiteCommand command = new SQLiteCommand("SELECT item_code FROM item_master WHERE flag = 1", conn);
            command.ExecuteScalar();
        }
    }

    public class ItemWriter
    {
        // The writer side of the same table (E-25).
        public void Save()
        {
            object conn = new SqliteConnection("Data Source=pos.db");
            SQLiteCommand command = new SQLiteCommand("UPDATE item_master SET flag = 0", conn);
            command.ExecuteNonQuery();
        }
    }
}
//▲ ADD 2024.01.17 Fixture@Test 夹具
