//▼ ADD 2024.01.17 Fixture@Test 夹具：03 §14.1（JSON のキー名 = DTO のプロパティ名）
using System.Collections.Generic;

namespace Assets.Common.View.Dto
{
    public class EdyCharge_SSAW0903_Element_DTO
    {
        public string TextTitle { get; set; }

        public List<string> ButtonTitle { get; set; } = new List<string>();

        public string ButtonClose { get; set; }
    }

    public class EdyCharge_SSAW0907_Element_DTO
    {
        public string TextTitle { get; set; }

        public string Unit { get; set; }

        public string BtnName { get; set; }

        public string ButtonClose { get; set; }
    }

    public class SSAW0001_Element_DTO
    {
        public string TextTitle { get; set; }

        public string ButtonPay { get; set; }
    }

    // 03 §13.3: the Presenter fills these from OnGetErrPopupType1(...).LocalizeMsg.
    public class EdyCharge_SSAW0907_DTO
    {
        public string InputPointErrMsg2 { get; set; }

        public string InputPointErrMsg22 { get; set; }

        public string InputPointErrMsg24 { get; set; }
    }
}
//▲ ADD 2024.01.17 Fixture@Test 夹具
