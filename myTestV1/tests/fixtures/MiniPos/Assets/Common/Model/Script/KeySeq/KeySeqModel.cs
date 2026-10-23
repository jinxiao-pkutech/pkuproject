//▼ ADD 2024.01.17 Fixture@Test 夹具：03 §11.2 の入口
using TEC.SEJPOS.Model.Apl.KeySeq;
using TEC.SEJPOS.Model.Apl.KeySeq.Impl;

namespace Assets.Common.Model.Script.KeySeq
{
    public static class KeySeqModel
    {
        private static readonly KeySeqEventDispatcher dispatcher = new KeySeqEventDispatcher();

        public static ResultStatus ExeKeyProc(KeySeqRequest keySeq)
        {
            KeySeqRequest request = keySeq;
            return dispatcher.GaExecuteKey(ref request);
        }
    }
}
//▲ ADD 2024.01.17 Fixture@Test 夹具
