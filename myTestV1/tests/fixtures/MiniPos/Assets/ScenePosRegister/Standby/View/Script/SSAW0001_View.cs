//▼ ADD 2024.01.17 Fixture@Test 夹具：03 §9（直接 lambda 形態の訂阅）
using Assets.Common.Script.Base;
using Assets.Common.View.Parts;
using UnityEngine.UIElements;

namespace Assets.ScenePosRegister.Standby.View.Script
{
    public interface ISSAW0001_View_Event
    {
        void OnClickBtnPay();
    }

    public class SSAW0001_View : BaseScreen
    {
        public ISSAW0001_View_Event eventHandler;

        private VisualElement velScreenMng;

        private BTN20_ButtonTextOnly btnPay;

        public SSAW0001_View(Components components, ScreenID screenId) : base(components, screenId)
        {
            GetScreenControl();
            SetEvent();
        }

        protected override void GetScreenControl()
        {
            velScreenMng = components.UIDocumentComponent.rootVisualElement.Query<VisualElement>("VelScreenMng");
            btnPay = velScreenMng.Query<BTN20_ButtonTextOnly>("btnPay");
        }

        // 03 §9 form 1: the lambda is subscribed directly.
        protected override void SetEvent()
        {
            btnPay.clicked += () => eventHandler?.OnClickBtnPay();
        }
    }
}
//▲ ADD 2024.01.17 Fixture@Test 夹具
