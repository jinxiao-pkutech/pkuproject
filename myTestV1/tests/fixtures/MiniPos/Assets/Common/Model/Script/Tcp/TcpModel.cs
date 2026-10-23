//▼ ADD 2024.01.17 Fixture@Test 夹具：03 §10.1 の署名（UniTask は Task に置換）
using System.Collections.Generic;
using System.Threading.Tasks;
using Assets.Common.Model.Script.Tcp.Const;

namespace Assets.Common.Model.Script.Tcp
{
    public class TcpModel
    {
        public async Task<string> SslSend(string eventPath, SocketRequestData socketRequestData = null,
                                          List<string> sendFileList = null, int retryCount = TcpConst.RETRY_COUNT_DEFAULT)
        {
            await Task.Yield();
            return eventPath;
        }

        public async Task<string> SslContentsSend(string eventPath, SocketRequestData socketRequestData = null,
                                                  List<string> sendFileList = null, int retryCount = TcpConst.RETRY_COUNT_DEFAULT)
        {
            await Task.Yield();
            return eventPath;
        }
    }
}
//▲ ADD 2024.01.17 Fixture@Test 夹具
