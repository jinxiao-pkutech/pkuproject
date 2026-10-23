//▼ ADD 2024.01.17 Fixture@Test 夹具：03 §11.1 の形
using TEC.SEJPOS.Model;
using TEC.SEJPOS.Model.Apl.KeySeq;

namespace Assets.ScenePosRegister.Common.Model.Script
{
    public static partial class NmlKty
    {
        private static IKeySeqRegister iKeySeqReg = ServiceLocator.GetService<IKeySeqRegister>();

        static partial void InitEdy(Scene_PosRegister_Model model);

        public static void Init(Scene_PosRegister_Model model) => InitEdy(model);
    }
}
//▲ ADD 2024.01.17 Fixture@Test 夹具
