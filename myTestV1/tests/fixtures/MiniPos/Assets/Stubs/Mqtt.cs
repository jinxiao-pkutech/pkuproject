//▼ ADD 2024.01.17 Fixture@Test 夹具：MQTTnet の最小スタブ
namespace MQTTnet
{
    public class MqttApplicationMessage
    {
        public byte[] Payload { get; set; } = new byte[0];

        public string Topic { get; set; }
    }
}

namespace MQTTnet.Client
{
    public class MqttApplicationMessageReceivedEventArgs
    {
        // 03 §17: the device response arrives as e.ApplicationMessage.Payload.
        public MQTTnet.MqttApplicationMessage ApplicationMessage { get; set; } = new MQTTnet.MqttApplicationMessage();
    }
}
//▲ ADD 2024.01.17 Fixture@Test 夹具
