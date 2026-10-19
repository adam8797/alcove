using System.Runtime.Loader;
using FixtureDep;

namespace FixtureProvider;

/// <summary>A non-leaky API: only the provider's own types.</summary>
public sealed class ProviderService
{
    public string Name => "ProviderService in '" + AssemblyLoadContext.GetLoadContext(typeof(ProviderService).Assembly)?.Name + "'";

    public static ProviderService Create() => new();
}

/// <summary>A leaky API: Dep's Widget appears in signatures, so Dep is an exposed dependency.</summary>
public static class Widgets
{
    public static Widget Make() => new();

    public static string Inspect(Widget widget) => "Provider inspected " + widget.Describe();

    public static object MakeAsObject() => new Widget();

    public static string InspectObject(object value) => value is Widget widget
        ? "IS my Widget: " + widget.Describe()
        : $"NOT my Widget: {value.GetType().FullName} from '{AssemblyLoadContext.GetLoadContext(value.GetType().Assembly)?.Name}'";
}
