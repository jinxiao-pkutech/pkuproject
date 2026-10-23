//▼ ADD 2024.01.17 Fixture@Test 夹具
namespace TEC.SEJPOS.Model.Apl.KeySeq
{
    public class KeySeqRequest
    {
        public PsCode PsCode { get; set; }

        public PmCode PmCode { get; set; }
    }

    public class PmCode
    {
        public static readonly PmCode PM_CEDY_CHARGEKIND_5000 = new PmCode();
    }
}
//▲ ADD 2024.01.17 Fixture@Test 夹具
