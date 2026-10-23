//▼ ADD 2024.01.17 Fixture@Test 夹具：03 §8 の形
using System;
using System.Collections.Generic;
using System.Reflection;

namespace Assets.Common.Script.Base
{
    public abstract class BaseScreenManager
    {
        protected List<BaseScreen> screenList = new List<BaseScreen>();

        protected LinkedList<BaseScreen> popupList = new LinkedList<BaseScreen>();

        protected Components components = new Components();

        public void DisplayTargetScreen(ScreenID screenId, bool isHideAllScreen = true, bool isOwnerRedirect = false) { }

        public void DisplayTargetScreen(ScreenID screenId, BaseParameter param, bool isHideAllScreen = true, bool isOwnerRedirect = false) { }

        public void DisplayTargetPopup(ScreenID screenId, bool isHideAllPopup = true) { }

        public void ShowTargetPopup(ScreenID screenId) { }

        public void HideCurrentPopup(bool isDisplayPrevious = true) { }

        public void HidePreviousPopup() { }

        public void NoneDisplayAllPopup() { }

        public BaseScreen GetCurrentScreen() => null;

        public BaseScreen GetNoneCommonScreen() => null;

        public T GetGenericScreen<T>(ScreenID screenId) where T : BaseScreen => null;

        protected abstract BaseScreen InstanceScreenInit(ScreenID screenId);
    }
}
//▲ ADD 2024.01.17 Fixture@Test 夹具
