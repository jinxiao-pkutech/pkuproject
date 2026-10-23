//▼ ADD 2024.01.17 Fixture@Test 夹具：03 §17（多重登記される接口）
using MQTTnet.Client;

namespace TEC.SEJPOS.Model.DeviceBroker
{
    public interface IDeviceBrokerMessageEvent
    {
        void OnMessageReceived(MqttApplicationMessageReceivedEventArgs e);

        string GetTopic();
    }
}
//▲ ADD 2024.01.17 Fixture@Test 夹具
