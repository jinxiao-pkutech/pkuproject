//▼ ADD 2024.01.17 Fixture@Test 夹具：03 §9 / §14.2 の形
using System;
using System.Collections.Generic;
using Assets.Common.Script.Base;
using Assets.Common.Script.Localization.Control;
using Assets.Common.View.Dto;
using Assets.Common.View.Parts;
using UnityEngine.UIElements;

namespace Assets.SceneCustomer.Reception.EdyCharge.View.Script
{
    public interface IEdyCharge_SSAW0903_View_Event
    {
        void OnClickBtnClose();

        void OnClickBtn1000();

        void OnClickBtn2000();

        void OnClickBtnOther();
    }

    public class EdyCharge_SSAW0903_View : BaseScreen
    {
        public IEdyCharge_SSAW0903_View_Event eventHandler;

        private VisualElement velScreenMng;

        private VisualElement velSSAW0903_View;

        private LocalizeLabel title;

        private BTN18_ButtonBack btnClose;

        private BTN20_ButtonTextOnly btn1000;

        private BTN20_ButtonTextOnly btn2000;

        private BTN20_ButtonTextOnly btnOther;

        private readonly List<Button> listButtons = new List<Button>();

        private Action closeFunc;

        private Action clickBtn1000Func;

        private Action clickBtn2000Func;

        private Action clickBtnOtherFunc;

        private EdyCharge_SSAW0903_Element_DTO dataIni;

        private const string moneyLabel = "moneyLabel";

        public EdyCharge_SSAW0903_View(Components components, ScreenID screenId) : base(components, screenId)
        {
            GetScreenControl();
            SetEvent();
        }

        protected override void GetScreenControl()
        {
            velScreenMng = components.UIDocumentComponent.rootVisualElement.Query<VisualElement>("VelScreenMng");
            velSSAW0903_View = velScreenMng?.Query<VisualElement>("EdyCharge_SSAW0903_View");
            title = velSSAW0903_View?.Query<LocalizeLabel>("Title");
            btnClose = velSSAW0903_View.Query<BTN18_ButtonBack>("btnClose");
            btn1000 = velSSAW0903_View.Query<BTN20_ButtonTextOnly>("selectCharge_1000");
            btn2000 = velSSAW0903_View.Query<BTN20_ButtonTextOnly>("selectCharge_2000");
            btnOther = velSSAW0903_View.Query<BTN20_ButtonTextOnly>("selectCharge_Other");

            // 03 §14.2: the Add order is what binds a button to ButtonTitle[i]. btnOther is index 2.
            listButtons.Add(btn1000);
            listButtons.Add(btn2000);
            listButtons.Add(btnOther);
        }

        protected override void SetEvent()
        {
            if (btnClose != null)
            {
                closeFunc = () => eventHandler?.OnClickBtnClose();
                this.btnClose.clicked += closeFunc;
            }

            if (btn1000 != null)
            {
                clickBtn1000Func = () => eventHandler?.OnClickBtn1000();
                this.btn1000.clicked += clickBtn1000Func;
            }

            if (btn2000 != null)
            {
                clickBtn2000Func = () => eventHandler?.OnClickBtn2000();
                this.btn2000.clicked += clickBtn2000Func;
            }

            if (btnOther != null)
            {
                clickBtnOtherFunc = () => eventHandler?.OnClickBtnOther();
                this.btnOther.clicked += clickBtnOtherFunc;
            }
        }

        public override void ResetEvent()
        {
            this.btnClose.clicked -= closeFunc;
            this.btn1000.clicked -= clickBtn1000Func;
            this.btn2000.clicked -= clickBtn2000Func;
            this.btnOther.clicked -= clickBtnOtherFunc;
        }

        // 03 §14.2: the text key reaches the control through the initialize JSON DTO.
        public void SetView(EdyCharge_SSAW0903_Element_DTO dataIni)
        {
            this.dataIni = dataIni;
            title.LocalizationKey = dataIni.TextTitle;
            btnClose.TitleButton = dataIni.ButtonClose;

            for (int i = 0; i < listButtons.Count; i++)
            {
                listButtons[i].Q<VisualElement>(moneyLabel).Q<Label>("MoneyOtherLabel").LocalizationKey = dataIni.ButtonTitle[i];
            }
        }
    }
}
//▲ ADD 2024.01.17 Fixture@Test 夹具
