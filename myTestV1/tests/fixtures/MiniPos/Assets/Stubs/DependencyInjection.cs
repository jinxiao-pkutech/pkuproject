//▼ ADD 2024.01.17 Fixture@Test 夹具：Microsoft.Extensions.DependencyInjection の最小スタブ
using System;
using System.Collections.Generic;

namespace Microsoft.Extensions.DependencyInjection
{
    public interface IServiceProvider2
    {
    }

    public interface IServiceCollection
    {
        // 03 §12 registration shapes.
        IServiceCollection AddSingleton<TService>() where TService : class;

        IServiceCollection AddSingleton<TService, TImplementation>()
            where TService : class
            where TImplementation : class, TService;

        IServiceCollection AddSingleton<TService>(Func<IServiceProviderStub, TService> factory) where TService : class;

        IServiceCollection AddTransient<TService, TImplementation>()
            where TService : class
            where TImplementation : class, TService;

        IServiceCollection AddScoped<TService, TImplementation>()
            where TService : class
            where TImplementation : class, TService;
    }

    public interface IServiceProviderStub
    {
        TService GetRequiredService<TService>() where TService : class;

        TService GetService<TService>() where TService : class;
    }

    public sealed class ServiceCollectionStub : IServiceCollection
    {
        private readonly List<Type> registered = new List<Type>();

        public IServiceCollection AddSingleton<TService>() where TService : class
        {
            registered.Add(typeof(TService));
            return this;
        }

        public IServiceCollection AddSingleton<TService, TImplementation>()
            where TService : class
            where TImplementation : class, TService
        {
            registered.Add(typeof(TImplementation));
            return this;
        }

        public IServiceCollection AddSingleton<TService>(Func<IServiceProviderStub, TService> factory) where TService : class
        {
            registered.Add(typeof(TService));
            return this;
        }

        public IServiceCollection AddTransient<TService, TImplementation>()
            where TService : class
            where TImplementation : class, TService
        {
            registered.Add(typeof(TImplementation));
            return this;
        }

        public IServiceCollection AddScoped<TService, TImplementation>()
            where TService : class
            where TImplementation : class, TService
        {
            registered.Add(typeof(TImplementation));
            return this;
        }

        public int Count => registered.Count;
    }
}
//▲ ADD 2024.01.17 Fixture@Test 夹具
