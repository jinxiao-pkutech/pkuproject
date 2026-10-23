//▼ ADD 2024.01.17 Fixture@Test 夹具：UnityEngine の最小スタブ
using System;

namespace UnityEngine
{
    // Native interop surface (03 §20). Only the members the fixture calls are declared.
    public class AndroidJavaObject : IDisposable
    {
        public AndroidJavaObject(string className, params object[] args) { ClassName = className; }

        public string ClassName { get; }

        public void Call(string methodName, params object[] args) { }

        public T Call<T>(string methodName, params object[] args) => default(T);

        public void Dispose() { }
    }

    public class AndroidJavaClass : IDisposable
    {
        public AndroidJavaClass(string className) { ClassName = className; }

        public string ClassName { get; }

        public T CallStatic<T>(string methodName, params object[] args) => default(T);

        public T Get<T>(string fieldName) => default(T);

        public void Dispose() { }
    }
}

namespace UnityEngine.UIElements
{
    public class VisualElement
    {
        // 03 §9: controls are found with Query<T>("name") or Q<T>("name"); the generic argument is
        // the control type and the string argument is the control name.
        public T Query<T>(string name) where T : VisualElement => null;

        public T Q<T>(string name) where T : VisualElement => null;

        public string name { get; set; }
    }

    public class Label : VisualElement
    {
        public string LocalizationKey { get; set; }

        public string Title { get; set; }

        public string MessageText { get; set; }

        public string Unit { get; set; }

        public string Text { get; set; }
    }

    public class Button : VisualElement
    {
        public event Action clicked;

        public string TitleButton { get; set; }

        public string Title { get; set; }

        public string LocalizationKey { get; set; }

        // Kept so the fixture compiles without warnings about an unused event.
        public void RaiseClicked() => clicked?.Invoke();
    }

    public class UIDocument
    {
        public VisualElement rootVisualElement { get; set; } = new VisualElement();
    }
}
//▲ ADD 2024.01.17 Fixture@Test 夹具
