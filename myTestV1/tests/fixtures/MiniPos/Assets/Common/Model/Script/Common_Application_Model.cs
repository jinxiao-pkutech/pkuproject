//▼ ADD 2024.01.17 Fixture@Test 夹具：03 §16 の取得関数（整数形態と ResultStatusCode 形態の両方）
using TEC.SEJPOS.Model.Apl.KeySeq;

namespace Assets.Common.Model.Script
{
    public class ErrPopupType1
    {
        public int ErrCode { get; set; }

        public string Msg { get; set; }

        public string LocalizeMsg { get; set; }
    }

    public class Common_Application_Model
    {
        public ErrPopupType1 OnGetErrPopupType1(int id) => new ErrPopupType1 { ErrCode = id };

        public ErrPopupType1 OnGetErrPopupType1(ResultStatusCode code) => OnGetErrPopupType1(code.Id);
    }
}
//▲ ADD 2024.01.17 Fixture@Test 夹具
