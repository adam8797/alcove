using System.Reflection;

namespace Alcove.Internal.Catalog;

/// <summary>The core's activator: calls the registered factory, or a public parameterless constructor.</summary>
internal sealed class DefaultExtensionActivator : IExtensionActivator
{
    public static DefaultExtensionActivator Instance { get; } = new();

    public object CreateInstance(ExtensionActivationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Factory is { } factory)
        {
            return factory();
        }

        var type = context.ImplementationType!;
        if (type.IsAbstract || type.GetConstructor(Type.EmptyTypes) is not { IsPublic: true } constructor)
        {
            throw new MissingMethodException(
                $"{type} needs a public parameterless constructor to be created without a dependency injection add-on.");
        }

        try
        {
            return constructor.Invoke(null);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(ex.InnerException);
            throw;
        }
    }
}
