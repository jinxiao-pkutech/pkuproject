//▼ ADD 2024.01.17 Fixture@Test 夹具：03 §7 / §13.2[1] の形
using Assets.Common.Script.Base;
using Assets.Common.Script.Queueing;
using Assets.Common.View.Dto;
using Assets.SceneCustomer.Common.Model.Script;
using Assets.SceneCustomer.Common.View.Script;
using Assets.SceneCustomer.Reception.EdyCharge.View.Script;

namespace Assets.SceneCustomer.Reception.EdyCharge.Presenter.Script
{
    public class EdyCharge_SSAW0903_Presenter : BasePresenter, IEdyCharge_SSAW0903_View_Event, ISocketEvent
    {
        private readonly Scene_Customer_View view;

        private readonly Scene_Customer_Model model;

        private readonly BaseScreenManager screenManager;

        private EdyCharge_SSAW0903_View EdyCharge_SSAW0903_View;

        private EdyCharge_SSAW0903_Element_DTO dataIni;

        public EdyCharge_SSAW0903_Presenter(Scene_Customer_View view, Scene_Customer_Model model, BaseScreenManager screenManager)
        {
            this.view = view;
            this.model = model;
            this.screenManager = screenManager;
        }

        public void OnInstanced()
        {
            this.EdyCharge_SSAW0903_View = this.view.GetGenericScreen<EdyCharge_SSAW0903_View>(ScreenID.SSAW0903);
            this.EdyCharge_SSAW0903_View.eventHandler = this;
            this.EdyCharge_SSAW0903_View.SetBaseSocketEventHandler(this);
            this.dataIni = model.edyCharge_Customer_Model.OnGetDataIni_SSAW0903();
        }

        public void OnDisplay()
        {
            this.EdyCharge_SSAW0903_View.SetView(this.dataIni);
        }

        // 03 §13.2[1]: the send runs inside a queued lambda, so it is deferred.
        public void OnClickBtnOther()
        {
            this.model.ModelQueueing.AddQueue(ElementLevel.Normal, new ElementRequest(async () =>
            {
                this.model.CustomerDetectionModel.MC_Estimate(1);
                await this.model.tcpModel.SslSend("PosRegister.SSAW0281.Other");
            }), true);
        }

        // No employee-side receiver in the fixture: these become dangling edges (E-29 family).
        public async void OnClickBtn1000()
        {
            await this.model.tcpModel.SslSend("PosRegister.SSAW0281.1000Yen");
        }

        public async void OnClickBtn2000()
        {
            await this.model.tcpModel.SslSend("PosRegister.SSAW0281.2000Yen");
        }

        public async void OnClickBtnClose()
        {
            await this.model.tcpModel.SslSend("PosRegister.SSAW0281.Close");
        }

        public void OnReceiveSocket(EventRequest request)
        {
            string eventpath = request.Request["EventPath"];
            if (eventpath.Equals("Customer.NextDispReq.SSAW0903"))
            {
                OnDisplay();
            }
        }
    }
}
//▲ ADD 2024.01.17 Fixture@Test 夹具
