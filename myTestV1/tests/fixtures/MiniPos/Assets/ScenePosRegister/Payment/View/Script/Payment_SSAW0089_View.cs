//▼ ADD 2024.01.17 Fixture@Test 夹具：03 §9（委托字段形態の訂阅）
using System;
using Assets.Common.Script.Base;
using Assets.Common.View.Parts;
using UnityEngine.UIElements;

namespace Assets.ScenePosRegister.Payment.View.Script
{
    public interface IPayment_SSAW0089_View_Event
    {
        void OnClickBtnEdy();

        void SendUnknown();
    }

    public class Payment_SSAW0089_View : BaseScreen
    {
        public IPayment_SSAW0089_View_Event eventHandler;

        private VisualElement velScreenMng;

        private BTN20_ButtonTextOnly btnEdy;

        private BTN20_ButtonTextOnly btnTest;

        private Action clickBtnEdyFunc;

        private Action clickBtnTestFunc;

        public Payment_SSAW0089_View(Components components, ScreenID screenId) : base(components, screenId)
        {
            GetScreenControl();
            SetEvent();
        }

        protected override void GetScreenControl()
        {
            velScreenMng = components.UIDocumentComponent.rootVisualElement.Query<VisualElement>("VelScreenMng");
            btnEdy = velScreenMng.Query<BTN20_ButtonTextOnly>("btnEdy");
            btnTest = velScreenMng.Query<BTN20_ButtonTextOnly>("btnTest");
        }

        // 03 §9 form 2: the lambda goes into a delegate field and the field is subscribed. The
        // analysis has to see through the field.
        protected override void SetEvent()
        {
            clickBtnEdyFunc = () => eventHandler?.OnClickBtnEdy();
            btnEdy.clicked += clickBtnEdyFunc;

            clickBtnTestFunc = () => eventHandler?.SendUnknown();
            btnTest.clicked += clickBtnTestFunc;
        }

        public override void ResetEvent()
        {
            this.btnEdy.clicked -= clickBtnEdyFunc;
            this.btnTest.clicked -= clickBtnTestFunc;
        }
    }
}
//▲ ADD 2024.01.17 Fixture@Test 夹具
