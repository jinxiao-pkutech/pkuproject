//▼ ADD 2024.01.17 Fixture@Test 夹具：03 §5 の形
using System;
using Assets.Common.Script.Base;

namespace TEC.SEJPOS.Model.Apl.KeySeq
{
    [Serializable]
    public partial class PsCode : Enumeration
    {
        public PsCode(int id, string name) : base(id, name) { }

        public static readonly PsCode KEYTABLE_END = new(-1, nameof(KEYTABLE_END));

        public static readonly PsCode PS_CCOD1 = new(1, nameof(PS_CCOD1));

        public static readonly PsCode PS_CCLOSE = new(2, nameof(PS_CCLOSE));

        public static readonly PsCode PS_CSTD = new(3, nameof(PS_CSTD));

        public static readonly PsCode PS_CEDY_CHARGEOTHER = new(4, nameof(PS_CEDY_CHARGEOTHER));
    }
}
//▲ ADD 2024.01.17 Fixture@Test 夹具
