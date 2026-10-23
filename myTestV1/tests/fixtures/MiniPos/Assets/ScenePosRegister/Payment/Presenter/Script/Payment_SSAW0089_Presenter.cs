//▼ ADD 2024.01.17 Fixture@Test 夹具：03 §13.2(a) の形
using System.Threading.Tasks;
using Assets.Common.Model.Script.KeySeq;
using Assets.Common.Model.Script.Tcp;
using Assets.Common.Model.Script.Tcp.Const;
using Assets.Common.Script.API;
using Assets.Common.Script.API.Const;
using Assets.Common.Script.Base;
using Assets.Common.Script.Queueing;
using Assets.ScenePosRegister.Common.Model.Script;
using Assets.ScenePosRegister.Common.View.Script;
using Assets.ScenePosRegister.Payment.View.Script;
using TEC.SEJPOS.Model;
using TEC.SEJPOS.Model.Apl.KeySeq;
using TEC.SEJPOS.Model.DeviceBroker;
using TEC.SEJPOS.Model.DeviceBroker.Impl;

namespace Assets.ScenePosRegister.Payment.Presenter.Script
{
    public class Payment_SSAW0089_Presenter : BasePresenter, IPayment_SSAW0089_View_Event, ISocketEvent
    {
        private readonly Scene_PosRegister_View view;

        private readonly Scene_PosRegister_Model model;

        private readonly BaseScreenManager screenManager;

        private Payment_SSAW0089_View Payment_SSAW0089_View;

        public Payment_SSAW0089_Presenter(Scene_PosRegister_View view, Scene_PosRegister_Model model, BaseScreenManager screenManager)
        {
            this.view = view;
            this.model = model;
            this.screenManager = screenManager;
        }

        public void OnInstanced()
        {
            this.Payment_SSAW0089_View = this.view.GetGenericScreen<Payment_SSAW0089_View>(ScreenID.SSAW0089);
            this.Payment_SSAW0089_View.eventHandler = this;
            this.Payment_SSAW0089_View.SetBaseSocketEventHandler(this);

            // The MQTT return path of the hardware boundary (E-24).
            ServiceLocator.GetService<EValueRwController>().SetReceiveEvent(OnEdyResponse);
        }

        public async void OnClickBtnEdy()
        {
            // ExternalHardware boundary (E-24).
            EvRwHttpResponse res = await ServiceLocator.GetService<IEValueRwCommand>().ReadValue("Edy", 1, "");

            // SocketRequestData data = new SocketRequestData() { TargetID = ScreenID.SSAW0907.Id, EventID = TcpConst.EVENTID_COMMON_DISPLAY_POPUP };   // commented out: must not appear (E-17)

            SocketRequestData data = new SocketRequestData()
            {
                TargetID = ScreenID.SSAW0903.Id,
                Value = "500",
                EventID = TcpConst.EVENTID_COMMON_DISPLAY_POPUP,
            };
            await this.model.tcpModel.SslSend("Customer.NextDispReq.SSAW0903", data);
        }

        public void OnEdyResponse(EvRwMqttResponse m)
        {
            screenManager.DisplayTargetScreen(ScreenID.SSAW0001);
        }

        // 03 §10.5: the event path is a local read out of the request dictionary.
        public void OnReceiveSocket(EventRequest request)
        {
            string eventPath = request.Request["EventPath"];
            if (eventPath.Equals("PosRegister.SSAW0281.Other"))
            {
                OnClickBtnOther();
            }
            else if (eventPath.Equals("PosRegister.SSAW1113.OK"))
            {
                OnClickBtnAmountOK();
            }
        }

        // 03 §13.2(a).
        public void OnClickBtnOther()
        {
            KeySeqRequest request = new KeySeqRequest() { PsCode = PsCode.PS_CEDY_CHARGEOTHER };
            ResultStatus res = KeySeqModel.ExeKeyProc(request);
            if (res.ResultCode.Id == ResultStatusCode.GA_NO_ERROR.Id)
            {
                this.model.ModelQueueing.AddQueue(ElementLevel.Normal, new ElementRequest(async () =>
                {
                    SocketRequestData data = new SocketRequestData()
                    {
                        EventID = TcpConst.EVENTID_COMMON_DISPLAY_POPUP,
                        TargetID = ScreenID.SSAW0907.Id,
                        Value = "0",
                    };
                    await this.model.tcpModel.SslSend("SSAW0903.To.SSAW0907", data);
                }), true);
            }
        }

        public async void OnClickBtnAmountOK()
        {
            KeySeqModel.ExeKeyProc(new KeySeqRequest() { PsCode = PsCode.PS_CSTD });

            IApiConnector connector = ServiceLocator.GetService<IApiConnector>();

            // ExternalMicroservice boundary; the api key is the second constructor argument (E-25).
            await connector.SendMicroserviceRequest(new ApiRequest("POST", ApiConst.EDY_CHARGE, null, "{}"));

            screenManager.DisplayTargetScreen(ScreenID.SSAW0001);
        }

        // No receiver anywhere in the fixture: a dangling edge (E-29).
        public async void SendUnknown()
        {
            await this.model.tcpModel.SslSend("PosRegister.Nobody.Handles");
        }
    }
}
//▲ ADD 2024.01.17 Fixture@Test 夹具
