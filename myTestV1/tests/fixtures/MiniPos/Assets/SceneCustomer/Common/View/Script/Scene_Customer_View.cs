//▼ ADD 2024.01.17 Fixture@Test 夹具：03 §6 の形
using System;
using System.Collections.Generic;
using System.Reflection;
using Assets.Common.Script.Base;
using Assets.SceneCustomer.Reception.EdyCharge.Presenter.Script;
using Assets.SceneCustomer.Reception.EdyCharge.View.Script;

namespace Assets.SceneCustomer.Common.View.Script
{
    public class Scene_Customer_View : BaseScreenManager
    {
        public static readonly Dictionary<ScreenID, (Type screen, Type presenter)> InitDictionary =
            new Dictionary<ScreenID, (Type screen, Type presenter)>
        {
            { ScreenID.SSAW0903, new(typeof(EdyCharge_SSAW0903_View), typeof(EdyCharge_SSAW0903_Presenter)) },
            { ScreenID.SSAW0907, new(typeof(EdyCharge_SSAW0907_View), typeof(EdyCharge_SSAW0907_Presenter)) },
        };

        protected override BaseScreen InstanceScreenInit(ScreenID screenId)
        {
            Type screenType = Scene_Customer_View.InitDictionary[screenId].screen;
            ConstructorInfo constructor = screenType.GetConstructor(new Type[] { typeof(Components), typeof(ScreenID) });
            BaseScreen screen = (BaseScreen)constructor.Invoke(new object[] { base.components, screenId });
            this.screenList.Add(screen);
            return screen;
        }
    }
}
//▲ ADD 2024.01.17 Fixture@Test 夹具
