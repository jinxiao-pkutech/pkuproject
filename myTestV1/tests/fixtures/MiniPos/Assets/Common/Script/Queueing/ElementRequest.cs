//▼ ADD 2024.01.17 Fixture@Test 夹具：03 §19（第 1 実参が lambda）
using System;
using System.Threading.Tasks;

namespace Assets.Common.Script.Queueing
{
    public class ElementRequest
    {
        public ElementRequest(Func<Task> asyncAction, bool isAwait = false)
        {
            this.AsyncAction = asyncAction;
        }

        public ElementRequest(Action action, bool isAwait = false)
        {
            this.Action = action;
        }

        public Func<Task> AsyncAction { get; }

        public Action Action { get; }
    }
}
//▲ ADD 2024.01.17 Fixture@Test 夹具
