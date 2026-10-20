using Alcove.Cells;
using Autofac;
using Autofac.Extensions.DependencyInjection;

namespace Alcove.Autofac.Composition;

/// <summary>
/// Gives each cell a child lifetime scope of the host's scope holding the registrations of the cell's
/// <see cref="IServicePlugin"/>s. Autofac falls back to the parent natively. The returned
/// <see cref="AutofacServiceProvider"/> owns the child scope: disposing it disposes the scope.
/// </summary>
internal sealed class LifetimeScopeCellServicesFactory(ILifetimeScope host) : ICellServicesFactory
{
    public IReadOnlyCollection<string> ServicePluginInterfaces { get; } = [typeof(IServicePlugin).FullName!];

    public IServiceProvider CreateServices(CellServicesContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scope = host.BeginLifetimeScope(builder =>
        {
            foreach (var plugin in context.Plugins)
            {
                if (plugin.EntryPoint is not IServicePlugin servicePlugin)
                {
                    throw new InvalidOperationException(
                        $"The entry point of plugin '{plugin.Plugin.Id}' implements a {typeof(IServicePlugin).FullName} that isn't the host's (does the plugin carry its own copy of {typeof(IServicePlugin).Assembly.GetName().Name}?), so cell '{context.Cell}' has no lifetime scope.");
                }

                try
                {
                    servicePlugin.ConfigureContainer(builder);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"Plugin '{plugin.Plugin.Id}' threw in ConfigureContainer, so cell '{context.Cell}' has no lifetime scope: {ex.Message}", ex);
                }
            }
        });
        return new AutofacServiceProvider(scope);
    }
}
