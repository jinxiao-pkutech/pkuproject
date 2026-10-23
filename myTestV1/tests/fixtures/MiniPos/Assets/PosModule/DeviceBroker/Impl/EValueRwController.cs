//▼ ADD 2024.01.17 Fixture@Test 夹具：03 §17 の形（HTTP 送信と MQTT 回流の間に静的な呼び出し辺は無い）
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using MQTTnet.Client;
using TEC.SEJPOS.Model.Apl.KeySeq;

namespace TEC.SEJPOS.Model.DeviceBroker.Impl
{
    public class EValueRwController : IDeviceBrokerMessageEvent, IEValueRwCommand
    {
        private Action<EvRwMqttResponse> receiveEvent;

        private readonly Queue<EvRwMqttResponse> messageReceivedList = new Queue<EvRwMqttResponse>();

        public async Task<EvRwHttpResponse> Connect()
        {
            await Task.Yield();
            return new EvRwHttpResponse();
        }

        public async Task<EvRwHttpResponse> SubtractValue(string service, long seqNo, long amount, string additionalSecurityInfo)
        {
            await Task.Yield();
            return new EvRwHttpResponse();
        }

        public async Task<EvRwHttpResponse> AddValue(string service, long seqNo, long amount, string additionalSecurityInfo)
        {
            await Task.Yield();
            return new EvRwHttpResponse();
        }

        public async Task<EvRwHttpResponse> ReadValue(string service, long seqNo, string additionalSecurityInfo)
        {
            await Task.Yield();
            return new EvRwHttpResponse();
        }

        // 03 §17: the asynchronous return path. No static call edge connects it to the request.
        public void OnMessageReceived(MqttApplicationMessageReceivedEventArgs e)
        {
            EvRwMqttResponse message = Deserialize(Encoding.UTF8.GetString(e.ApplicationMessage.Payload));
            StageNo stgNo = TEC.SEJPOS.Model.ServiceLocator.GetService<IStageNoStatus>().GetStageNo();
            if (stgNo == StageNo.CSP_NML_ID_CARDCOMM_PAY)
            {
                messageReceivedList.Enqueue(message);
            }
            else
            {
                receiveEvent?.Invoke(message);
            }
        }

        public void SetReceiveEvent(Action<EvRwMqttResponse> receiveEvent) => this.receiveEvent = receiveEvent;

        // 03 §17: an interpolated string; only the constant parts can be folded.
        public string GetTopic()
            => $"shared-bus-{GetRegNo()}-service-ngp-device-{DeviceBrokerConst.EVRW_CATEGORY.ToLower()}-events";

        private string GetRegNo() => "0001";

        private static EvRwMqttResponse Deserialize(string json) => new EvRwMqttResponse();
    }
}
//▲ ADD 2024.01.17 Fixture@Test 夹具
