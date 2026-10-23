//▼ ADD 2024.01.17 Fixture@Test 夹具：03 §7 の形
using System.Threading.Tasks;

namespace Assets.Common.Script.Base
{
    public abstract class BaseScreen
    {
        protected Components components;

        private ISocketEvent _socketEvent;

        private IScanEvent _scanEvent;

        protected BaseScreen(Components components, ScreenID screenId)
        {
            this.components = components;
            this.ScreenId = screenId;
        }

        public ScreenID ScreenId { get; private set; }

        protected abstract void GetScreenControl();

        protected abstract void SetEvent();

        public virtual void ResetEvent() { }

        public virtual void SetBaseSocketEventHandler(ISocketEvent socketEvent) => _socketEvent = socketEvent;

        public void OnReceiveSocket(EventRequest request)
        {
            _socketEvent?.OnReceiveSocket(request);
        }

        public virtual void SetBaseOnScanEventHandler(IScanEvent onScanEvent) => _scanEvent = onScanEvent;

        public async Task OnScan(EventRequest request)
        {
            if (_scanEvent != null)
            {
                await _scanEvent.OnScan(request);
            }
        }
    }
}
//▲ ADD 2024.01.17 Fixture@Test 夹具
