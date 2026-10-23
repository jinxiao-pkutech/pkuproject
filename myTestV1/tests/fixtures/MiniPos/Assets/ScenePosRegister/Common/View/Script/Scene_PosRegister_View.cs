//▼ ADD 2024.01.17 Fixture@Test 夹具：03 §6 の形（集合初始化器 + 目标类型 new() のタプル）
using System;
using System.Collections.Generic;
using System.Reflection;
using Assets.Common.Script.Base;
using Assets.ScenePosRegister.Payment.View.Script;
using Assets.ScenePosRegister.Reception.EdyCharge.View.Script;
using Assets.ScenePosRegister.Standby.View.Script;
using Assets.ScenePosRegister.Payment.Presenter.Script;
using Assets.ScenePosRegister.Reception.EdyCharge.Presenter.Script;
using Assets.ScenePosRegister.Standby.Presenter.Script;

namespace Assets.ScenePosRegister.Common.View.Script
{
    public class Scene_PosRegister_View : BaseScreenManager
    {
        // The registry is what decides which terminal a screen belongs to (03 §3).
        public static readonly Dictionary<ScreenID, (Type screen, Type presenter)> InitDictionary
            = new Dictionary<ScreenID, (Type screen, Type presenter)>
        {
            { ScreenID.SSAW0001, new(typeof(SSAW0001_View), typeof(SSAW0001_Presenter)) },
            { ScreenID.SSAW0089, new(typeof(Payment_SSAW0089_View), typeof(Payment_SSAW0089_Presenter)) },
            { ScreenID.SSAW0281, new(typeof(EdyCharge_SSAW0281_View), typeof(EdyCharge_SSAW0281_Presenter)) },
        };

        // 03 §6: reflection covered by the registry, so it is not a break link.
        protected override BaseScreen InstanceScreenInit(ScreenID screenId)
        {
            Type screenType = Scene_PosRegister_View.InitDictionary[screenId].screen;
            ConstructorInfo constructor = screenType.GetConstructor(new Type[] { typeof(Components), typeof(ScreenID) });
            BaseScreen screen = (BaseScreen)constructor.Invoke(new object[] { base.components, screenId });
            if (screenId.IsPopUp)
            {
                this.popupList.AddLast(screen);
            }
            else
            {
                this.screenList.Add(screen);
            }

            return screen;
        }

        public BaseScreen InstancePopup(ScreenID screenId) => InstanceScreenInit(screenId);
    }
}
//▲ ADD 2024.01.17 Fixture@Test 夹具
