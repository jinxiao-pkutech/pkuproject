//▼ ADD 2024.01.17 Fixture@Test 夹具
using System.Threading.Tasks;

namespace TEC.SEJPOS.Model.DeviceBroker
{
    public class ScannerHttpResponse
    {
        public string Code { get; set; }
    }

    public interface IScannerCommand
    {
        Task<ScannerHttpResponse> StartScan();
    }
}
//▲ ADD 2024.01.17 Fixture@Test 夹具
