//▼ ADD 2024.01.17 Fixture@Test 夹具
using System;
using Assets.Common.Script.Base;
using Assets.Common.View.Parts;
using UnityEngine.UIElements;

namespace Assets.ScenePosRegister.Reception.EdyCharge.View.Script
{
    public interface IEdyCharge_SSAW0281_View_Event
    {
        void OnClickBtnOther(EventRequest e);

        void OnClickBtnDebug();
    }

    public class EdyCharge_SSAW0281_View : BaseScreen
    {
        public IEdyCharge_SSAW0281_View_Event eventHandler;

        private VisualElement velScreenMng;

        private BTN20_ButtonTextOnly btnOther;

        private BTN20_ButtonTextOnly btnDebug;

        private Action clickBtnOtherFunc;

        private Action clickBtnDebugFunc;

        public EdyCharge_SSAW0281_View(Components components, ScreenID screenId) : base(components, screenId)
        {
            GetScreenControl();
            SetEvent();
        }

        protected override void GetScreenControl()
        {
            velScreenMng = components.UIDocumentComponent.rootVisualElement.Query<VisualElement>("VelScreenMng");
            btnOther = velScreenMng.Query<BTN20_ButtonTextOnly>("btnOther");
            btnDebug = velScreenMng.Query<BTN20_ButtonTextOnly>("btnDebug");
        }

        protected override void SetEvent()
        {
            clickBtnOtherFunc = () => eventHandler?.OnClickBtnOther(OnClickBtnOther());
            btnOther.clicked += clickBtnOtherFunc;

            clickBtnDebugFunc = () => eventHandler?.OnClickBtnDebug();
            btnDebug.clicked += clickBtnDebugFunc;
        }

        public EventRequest OnClickBtnOther() => new EventRequest();
    }
}
//▲ ADD 2024.01.17 Fixture@Test 夹具
