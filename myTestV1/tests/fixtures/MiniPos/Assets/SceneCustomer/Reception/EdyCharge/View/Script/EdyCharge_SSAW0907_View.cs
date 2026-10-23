//▼ ADD 2024.01.17 Fixture@Test 夹具：03 §13.3 を逐字採用（コメントは Err2/Err22 が逆）
using System;
using Assets.Common.Script.Base;
using Assets.Common.Script.Localization.Control;
using Assets.Common.View.Dto;
using Assets.Common.View.Parts;
using UnityEngine.UIElements;

namespace Assets.SceneCustomer.Reception.EdyCharge.View.Script
{
    public interface IEdyCharge_SSAW0907_View_Event
    {
        void OnClickBtnOk(string inputValue);

        void OnClickBtnClose();
    }

    public class EdyCharge_SSAW0907_View : BaseScreen
    {
        public IEdyCharge_SSAW0907_View_Event eventHandler;

        private const int MaxValueForLabelInput = 25;

        private const int LabelInputMaxLengh = 2;

        private VisualElement velScreenMng;

        private VisualElement velSSAW0907_View;

        private LocalizeLabel title;

        private BTN18_ButtonBack btnClose;

        private BTN20_ButtonTextOnly btnOk;

        private Label inputField;

        private TenKeyControl tenkey;

        private EdyCharge_SSAW0907_DTO errMsg = new EdyCharge_SSAW0907_DTO();

        private Action clickBtnOkFunc;

        private Action closeFunc;

        private string inputValue;

        public EdyCharge_SSAW0907_View(Components components, ScreenID screenId) : base(components, screenId)
        {
            GetScreenControl();
            SetEvent();
        }

        protected override void GetScreenControl()
        {
            velScreenMng = components.UIDocumentComponent.rootVisualElement.Query<VisualElement>("VelScreenMng");
            velSSAW0907_View = velScreenMng?.Query<VisualElement>("EdyCharge_SSAW0907_View");
            title = velSSAW0907_View?.Query<LocalizeLabel>("Title");
            btnClose = velSSAW0907_View.Query<BTN18_ButtonBack>("btnClose");
            btnOk = velSSAW0907_View.Query<BTN20_ButtonTextOnly>("btnOk");
            inputField = velSSAW0907_View.Query<Label>("inputField");
            tenkey = velSSAW0907_View.Query<TenKeyControl>("tenkey");
        }

        protected override void SetEvent()
        {
            clickBtnOkFunc = () =>
            {
                inputValue = tenkey.vel_Txt.Text;
                if (string.IsNullOrWhiteSpace(inputValue))
                {
                    this.tenkey.SetInputAllowEmptyString(false, errMsg.InputPointErrMsg24);
                }
                else if (Convert.ToInt32(inputValue) == 0)
                {
                    this.tenkey.SetInputMinValue(1, errMsg.InputPointErrMsg22);
                }

                if (this.tenkey.vel_Txt.State != StateInput.Error)
                {
                    eventHandler?.OnClickBtnOk(inputValue);
                }
            };
            this.btnOk.clicked += clickBtnOkFunc;

            closeFunc = () => eventHandler?.OnClickBtnClose();
            this.btnClose.clicked += closeFunc;
        }

        private void SetKeyPadInput()
        {
            tenkey.vel_Txt.ClearErrorStatus();
            // 上限金額オーバー時の金額＆メッセージをセット　Err2をセット（数が大きすぎます）
            tenkey.SetInputMaxNumberOfCharacter(LabelInputMaxLengh, errMsg.InputPointErrMsg2);
            // 桁オーバー時の桁数＆メッセージをセット　Err22をセット（登録できない数です）
            tenkey.SetInputMaxValue(MaxValueForLabelInput, errMsg.InputPointErrMsg22);
        }

        public void CheckInputCharge()
        {
            tenkey.SetInputAllowEmptyString(false, errMsg.InputPointErrMsg24);
            if (int.TryParse(tenkey.vel_Txt.Text, out int result))
            {
                tenkey.SetInputMaxNumberOfCharacter(2, errMsg.InputPointErrMsg2);
                tenkey.SetInputMinValue(1, errMsg.InputPointErrMsg22);
            }
        }

        public void SetView(EdyCharge_SSAW0907_Element_DTO dataIni, EdyCharge_SSAW0907_DTO errMsg)
        {
            this.errMsg = errMsg;
            title.LocalizationKey = dataIni.TextTitle;
            inputField.Unit = dataIni.Unit;
            btnOk.Title = dataIni.BtnName;
            btnClose.TitleButton = dataIni.ButtonClose;
            SetKeyPadInput();
        }
    }
}
//▲ ADD 2024.01.17 Fixture@Test 夹具
