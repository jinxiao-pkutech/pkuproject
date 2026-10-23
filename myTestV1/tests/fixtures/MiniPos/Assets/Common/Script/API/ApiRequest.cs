//▼ ADD 2024.01.17 Fixture@Test 夹具：03 §18 の形（apiKey は構造函数実参）
using System.Collections.Generic;

namespace Assets.Common.Script.API
{
    public class QueryInfo
    {
        public string Key { get; set; }

        public string Value { get; set; }
    }

    public class ApiResponse
    {
        public int StatusCode { get; set; }

        public string Body { get; set; }
    }

    public class ApiRequest
    {
        public ApiRequest(string method, string apiKey, List<QueryInfo> queryList, string bodyData)
        {
            this.method = method;
            this.apiKey = apiKey;
            this.queryList = queryList;
            this.bodyData = bodyData;
        }

        public string method { get; }

        public string apiKey { get; }

        public List<QueryInfo> queryList { get; }

        public string bodyData { get; }
    }
}
//▲ ADD 2024.01.17 Fixture@Test 夹具
