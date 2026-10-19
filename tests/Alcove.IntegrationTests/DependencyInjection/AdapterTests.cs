using Alcove.Autofac;
using Alcove.DependencyInjection;
using Alcove.IntegrationTests.Infrastructure;
using Autofac;
using Fixture.HostContracts;
using Microsoft.Extensions.DependencyInjection;

namespace Alcove.IntegrationTests.DependencyInjection;

/// <summary>The shared suite with Microsoft.Extensions.DependencyInjection (<c>services.AddPlugins</c>).</summary>
[Collection(DependencyInjectionCollection.Name)]
public sealed class MicrosoftDependencyInjectionTests(DependencyInjectionFixtures fixtures) : CellServicesTests(fixtures)
{
    protected override string AdapterPackage => "Alcove.DependencyInjection";

    protected override string AdapterName => "ms";

    protected override IAppContainer BuildContainer(Action<PluginHostOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEventLog>(Log);
        services.AddSingleton<ICellService, HostCellService>();
        services.AddPlugins(configure);
        return new Container(services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }));
    }

    private sealed class Container(ServiceProvider provider) : IAppContainer
    {
        public T Resolve<T>()
            where T : notnull => provider.GetRequiredService<T>();

        public IEnumerable<T> ResolveAll<T>() => provider.GetServices<T>();

        public ValueTask DisposeAsync() => provider.DisposeAsync();
    }
}

/// <summary>The shared suite with Autofac (<c>builder.AddPlugins</c>).</summary>
[Collection(DependencyInjectionCollection.Name)]
public sealed class AutofacTests(DependencyInjectionFixtures fixtures) : CellServicesTests(fixtures)
{
    protected override string AdapterPackage => "Alcove.Autofac";

    protected override string AdapterName => "autofac";

    protected override IAppContainer BuildContainer(Action<PluginHostOptions> configure)
    {
        var builder = new ContainerBuilder();
        builder.RegisterInstance(Log).As<IEventLog>();
        builder.RegisterType<HostCellService>().As<ICellService>().SingleInstance();
        builder.AddPlugins(configure);
        return new Container(builder.Build());
    }

    private sealed class Container(IContainer container) : IAppContainer
    {
        public T Resolve<T>()
            where T : notnull => container.Resolve<T>();

        public IEnumerable<T> ResolveAll<T>() => container.Resolve<IEnumerable<T>>();

        public ValueTask DisposeAsync() => container.DisposeAsync();
    }
}
