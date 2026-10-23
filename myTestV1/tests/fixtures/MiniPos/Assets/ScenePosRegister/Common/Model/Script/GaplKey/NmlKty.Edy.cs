//▼ ADD 2024.01.17 Fixture@Test 夹具：03 §11.1 / 05 §3.3 の形（コメントアウトされた条目は数えない）
using System.Collections.Generic;
using TEC.SEJPOS.Model.Apl.KeySeq;

namespace Assets.ScenePosRegister.Common.Model.Script
{
    public static partial class NmlKty
    {
        static partial void InitEdy(Scene_PosRegister_Model model)
        {
            List<KeySeqTableRecord> KtNml_EDY_CHARGE_SELECT = new()
            {
                new KeySeqTableRecord(PsCode.PS_CEDY_CHARGEOTHER, new KeySeqFunction(model.EdyCharge_Model.GaEntryChargeOther), StageNo.CSP_NML_EDY_CHARGE_INPUT),
                // new KeySeqTableRecord(PsCode.PS_CCLOSE, new KeySeqFunction(model.EdyCharge_Model.GaEntryOld), StageNo.CSP_NML_INSIDE),
            };
            iKeySeqReg.Add(StageNo.CSP_NML_EDY_CHARGE_SELECT, KtNml_EDY_CHARGE_SELECT);

            List<KeySeqTableRecord> KtNml_EDY_PAY_CHECK = new()
            {
                new KeySeqTableRecord(PsCode.PS_CEDY_CHARGEOTHER, new KeySeqFunction(model.EdyCharge_Model.GaEntryChargeOtherFromPay), StageNo.CSP_NML_EDY_CHARGE_INPUT),
                new KeySeqTableRecord(PsCode.PS_CSTD, new KeySeqFunction(model.EdyCharge_Model.GaEntryAmountOk), StageNo.CSP_NML_INSIDE),
            };
            iKeySeqReg.Add(StageNo.CSP_NML_EDY_PAY_CHECK, KtNml_EDY_PAY_CHECK);

            // An empty table exists too (03 §11.1).
            List<KeySeqTableRecord> KtNml_EMPTY = new() { };
            iKeySeqReg.Add(StageNo.CSP_NML_EDY_SHORTTEND_BZ, KtNml_EMPTY);
        }
    }
}
//▲ ADD 2024.01.17 Fixture@Test 夹具
