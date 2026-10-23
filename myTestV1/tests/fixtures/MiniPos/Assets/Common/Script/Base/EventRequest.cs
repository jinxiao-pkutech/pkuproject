//▼ ADD 2024.01.17 Fixture@Test 夹具
using System.Collections.Generic;
using TEC.SEJPOS.Model.Apl.KeySeq;

namespace Assets.Common.Script.Base
{
    // 03 §10.5: the event path is read out of the Request dictionary.
    public class EventRequest
    {
        public Dictionary<string, string> Request { get; set; } = new Dictionary<string, string>();

        public KeySeqRequest KeyRequest { get; set; } = new KeySeqRequest();
    }
}
//▲ ADD 2024.01.17 Fixture@Test 夹具
