using Microsoft.Extensions.DependencyInjection;

namespace Alcove.Autofac;

/// <summary>
/// Creates extensions registered by type with <see cref="ActivatorUtilities"/> over the plugin's cell scope (an
/// <c>AutofacServiceProvider</c>), so their constructors receive cell and host services without being registered in
/// the container. Factories run as registered.
/// </summary>
internal sealed class AutofacExtensionActivator : IExtensionActivator
{
    public static AutofacExtensionActivator Instance { get; } = new();

    public object CreateInstance(ExtensionActivationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Factory is { } factory
            ? factory()
            : ActivatorUtilities.CreateInstance(context.Services, context.ImplementationType!);
    }
}
