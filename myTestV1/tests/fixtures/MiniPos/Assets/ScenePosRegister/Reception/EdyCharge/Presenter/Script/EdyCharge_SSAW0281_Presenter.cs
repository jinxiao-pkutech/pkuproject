//▼ ADD 2024.01.17 Fixture@Test 夹具：03 §13.2(b) の形（包装方法に事件路径を形参で渡す）
using System;
using System.Threading.Tasks;
using Assets.Common.Model.Script.KeySeq;
using Assets.Common.Model.Script.Tcp;
using Assets.Common.Model.Script.Tcp.Const;
using Assets.Common.Script.Base;
using Assets.ScenePosRegister.Common.Model.Script;
using Assets.ScenePosRegister.Common.View.Script;
using Assets.ScenePosRegister.Reception.EdyCharge.View.Script;
using TEC.SEJPOS.Model;
using TEC.SEJPOS.Model.Apl.KeySeq;
using TEC.SEJPOS.Model.DeviceBroker;

namespace Assets.ScenePosRegister.Reception.EdyCharge.Presenter.Script
{
    public class EdyCharge_SSAW0281_Presenter : BasePresenter, IEdyCharge_SSAW0281_View_Event, ISocketEvent
    {
        private readonly Scene_PosRegister_View view;

        private readonly Scene_PosRegister_Model model;

        private readonly BaseScreenManager screenManager;

        private EdyCharge_SSAW0281_View EdyCharge_SSAW0281_View;

        public EdyCharge_SSAW0281_Presenter(Scene_PosRegister_View view, Scene_PosRegister_Model model, BaseScreenManager screenManager)
        {
            this.view = view;
            this.model = model;
            this.screenManager = screenManager;
        }

        public void OnInstanced()
        {
            this.EdyCharge_SSAW0281_View = this.view.GetGenericScreen<EdyCharge_SSAW0281_View>(ScreenID.SSAW0281);
            this.EdyCharge_SSAW0281_View.eventHandler = this;
            this.EdyCharge_SSAW0281_View.SetBaseSocketEventHandler(this);
        }

        public void OnReceiveSocket(EventRequest request)
        {
            string eventpath = request.Request["EventPath"];
            if (eventpath.Equals("PosRegister.SSAW0281.Other"))
            {
                this.model.EdyCharge_Model.OperationFromCustomer = true;
                OnClickBtnOther(new EventRequest());
            }
        }

        public async void OnClickBtnOther(EventRequest e)
        {
            // The event code is not statically determinable here (E-14, E-30).
            KeySeqModel.ExeKeyProc(e.KeyRequest);

            await CustomerToOtherScreen(
                ScreenID.SSAW0907,
                "ToSSAW0907" + (this.model.EdyCharge_Model.OperationFromCustomer ? string.Empty : ".employee"),
                "x");
        }

        // 03 §10.1: the event path arrives as a parameter, so the send has to be resolved by
        // propagating the argument into the wrapper (E-16).
        private async Task<string> CustomerToOtherScreen(ScreenID screenID, string eventPath, string value)
        {
            SocketRequestData data = new SocketRequestData()
            {
                EventID = TcpConst.EVENTID_COMMON_DISPLAY_POPUP,
                TargetID = screenID.Id,
                Value = value,
            };
            return await this.model.tcpModel.SslSend(eventPath, data);
        }

        public void OnClickBtnDebug()
        {
            // ReflectionConstruction (E-25) and an ambiguous DI resolution (E-15).
            object instance = Activator.CreateInstance(Type.GetType("X"));
            IDeviceBrokerMessageEvent handler = ServiceLocator.GetService<IDeviceBrokerMessageEvent>();
            handler.GetTopic();
        }

        // Contains a goto and an unreachable call after return: neither may produce an edge (E-10).
        public void Legacy()
        {
            int i = 0;
        start:
            i++;
            if (i < 2)
            {
                goto start;
            }

            return;

#pragma warning disable CS0162
            screenManager.DisplayTargetScreen(ScreenID.SSAW0002);
#pragma warning restore CS0162
        }
    }
}
//▲ ADD 2024.01.17 Fixture@Test 夹具
