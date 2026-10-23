//▼ ADD 2024.01.17 Fixture@Test 夹具：03 §11.1 の形
namespace TEC.SEJPOS.Model.Apl.KeySeq
{
    public class KeySeqTableRecord
    {
        public KeySeqTableRecord(PsCode pScode, KeySeqFunction func, StageNo nextStage)
        {
            this.PsCode = pScode;
            this.Func = func;
            this.NextStage = nextStage;
        }

        public PsCode PsCode { get; }

        public KeySeqFunction Func { get; }

        public StageNo NextStage { get; }
    }
}
//▲ ADD 2024.01.17 Fixture@Test 夹具
