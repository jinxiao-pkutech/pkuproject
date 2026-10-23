//▼ ADD 2024.01.17 Fixture@Test 夹具：03 §9 / §14.2 の控件型
using UnityEngine.UIElements;

namespace Assets.Common.Script.Localization.Control
{
    public class LocalizeLabel : Label
    {
    }

    public class LocalizeImage : VisualElement
    {
    }
}

namespace Assets.Common.View.Parts
{
    public class BTN18_ButtonBack : Button
    {
    }

    public class BTN20_ButtonTextOnly : Button
    {
    }

    public class MB04_IconDigitText : Label
    {
    }

    public enum StateInput
    {
        Normal = 0,
        Error = 1,
    }

    public class TextInputControl : VisualElement
    {
        public string Text { get; set; }

        public StateInput State { get; set; }

        public void ClearErrorStatus() { }
    }

    // 03 §13.3: the four input constraint setters. The value comes first and the message second.
    public class TenKeyControl : VisualElement
    {
        public TextInputControl vel_Txt { get; set; } = new TextInputControl();

        public void SetInputMaxNumberOfCharacter(int maxLength, string message) { }

        public void SetInputMaxValue(int maxValue, string message) { }

        public void SetInputMinValue(int minValue, string message) { }

        public void SetInputAllowEmptyString(bool allowEmpty, string message) { }
    }
}
//▲ ADD 2024.01.17 Fixture@Test 夹具
