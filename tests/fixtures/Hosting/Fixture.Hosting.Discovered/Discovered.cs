using Alcove;
using Fixture.HostContracts;

namespace Fixture.Hosting.Discovered;

/// <summary>The entry point, found without a PluginEntryPoint (through its abstract base class).</summary>
public sealed class DiscoveredPlugin : DiscoveredPluginBase
{
    public override void Register(IExtensionRegistrar registrar, IPluginContext context) =>
        registrar.Add<IDescriber>(() => new DiscoveredDescriber("registered by the discovered entry point"));
}

/// <summary>Abstract: not a candidate.</summary>
public abstract class DiscoveredPluginBase : IPlugin
{
    public abstract void Register(IExtensionRegistrar registrar, IPluginContext context);
}

/// <summary>Not public: not a candidate.</summary>
internal sealed class InternalPlugin : IPlugin
{
    public void Register(IExtensionRegistrar registrar, IPluginContext context) =>
        registrar.Add<IDescriber>(() => new DiscoveredDescriber("internal"));
}

/// <summary>Not scanned: the plugin has an entry point.</summary>
[Extension(typeof(IDescriber))]
public sealed class DiscoveredDescriber(string text) : IDescriber
{
    public DiscoveredDescriber() : this("scanned") { }

    public string Describe() => text;
}
