//▼ ADD 2024.01.17 Fixture@Test 夹具：03 §13.3 の形
using Assets.Common.Model.Script.Tcp;
using Assets.Common.Model.Script.Tcp.Const;
using Assets.Common.Script.Base;
using Assets.Common.View.Dto;
using Assets.SceneCustomer.Common.Model.Script;
using Assets.SceneCustomer.Common.View.Script;
using Assets.SceneCustomer.Reception.EdyCharge.View.Script;
using TEC.SEJPOS.Model.Apl.KeySeq;

namespace Assets.SceneCustomer.Reception.EdyCharge.Presenter.Script
{
    public class EdyCharge_SSAW0907_Presenter : BasePresenter, IEdyCharge_SSAW0907_View_Event, ISocketEvent
    {
        private readonly Scene_Customer_View view;

        private readonly Scene_Customer_Model model;

        private readonly BaseScreenManager screenManager;

        private EdyCharge_SSAW0907_View EdyCharge_SSAW0907_View;

        private EdyCharge_SSAW0907_Element_DTO dataIni;

        public EdyCharge_SSAW0907_Presenter(Scene_Customer_View view, Scene_Customer_Model model, BaseScreenManager screenManager)
        {
            this.view = view;
            this.model = model;
            this.screenManager = screenManager;
        }

        public void OnInstanced()
        {
            this.EdyCharge_SSAW0907_View = this.view.GetGenericScreen<EdyCharge_SSAW0907_View>(ScreenID.SSAW0907);
            this.EdyCharge_SSAW0907_View.eventHandler = this;
            this.EdyCharge_SSAW0907_View.SetBaseSocketEventHandler(this);
            this.dataIni = model.edyCharge_Customer_Model.OnGetDataIni_SSAW0907();
        }

        // 03 §10.5: Contains matches both ToSSAW0907 and ToSSAW0907.employee; Equals matches the
        // other sender's event path.
        public void OnReceiveSocket(EventRequest request)
        {
            string eventpath = request.Request["EventPath"];
            if (eventpath.Contains("ToSSAW0907"))
            {
                string[] tokens = eventpath.Split(".");
                if (tokens.Length > 1 && tokens[1].Contains("employee"))
                {
                    SetErrorMessages();
                }

                SetErrorMessages();
                OnDisplay();
            }
            else if (eventpath.Equals("SSAW0903.To.SSAW0907"))
            {
                SetErrorMessages();
                OnDisplay();
            }
        }

        // 03 §13.3: the error texts come from the error code through OnGetErrPopupType1.
        private void SetErrorMessages()
        {
            this.model.edyCharge_Customer_Model.edyCharge_SSAW0907_DTO.InputPointErrMsg2
                = this.model.Common_Model.OnGetErrPopupType1(ResultStatusCode.ERRB002).LocalizeMsg;
            this.model.edyCharge_Customer_Model.edyCharge_SSAW0907_DTO.InputPointErrMsg22
                = this.model.Common_Model.OnGetErrPopupType1(ResultStatusCode.ERRB022).LocalizeMsg;
            this.model.edyCharge_Customer_Model.edyCharge_SSAW0907_DTO.InputPointErrMsg24
                = this.model.Common_Model.OnGetErrPopupType1(ResultStatusCode.ERRB024).LocalizeMsg;
        }

        public void OnDisplay()
        {
            this.EdyCharge_SSAW0907_View.SetView(this.dataIni, this.model.edyCharge_Customer_Model.edyCharge_SSAW0907_DTO);
        }

        public async void OnClickBtnOk(string inputValue)
        {
            SocketRequestData data = new SocketRequestData()
            {
                EventID = TcpConst.EVENTID_COMMON_DISPLAY_POPUP,
                TargetID = ScreenID.SSAW1113.Id,
                Value = inputValue,
            };
            await this.model.tcpModel.SslSend("PosRegister.SSAW1113.OK", data);
        }

        public async void OnClickBtnClose()
        {
            await this.model.tcpModel.SslSend("PosRegister.SSAW0907.Close");
        }
    }
}
//▲ ADD 2024.01.17 Fixture@Test 夹具
