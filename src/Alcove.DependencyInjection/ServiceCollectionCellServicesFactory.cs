using Microsoft.Extensions.DependencyInjection;

namespace Alcove.DependencyInjection;

/// <summary>
/// Builds each cell's provider from a child <see cref="ServiceCollection"/> holding the registrations of the cell's
/// <see cref="IServicePlugin"/>s, wrapped in a <see cref="CellServiceProvider"/> that falls back to the host.
/// </summary>
internal sealed class ServiceCollectionCellServicesFactory : ICellServicesFactory
{
    public static ServiceCollectionCellServicesFactory Instance { get; } = new();

    public IReadOnlyCollection<string> ServicePluginInterfaces { get; } = [typeof(IServicePlugin).FullName!];

    public IServiceProvider CreateServices(CellServicesContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var registered = new ServiceCollection();
        foreach (var plugin in context.Plugins)
        {
            if (plugin.EntryPoint is not IServicePlugin servicePlugin)
            {
                throw new InvalidOperationException(
                    $"The entry point of plugin '{plugin.Plugin.Id}' implements a {typeof(IServicePlugin).FullName} that isn't the host's (does the plugin carry its own copy of {typeof(IServicePlugin).Assembly.GetName().Name}?), so cell '{context.Cell}' has no service provider.");
            }

            try
            {
                servicePlugin.ConfigureServices(registered);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Plugin '{plugin.Plugin.Id}' threw in ConfigureServices, so cell '{context.Cell}' has no service provider: {ex.Message}", ex);
            }
        }

        // Registrations by type or factory resolve their own dependencies through the cell's view (cell + host), found
        // through a scoped holder: the root provider's, or the paired scope's.
        IServiceCollection services = new ServiceCollection();
        services.AddScoped<CellServiceProvider.Holder>();
        foreach (var descriptor in registered)
        {
            services.Add(WithFallback(descriptor));
        }

        var cell = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = false, ValidateOnBuild = false });
        return CellServiceProvider.CreateRoot(cell, context.HostServices);
    }

    private static ServiceDescriptor WithFallback(ServiceDescriptor descriptor)
    {
        if (descriptor.IsKeyedService || descriptor.ServiceType.IsGenericTypeDefinition)
        {
            return descriptor;
        }

        if (descriptor.ImplementationFactory is { } factory)
        {
            return new ServiceDescriptor(descriptor.ServiceType, provider => factory(CellServiceProvider.Holder.Of(provider)), descriptor.Lifetime);
        }

        if (descriptor.ImplementationType is { } type)
        {
            return new ServiceDescriptor(
                descriptor.ServiceType,
                provider => ActivatorUtilities.CreateInstance(CellServiceProvider.Holder.Of(provider), type),
                descriptor.Lifetime);
        }

        return descriptor;
    }
}
