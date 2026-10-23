//▼ ADD 2024.01.17 Fixture@Test 夹具：03 §18（Task.Run 包装 = 延迟実行）
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Assets.Common.Script.API
{
    public class ApiConnector : IApiConnector
    {
        public ApiResponse SendMicroserviceRequestSync(ApiRequest request, bool isTraining = false) => new ApiResponse();

        public async Task<ApiResponse> SendMicroserviceRequest(ApiRequest request, bool isTraining = false)
            => await Task.Run(() => SendMicroserviceRequestSync(request, isTraining));

        public ApiResponse SendMicroserviceRequestTemp(string method, string keyName, List<QueryInfo> queryList, string bodyData)
            => new ApiResponse();

        public async Task<ApiResponse> SendMicroserviceRequest(string method, string keyName, List<QueryInfo> queryList, string bodyData)
            => await Task.Run(() => SendMicroserviceRequestTemp(method, keyName, queryList, bodyData));
    }
}
//▲ ADD 2024.01.17 Fixture@Test 夹具
