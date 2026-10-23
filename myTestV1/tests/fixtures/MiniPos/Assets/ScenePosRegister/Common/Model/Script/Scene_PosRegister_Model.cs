//▼ ADD 2024.01.17 Fixture@Test 夹具
using Assets.Common.Model.Script;
using Assets.Common.Model.Script.Tcp;
using Assets.Common.Script.Queueing;

namespace Assets.ScenePosRegister.Common.Model.Script
{
    public class Scene_PosRegister_Model
    {
        public TcpModel tcpModel { get; set; } = new TcpModel();

        public ModelQueueing ModelQueueing { get; set; } = new ModelQueueing();

        public EdyCharge_Model EdyCharge_Model { get; set; } = new EdyCharge_Model();

        public Payment_PosRegister_Edy_Model Payment_PosRegister_Edy_Model { get; set; } = new Payment_PosRegister_Edy_Model();

        public Common_Application_Model Common_Model { get; set; } = new Common_Application_Model();

        public CustomerDetectionModel CustomerDetectionModel { get; set; } = new CustomerDetectionModel();
    }
}
//▲ ADD 2024.01.17 Fixture@Test 夹具
