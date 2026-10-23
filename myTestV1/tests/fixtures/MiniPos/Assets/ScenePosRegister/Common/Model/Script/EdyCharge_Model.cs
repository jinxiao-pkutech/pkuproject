//▼ ADD 2024.01.17 Fixture@Test 夹具：键序处理函数（03 §11.1 の方法组）
using TEC.SEJPOS.Model;
using TEC.SEJPOS.Model.Apl.KeySeq;
using TEC.SEJPOS.Model.DeviceBroker;

namespace Assets.ScenePosRegister.Common.Model.Script
{
    public class EdyCharge_Model
    {
        public bool OperationFromCustomer { get; set; }

        public ResultStatus GaEntryChargeOther(ref KeySeqRequest keySeq)
        {
            // Reaches the IC reader/writer: an ExternalHardware boundary (E-24).
            ServiceLocator.GetService<IEValueRwCommand>().AddValue("Edy", 1, 1000, "");
            return new ResultStatus();
        }

        public ResultStatus GaEntryChargeOtherFromPay(ref KeySeqRequest keySeq) => new ResultStatus();

        public ResultStatus GaEntryAmountOk(ref KeySeqRequest keySeq) => new ResultStatus();

        public ResultStatus GaEntryOld(ref KeySeqRequest keySeq) => new ResultStatus();
    }

    public class Payment_PosRegister_Edy_Model
    {
        public ResultStatus GaEntryEdyPayYesNo(ref KeySeqRequest keySeq) => new ResultStatus();

        public ResultStatus GaEntryEdyChargeFromBalanceShort(ref KeySeqRequest keySeq) => new ResultStatus();
    }

    public class CustomerDetectionModel
    {
        public void MC_Estimate(int mode) { }
    }
}
//▲ ADD 2024.01.17 Fixture@Test 夹具
