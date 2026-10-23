//▼ ADD 2024.01.17 Fixture@Test 夹具：03 §12 の形
using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;

namespace TEC.SEJPOS.Model
{
    public static partial class ServiceLocator
    {
        private static readonly ServiceCollectionStub services = new ServiceCollectionStub();

        public static T GetService<T>() => default(T);

        public static IEnumerable<T> GetServices<T>() => new List<T>();

        public static void Configure(Action<IServiceCollection> configure) => configure(services);

        static partial void InitDeviceBroker();

        public static void Init() => InitDeviceBroker();
    }
}
//▲ ADD 2024.01.17 Fixture@Test 夹具
