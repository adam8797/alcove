using Fixture.Services.Contracts;
using Fixture.Services.HostSdk;
using Fixture.Services.Library;

namespace Fixture.Services.Plugin;

/// <summary>Implements the interface directly.</summary>
public sealed class DirectPlugin : IServicePlugin
{
    public void ConfigureServices(IList<string> services) => services.Add("direct");
}

/// <summary>Through a base class of the plugin's own assembly.</summary>
public sealed class DerivedPlugin : OwnServicePluginBase;

public abstract class OwnServicePluginBase : IServicePlugin
{
    public void ConfigureServices(IList<string> services) => services.Add(GetType().Name);
}

/// <summary>Through a generic base class of the plugin's own assembly.</summary>
public sealed class GenericDerivedPlugin : GenericServicePluginBase<int>;

public abstract class GenericServicePluginBase<T> : IServicePlugin
{
    public void ConfigureServices(IList<string> services) => services.Add(typeof(T).Name);
}

/// <summary>Through an interface of the plugin's own assembly that extends it.</summary>
public sealed class ExtendedInterfacePlugin : IOwnServicePlugin
{
    public void ConfigureServices(IList<string> services) => services.Add("extended");
}

public interface IOwnServicePlugin : IServicePlugin;

/// <summary>Through a base class of another library of the cell.</summary>
public sealed class LibraryDerivedPlugin : LibraryServicePluginBase;

/// <summary>Through a base class of the host's plugin SDK (a host-owned assembly).</summary>
public sealed class HostSdkDerivedPlugin : HostServicePluginBase;

/// <summary>Implements neither.</summary>
public sealed class PlainPlugin : IDisposable
{
    public void Dispose()
    {
    }
}

public static class Outer
{
    /// <summary>A nested entry point (<c>Outer+NestedPlugin</c>).</summary>
    public sealed class NestedPlugin : IServicePlugin
    {
        public void ConfigureServices(IList<string> services) => services.Add("nested");
    }
}
