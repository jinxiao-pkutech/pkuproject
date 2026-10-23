//▼ ADD 2024.01.17 Fixture@Test 夹具：03 §10.3 の構造
using System;
using Assets.Common.Model.Script.Tcp;
using Assets.Common.Model.Script.Tcp.Const;
using Assets.Common.Script.Base;
using Assets.Common.Script.Queueing;
using Assets.SceneCustomer.Common.Model.Script;
using Assets.SceneCustomer.Common.View.Script;

namespace Assets.SceneCustomer.Common.Presenter.Script.Tcp
{
    public class TcpPresenter : BasePresenter
    {
        private readonly Scene_Customer_Model model;

        private readonly BaseScreenManager screenManagerCL;

        public TcpPresenter(Scene_Customer_Model model, BaseScreenManager screenManagerCL)
        {
            this.model = model;
            this.screenManagerCL = screenManagerCL;
        }

        // 03 §10.3: everything received is executed through the model queue at emergency level.
        public void OnReceive(SocketRequestData ReceiveRequestData, EventRequest eventRequest)
        {
            model.ModelQueueing.AddQueue(ElementLevel.Emergency, new ElementRequest(() =>
            {
                if (IsCommonEvent(ReceiveRequestData))
                {
                    CommonEventHandler(ReceiveRequestData, eventRequest);
                }
                else
                {
                    BaseScreen screen = screenManagerCL.GetNoneCommonScreen();
                    screen.OnReceiveSocket(eventRequest);
                }
            }, true));
        }

        private bool IsCommonEvent(SocketRequestData ReceiveRequestData)
        {
            string[] evts = (ReceiveRequestData.EventID ?? "").Split(".");
            return evts[0].Equals(TcpConst.EVENTID_COMMON);
        }

        private void CommonEventHandler(SocketRequestData ReceiveRequestData, EventRequest eventRequest)
        {
            if (ReceiveRequestData.EventID == TcpConst.EVENTID_COMMON_DISPLAY)
            {
                ScreenID targetScreenID = (ScreenID)typeof(ScreenID).GetField(ReceiveRequestData.TargetID).GetValue(null);
                screenManagerCL.DisplayTargetScreen(targetScreenID);
                BaseScreen current = screenManagerCL.GetNoneCommonScreen();
                current.OnReceiveSocket(eventRequest);
                return;
            }

            PopupEventHandler(ReceiveRequestData, eventRequest);
        }

        // 03 §10.3: the display instruction names the target screen, and the same event is then
        // handed to whichever screen is now current.
        private void PopupEventHandler(SocketRequestData ReceiveRequestData, EventRequest eventRequest)
        {
            string eventId = ReceiveRequestData.EventID;
            string targetID = ReceiveRequestData.TargetID ?? string.Empty;
            switch (eventId)
            {
                case TcpConst.EVENTID_COMMON_DISPLAY_POPUP:
                {
                    ScreenID screenID = (ScreenID)typeof(ScreenID).GetField(targetID).GetValue(null);
                    screenManagerCL.DisplayTargetPopup(screenID);
                    break;
                }

                case TcpConst.EVENTID_COMMON_DISPLAY_POPUP_ABOVE:
                {
                    ScreenID screenID = (ScreenID)typeof(ScreenID).GetField(targetID).GetValue(null);
                    screenManagerCL.ShowTargetPopup(screenID);
                    break;
                }

                case TcpConst.EVENTID_COMMON_DISPLAY_POPUP_FIRST_CLOSE_AFTER:
                {
                    ScreenID screenID = (ScreenID)typeof(ScreenID).GetField(targetID).GetValue(null);
                    screenManagerCL.DisplayTargetPopup(screenID, false);
                    break;
                }

                case TcpConst.EVENTID_COMMON_CLOSE_POPUP:
                    screenManagerCL.HideCurrentPopup(true);
                    break;

                case TcpConst.EVENTID_COMMON_CLOSE_POPUP_PREVIOUS:
                    screenManagerCL.HidePreviousPopup();
                    break;
            }

            BaseScreen screen = screenManagerCL.GetNoneCommonScreen();
            screen.OnReceiveSocket(eventRequest);
        }
    }
}
//▲ ADD 2024.01.17 Fixture@Test 夹具
