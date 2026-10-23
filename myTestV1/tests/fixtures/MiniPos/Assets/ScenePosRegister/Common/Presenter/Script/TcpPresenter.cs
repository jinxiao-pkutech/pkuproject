//▼ ADD 2024.01.17 Fixture@Test 夹具：03 §10.4 の構造（已核实部分のみ）
using Assets.Common.Model.Script.Tcp;
using Assets.Common.Script.Base;
using Assets.Common.Script.Queueing;
using Assets.ScenePosRegister.Common.Model.Script;

namespace Assets.ScenePosRegister.Common.Presenter.Script
{
    public class TcpPresenter : BasePresenter
    {
        private readonly Scene_PosRegister_Model model;

        private readonly BaseScreenManager screenManager;

        public TcpPresenter(Scene_PosRegister_Model model, BaseScreenManager screenManager)
        {
            this.model = model;
            this.screenManager = screenManager;
        }

        public void OnReceive(SocketRequestData ReceiveRequestData, EventRequest eventRequest)
        {
            model.ModelQueueing.AddQueue(ElementLevel.Emergency, new ElementRequest(() =>
            {
                BaseScreen screen = screenManager.GetNoneCommonScreen();
                screen.OnReceiveSocket(eventRequest);
            }, true));
        }
    }
}
//▲ ADD 2024.01.17 Fixture@Test 夹具
