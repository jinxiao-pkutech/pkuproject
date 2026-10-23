//▼ ADD 2024.01.17 Fixture@Test 夹具：03 §11.2 の分派器（委托调用；遍历不得进入其内部）
namespace TEC.SEJPOS.Model.Apl.KeySeq.Impl
{
    public class KeySeqEventDispatcher
    {
        private readonly IKeySeqRegister register = new KeySeqRegister();

        private StageNo nowStage = StageNo.CSP_NML_NONE;

        // 03 §11.2: the next stage is set BEFORE the handler runs and restored when it fails.
        public ResultStatus GaExecuteKey(ref KeySeqRequest keySeq)
        {
            KeySeqFunction func = null;
            if (!register.Find(nowStage, keySeq.PsCode, ref func))
            {
                return new ResultStatus { ResultCode = ResultStatusCode.ERRB001 };
            }

            StageNo previous = nowStage;
            nowStage = register.FindNextStage(nowStage, keySeq.PsCode);

            ResultStatus result = func(ref keySeq);
            if (result.ResultCode.Id != ResultStatusCode.SUCCESS.Id)
            {
                nowStage = previous;
            }

            return result;
        }
    }
}
//▲ ADD 2024.01.17 Fixture@Test 夹具
