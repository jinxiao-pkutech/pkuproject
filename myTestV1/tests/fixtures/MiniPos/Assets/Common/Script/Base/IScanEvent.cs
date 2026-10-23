//▼ ADD 2024.01.17 Fixture@Test 夹具
using System.Threading.Tasks;

namespace Assets.Common.Script.Base
{
    public interface IScanEvent
    {
        Task OnScan(EventRequest request);
    }
}
//▲ ADD 2024.01.17 Fixture@Test 夹具
