using Fixture.Services.Contracts;

namespace Fixture.Services.HostSdk;

public abstract class HostServicePluginBase : IServicePlugin
{
    public void ConfigureServices(IList<string> services) => services.Add(GetType().Name);
}
