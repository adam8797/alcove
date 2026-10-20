using Alcove.Catalog;
using Microsoft.Extensions.DependencyInjection;

namespace Alcove.DependencyInjection.Composition;

/// <summary>
/// Creates extensions registered by type with <see cref="ActivatorUtilities"/> over the plugin's cell services, so
/// their constructors receive cell and host services. Factories run as registered.
/// </summary>
internal sealed class ServiceProviderActivator : IExtensionActivator
{
    public static ServiceProviderActivator Instance { get; } = new();

    public object CreateInstance(ExtensionActivationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Factory is { } factory
            ? factory()
            : ActivatorUtilities.CreateInstance(context.Services, context.ImplementationType!);
    }
}
