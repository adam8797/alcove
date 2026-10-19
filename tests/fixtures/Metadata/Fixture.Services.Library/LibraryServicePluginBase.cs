using Fixture.Services.Contracts;

namespace Fixture.Services.Library;

public abstract class LibraryServicePluginBase : IServicePlugin
{
    public void ConfigureServices(IList<string> services) => services.Add(GetType().Name);
}
