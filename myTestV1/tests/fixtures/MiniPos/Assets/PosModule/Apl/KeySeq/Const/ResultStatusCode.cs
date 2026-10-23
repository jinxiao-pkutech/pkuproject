//▼ ADD 2024.01.17 Fixture@Test 夹具：03 §5 / §13.3 の形
using Assets.Common.Script.Base;

namespace TEC.SEJPOS.Model.Apl.KeySeq
{
    public partial class ResultStatusCode : Enumeration
    {
        public ResultStatusCode(int id, string name) : base(id, name) { }

        public static readonly ResultStatusCode SUCCESS = new(0, nameof(SUCCESS));

        public static readonly ResultStatusCode GA_NO_ERROR = new(0, nameof(GA_NO_ERROR));

        public static readonly ResultStatusCode ERRB001 = new(1, nameof(ERRB001));

        public static readonly ResultStatusCode ERRB002 = new(2, nameof(ERRB002));

        public static readonly ResultStatusCode ERRB022 = new(22, nameof(ERRB022));

        public static readonly ResultStatusCode ERRB024 = new(24, nameof(ERRB024));
    }

    public class ResultStatus
    {
        public ResultStatusCode ResultCode { get; set; } = ResultStatusCode.SUCCESS;
    }
}
//▲ ADD 2024.01.17 Fixture@Test 夹具
