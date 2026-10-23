//▼ ADD 2024.01.17 Fixture@Test 夹具：03 §5 の形（partial, 目标类型 new()）
using Assets.Common.Script.Base;

namespace TEC.SEJPOS.Model.Apl.KeySeq
{
    public partial class StageNo : Enumeration
    {
        public StageNo(int id, string name) : base(id, name) { }

        public static readonly StageNo CSP_NML_NONE = new(0, nameof(CSP_NML_NONE));

        public static readonly StageNo CSP_NML_INSIDE = new(1, nameof(CSP_NML_INSIDE));

        public static readonly StageNo CSP_NML_EDY_CHARGE_SELECT = new(2, nameof(CSP_NML_EDY_CHARGE_SELECT));

        public static readonly StageNo CSP_NML_EDY_CHARGE_INPUT = new(3, nameof(CSP_NML_EDY_CHARGE_INPUT));

        public static readonly StageNo CSP_NML_EDY_PAY_CHECK = new(4, nameof(CSP_NML_EDY_PAY_CHECK));

        public static readonly StageNo CSP_NML_EDY_SHORTTEND_BZ = new(5, nameof(CSP_NML_EDY_SHORTTEND_BZ));
    }
}
//▲ ADD 2024.01.17 Fixture@Test 夹具
