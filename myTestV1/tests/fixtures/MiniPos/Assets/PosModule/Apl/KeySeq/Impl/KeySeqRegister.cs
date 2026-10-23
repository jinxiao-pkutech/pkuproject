//▼ ADD 2024.01.17 Fixture@Test 夹具
using System.Collections.Generic;

namespace TEC.SEJPOS.Model.Apl.KeySeq.Impl
{
    public class KeySeqRegister : IKeySeqRegister
    {
        private readonly Dictionary<int, List<KeySeqTableRecord>> tables = new Dictionary<int, List<KeySeqTableRecord>>();

        public void Add(StageNo stage, List<KeySeqTableRecord> table) => tables[stage.Id] = table;

        public void AddSub(StageNo stage, List<KeySeqTableRecord> table, SubTableName subTableName) => tables[stage.Id] = table;

        public bool Find(StageNo nowStage, PsCode psCode, ref KeySeqFunction func) => false;

        public StageNo FindNextStage(StageNo nowStage, PsCode psCode) => StageNo.CSP_NML_NONE;
    }

    public class StageNoStatus : IStageNoStatus
    {
        public StageNo GetStageNo() => StageNo.CSP_NML_NONE;
    }
}
//▲ ADD 2024.01.17 Fixture@Test 夹具
