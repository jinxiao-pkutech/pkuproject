//▼ ADD 2024.01.17 Fixture@Test 夹具
using Assets.Common.Model.Script;
using Assets.Common.Model.Script.Tcp;
using Assets.Common.Script.Queueing;
using Assets.Common.View.Dto;
using Assets.ScenePosRegister.Common.Model.Script;

namespace Assets.SceneCustomer.Common.Model.Script
{
    public class EdyCharge_Customer_Model
    {
        public EdyCharge_SSAW0907_DTO edyCharge_SSAW0907_DTO { get; set; } = new EdyCharge_SSAW0907_DTO();

        public EdyCharge_SSAW0903_Element_DTO OnGetDataIni_SSAW0903() => new EdyCharge_SSAW0903_Element_DTO();

        public EdyCharge_SSAW0907_Element_DTO OnGetDataIni_SSAW0907() => new EdyCharge_SSAW0907_Element_DTO();
    }

    public class Scene_Customer_Model
    {
        public TcpModel tcpModel { get; set; } = new TcpModel();

        public ModelQueueing ModelQueueing { get; set; } = new ModelQueueing();

        public EdyCharge_Customer_Model edyCharge_Customer_Model { get; set; } = new EdyCharge_Customer_Model();

        public Common_Application_Model Common_Model { get; set; } = new Common_Application_Model();

        public CustomerDetectionModel CustomerDetectionModel { get; set; } = new CustomerDetectionModel();
    }
}
//▲ ADD 2024.01.17 Fixture@Test 夹具
