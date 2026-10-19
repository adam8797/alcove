using System.Runtime.Loader;
using Alcove;
using FixtureDep;
using Fixture.HostContracts;

namespace Fixture.Hosting.Provider;

public sealed class ProviderPlugin : IPlugin
{
    public void Register(IExtensionRegistrar registrar, IPluginContext context) =>
        registrar.Add<IDescriber>(() => new ProviderDescriber());
}

public sealed class ProviderDescriber : IDescriber
{
    public string Describe() => "Provider: " + ProviderService.Create().Name + ", " + Widgets.Make().Describe();
}

/// <summary>A non-leaky API: only the provider's own types.</summary>
public sealed class ProviderService
{
    public string Name => "ProviderService in '" + AssemblyLoadContext.GetLoadContext(typeof(ProviderService).Assembly)?.Name + "'";

    public static ProviderService Create() => new();
}

/// <summary>A leaky API: Dep's Widget appears in signatures.</summary>
public static class Widgets
{
    public static Widget Make() => new();

    public static string InspectObject(object value) => value is Widget widget
        ? "IS my Widget: " + widget.Describe()
        : $"NOT my Widget: {value.GetType().FullName} from '{AssemblyLoadContext.GetLoadContext(value.GetType().Assembly)?.Name}'";
}
