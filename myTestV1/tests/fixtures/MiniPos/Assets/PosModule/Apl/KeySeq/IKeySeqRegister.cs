//▼ ADD 2024.01.17 Fixture@Test 夹具：03 §11.1 の形
using System.Collections.Generic;

namespace TEC.SEJPOS.Model.Apl.KeySeq
{
    public delegate ResultStatus KeySeqFunction(ref KeySeqRequest keySeq);

    public enum SubTableName
    {
        None = 0,
        Sub1 = 1,
    }

    public interface IKeySeqRegister
    {
        void Add(StageNo stage, List<KeySeqTableRecord> table);

        void AddSub(StageNo stage, List<KeySeqTableRecord> table, SubTableName subTableName);

        bool Find(StageNo nowStage, PsCode psCode, ref KeySeqFunction func);

        StageNo FindNextStage(StageNo nowStage, PsCode psCode);
    }

    public interface IStageNoStatus
    {
        StageNo GetStageNo();
    }
}
//▲ ADD 2024.01.17 Fixture@Test 夹具
