//▼ ADD 2024.01.17 Fixture@Test 夹具：03 §12 の登記形態（工厂别名と多重登記）
using Microsoft.Extensions.DependencyInjection;
using TEC.SEJPOS.Model.Apl.KeySeq;
using TEC.SEJPOS.Model.Apl.KeySeq.Impl;
using TEC.SEJPOS.Model.DeviceBroker;
using TEC.SEJPOS.Model.DeviceBroker.Impl;
using Assets.Common.Script.API;

namespace TEC.SEJPOS.Model
{
    public static partial class ServiceLocator
    {
        static partial void InitDeviceBroker()
        {
            Configure(services =>
            {
                services.AddSingleton<EValueRwController>();
                services.AddSingleton<IEValueRwCommand>(ServiceProvider => ServiceProvider.GetRequiredService<EValueRwController>());
                services.AddSingleton<IDeviceBrokerMessageEvent>(serviceProvider => serviceProvider.GetRequiredService<EValueRwController>());
                services.AddSingleton<ScannerController>();
                services.AddSingleton<IDeviceBrokerMessageEvent>(serviceProvider => serviceProvider.GetRequiredService<ScannerController>());
                services.AddSingleton<IApiConnector, ApiConnector>();
                services.AddSingleton<IKeySeqRegister, KeySeqRegister>();
                services.AddSingleton<IStageNoStatus, StageNoStatus>();
            });
        }
    }
}
//▲ ADD 2024.01.17 Fixture@Test 夹具
