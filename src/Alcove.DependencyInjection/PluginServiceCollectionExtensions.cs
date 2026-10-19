using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Alcove.DependencyInjection;

/// <summary>Registers Alcove in a Microsoft.Extensions.DependencyInjection container.</summary>
public static class PluginServiceCollectionExtensions
{
    /// <summary>The package ID that <see cref="UseDependencyInjection"/> makes host-owned.</summary>
    internal const string PackageId = "Alcove.DependencyInjection";

    /// <summary>
    /// Registers <see cref="PluginHost"/> and its <see cref="ExtensionCatalog"/> as singletons. The host is created on
    /// first resolution with <see cref="UseDependencyInjection"/> applied to the options, over the root provider; its
    /// <see cref="PluginHostOptions.LoggerFactory"/> defaults to the container's <see cref="ILoggerFactory"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing loads until the app calls <c>await provider.GetRequiredService&lt;PluginHost&gt;().LoadAsync()</c>,
    /// e.g. at startup. Get extensions from the <see cref="ExtensionCatalog"/>: they're never registered in the
    /// container, which can't change after it's built.
    /// </para>
    /// <para>
    /// <see cref="PluginHost"/> is only <see cref="IAsyncDisposable"/>: dispose the provider with <c>DisposeAsync</c>.
    /// </para>
    /// </remarks>
    /// <param name="services">The app's services.</param>
    /// <param name="configure">Configures the options, e.g. <see cref="PluginHostOptions.Root"/> and the feeds.</param>
    /// <returns><paramref name="services"/>.</returns>
    public static IServiceCollection AddPlugins(this IServiceCollection services, Action<PluginHostOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        services.AddSingleton(provider =>
        {
            var options = new PluginHostOptions();
            configure(options);
            options.UseDependencyInjection(provider);
            options.LoggerFactory ??= provider.GetService<ILoggerFactory>();
            return PluginHost.Create(options);
        });
        services.AddSingleton(provider => provider.GetRequiredService<PluginHost>().Catalog);
        return services;
    }

    /// <summary>
    /// Sets up options for a host created by the app itself: <paramref name="services"/> become the host's services;
    /// extensions get constructor injection (<see cref="ActivatorUtilities"/>) unless an activator is already set;
    /// each cell gets a provider holding its <see cref="IServicePlugin"/> registrations, falling back to
    /// <paramref name="services"/>; and <c>Alcove.DependencyInjection</c> is made host-owned.
    /// </summary>
    /// <remarks>
    /// A cell's provider resolves a service from the cell when one of its plugins registered it, otherwise from the
    /// host. <c>IEnumerable&lt;T&gt;</c> returns the host's services followed by the cell's. Cell services registered by
    /// type or factory get their own dependencies from the cell and the host alike (by type: through
    /// <see cref="ActivatorUtilities"/>, so the constructor it picks). Open generic and keyed registrations of a cell
    /// resolve their dependencies from the cell only. Scopes created from a cell's provider pair a cell scope with a
    /// host scope.
    /// </remarks>
    /// <param name="options">The options.</param>
    /// <param name="services">The host's services, typically the root provider.</param>
    /// <returns><paramref name="options"/>.</returns>
    public static PluginHostOptions UseDependencyInjection(this PluginHostOptions options, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(services);
        options.Services = services;
        options.Activator ??= ServiceProviderActivator.Instance;
        options.CellServicesFactory ??= ServiceCollectionCellServicesFactory.Instance;
        if (!options.HostOwnedPackages.Contains(PackageId, StringComparer.OrdinalIgnoreCase))
        {
            options.HostOwnedPackages.Add(PackageId);
        }

        return options;
    }
}
