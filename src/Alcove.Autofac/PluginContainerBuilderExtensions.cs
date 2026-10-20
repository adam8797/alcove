using Alcove.Autofac.Composition;
using Alcove.Catalog;
using Autofac;
using Autofac.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Alcove.Autofac;

/// <summary>Registers Alcove in an Autofac container.</summary>
public static class PluginContainerBuilderExtensions
{
    /// <summary>The package ID that <see cref="UseAutofac"/> makes host-owned.</summary>
    internal const string PackageId = "Alcove.Autofac";

    /// <summary>
    /// Registers <see cref="PluginHost"/> and its <see cref="ExtensionCatalog"/> as single instances. The host is
    /// created on first resolution with <see cref="UseAutofac"/> applied to the options, over the root lifetime scope;
    /// its <see cref="PluginHostOptions.LoggerFactory"/> defaults to the container's <see cref="ILoggerFactory"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing loads until the app calls <c>await container.Resolve&lt;PluginHost&gt;().LoadAsync()</c>, e.g. at
    /// startup. Get extensions from the <see cref="ExtensionCatalog"/>: they're never registered in the container,
    /// which can't change after it's built.
    /// </para>
    /// <para>
    /// <see cref="PluginHost"/> is only <see cref="IAsyncDisposable"/>: dispose the container with <c>DisposeAsync</c>.
    /// </para>
    /// </remarks>
    /// <param name="builder">The app's container builder.</param>
    /// <param name="configure">Configures the options, e.g. <see cref="PluginHostOptions.Root"/> and the feeds.</param>
    /// <returns><paramref name="builder"/>.</returns>
    public static ContainerBuilder AddPlugins(this ContainerBuilder builder, Action<PluginHostOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        builder.Register(context =>
        {
            // A single instance is activated in the root scope, so this is the container itself.
            var root = context.Resolve<ILifetimeScope>();
            var options = new PluginHostOptions();
            configure(options);
            options.UseAutofac(root);
            options.LoggerFactory ??= context.ResolveOptional<ILoggerFactory>();
            return PluginHost.Create(options);
        }).SingleInstance();
        builder.Register(context => context.Resolve<PluginHost>().Catalog).SingleInstance();
        return builder;
    }

    /// <summary>
    /// Sets up options for a host created by the app itself: <paramref name="scope"/> becomes the host's services
    /// (through an <see cref="AutofacServiceProvider"/>); extensions get constructor injection unless an activator is
    /// already set; each cell gets a child lifetime scope of <paramref name="scope"/> holding its
    /// <see cref="IServicePlugin"/> registrations; and <c>Alcove.Autofac</c> is made host-owned.
    /// </summary>
    /// <remarks>
    /// Autofac's own child-scope rules apply: a cell's registrations override the host's for single resolution, and
    /// <c>IEnumerable&lt;T&gt;</c> holds the host's and the cell's. Each cell's scope is disposed with the host, in
    /// reverse load order.
    /// </remarks>
    /// <param name="options">The options.</param>
    /// <param name="scope">The host's lifetime scope, typically the root container. The host never disposes it.</param>
    /// <returns><paramref name="options"/>.</returns>
    public static PluginHostOptions UseAutofac(this PluginHostOptions options, ILifetimeScope scope)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(scope);
        options.Services = new AutofacServiceProvider(scope);
        options.Activator ??= AutofacExtensionActivator.Instance;
        options.CellServicesFactory ??= new LifetimeScopeCellServicesFactory(scope);
        if (!options.HostOwnedPackages.Contains(PackageId, StringComparer.OrdinalIgnoreCase))
        {
            options.HostOwnedPackages.Add(PackageId);
        }

        return options;
    }
}
