//▼ ADD 2024.01.17 Fixture@Test 夹具：03 §17 の形
using System.Threading.Tasks;

namespace TEC.SEJPOS.Model.DeviceBroker
{
    public class EvRwHttpResponse
    {
        public string Status { get; set; }
    }

    public class EvRwMqttResponse
    {
        public string Status { get; set; }
    }

    public interface IEValueRwCommand
    {
        Task<EvRwHttpResponse> Connect();

        Task<EvRwHttpResponse> SubtractValue(string service, long seqNo, long amount, string additionalSecurityInfo);

        Task<EvRwHttpResponse> AddValue(string service, long seqNo, long amount, string additionalSecurityInfo);

        Task<EvRwHttpResponse> ReadValue(string service, long seqNo, string additionalSecurityInfo);
    }

    public static class DeviceBrokerConst
    {
        public const string EVRW_CATEGORY = "EVRW";
    }
}
//▲ ADD 2024.01.17 Fixture@Test 夹具
