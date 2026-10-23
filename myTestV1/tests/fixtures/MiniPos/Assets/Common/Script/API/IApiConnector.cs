//▼ ADD 2024.01.17 Fixture@Test 夹具：03 §18 の署名
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Assets.Common.Script.API
{
    public interface IApiConnector
    {
        ApiResponse SendMicroserviceRequestSync(ApiRequest request, bool isTraining = false);

        Task<ApiResponse> SendMicroserviceRequest(ApiRequest request, bool isTraining = false);

        ApiResponse SendMicroserviceRequestTemp(string method, string keyName, List<QueryInfo> queryList, string bodyData);

        Task<ApiResponse> SendMicroserviceRequest(string method, string keyName, List<QueryInfo> queryList, string bodyData);
    }
}
//▲ ADD 2024.01.17 Fixture@Test 夹具
