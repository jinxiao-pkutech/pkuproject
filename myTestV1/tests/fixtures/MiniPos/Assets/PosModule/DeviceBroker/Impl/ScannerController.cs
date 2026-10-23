//▼ ADD 2024.01.17 Fixture@Test 夹具
using System.Threading.Tasks;
using MQTTnet.Client;

namespace TEC.SEJPOS.Model.DeviceBroker.Impl
{
    public class ScannerController : IDeviceBrokerMessageEvent, IScannerCommand
    {
        public async Task<ScannerHttpResponse> StartScan()
        {
            await Task.Yield();
            return new ScannerHttpResponse();
        }

        public void OnMessageReceived(MqttApplicationMessageReceivedEventArgs e) { }

        public string GetTopic() => "shared-bus-0001-service-ngp-device-scanner-events";
    }
}
//▲ ADD 2024.01.17 Fixture@Test 夹具
