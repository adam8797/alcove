using System.Runtime.Loader;
using Alcove.Cells;
using Alcove.IntegrationTests.Infrastructure;
using Alcove.Wpf.Loading;
using Microsoft.Extensions.Logging;

namespace Alcove.Wpf.Tests;

/// <summary>A host whose construction fails disposes the cell observers it already created (e.g. the forwarder).</summary>
[Collection(nameof(ForwarderTests))]
public sealed class HostConstructionTests : IDisposable
{
    private readonly TempDirectory _root = new("alcove-wpf-create");

    public void Dispose() => _root.Dispose();

    [Fact]
    public void AFailingCreate_LeavesNoForwarderRegisteredOnTheDefaultContext()
    {
        var logs = new ListLoggerFactory();
        ForwardingObserver? created = null;
        var options = new PluginHostOptions { Root = _root.Path, LoggerFactory = logs };
        options.CellObservers.Add(loggerFactory => created = new ForwardingObserver(loggerFactory));
        options.CellObservers.Add(_ => throw new InvalidOperationException("second factory"));

        var thrown = Assert.Throws<InvalidOperationException>(() => PluginHost.Create(options));

        Assert.Equal("second factory", thrown.Message);
        Assert.NotNull(created);
        Assert.False(created.Forwarder.IsRegistered);
        Assert.Equal(["registered", "removed"], logs.WithEventId(6005).Select(e => e.Message.Split(' ')[^1]));
        Assert.Null(created.Forwarder.OnResolving(AssemblyLoadContext.Default, new System.Reflection.AssemblyName("Anything")));
    }

    [Fact]
    public void AFactoryReturningNull_AlsoDisposesTheEarlierObservers()
    {
        ForwardingObserver? created = null;
        var options = new PluginHostOptions { Root = _root.Path };
        options.CellObservers.Add(loggerFactory => created = new ForwardingObserver(loggerFactory));
        options.CellObservers.Add(_ => null!);

        Assert.Throws<InvalidOperationException>(() => PluginHost.Create(options));

        Assert.False(created!.Forwarder.IsRegistered);
    }

    /// <summary>Holds a registered forwarder, as Alcove.Wpf's observer does when ForwardDefaultContextLoads is on.</summary>
    private sealed class ForwardingObserver : ICellObserver, IDisposable
    {
        public ForwardingObserver(ILoggerFactory loggerFactory)
        {
            Forwarder = new DefaultContextForwarder(loggerFactory.CreateLogger("Alcove.Wpf"));
            Forwarder.Register();
        }

        public DefaultContextForwarder Forwarder { get; }

        public void Dispose() => Forwarder.Dispose();
    }
}
