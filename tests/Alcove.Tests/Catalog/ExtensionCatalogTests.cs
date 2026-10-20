using System.Collections.Concurrent;
using System.Runtime.Loader;
using Alcove.Catalog;
using Alcove.Loading;
using NuGet.Versioning;

namespace Alcove.Tests.Catalog;

public sealed class ExtensionCatalogTests
{
    public interface IWidget
    {
        string Name { get; }
    }

    public interface IOther;

    [Fact]
    public void GetAll_ListsByExactExtensionPointInAddOrder_WithoutCreating()
    {
        var catalog = new ExtensionCatalog();
        var a = new FakeOwner("A");
        var b = new FakeOwner("B");
        var created = 0;
        catalog.Add(a.Plugin, [a.Factory<IWidget>(() => { created++; return new Widget("a1"); }), a.Factory<IOther>(() => new Other())]);
        catalog.Add(b.Plugin, [b.Factory<IWidget>(() => new Widget("b1"), new Label("B"))]);

        var all = catalog.GetAll<IWidget>();

        Assert.Equal(["A", "B"], all.Select(e => e.Plugin.Id));
        Assert.Single(catalog.GetAll<IOther>());
        Assert.Empty(catalog.GetAll<IDisposable>());
        Assert.Equal(0, created);
        Assert.Equal("b1", all[1].Value.Name);
    }

    [Fact]
    public void GetAllWithMetadata_KeepsOnlyMatchingMetadata_AndSharesTheInstance()
    {
        var catalog = new ExtensionCatalog();
        var owner = new FakeOwner("A");
        catalog.Add(owner.Plugin,
        [
            owner.Factory<IWidget>(() => new Widget("labelled"), new Label("one")),
            owner.Factory<IWidget>(() => new Widget("plain")),
            owner.Factory<IWidget>(() => new Widget("other"), "a string"),
        ]);

        var typed = Assert.Single(catalog.GetAll<IWidget, Label>());

        Assert.Equal("one", typed.Metadata.Text);
        Assert.Same(typed.Value, catalog.GetAll<IWidget>()[0].Value);
        Assert.Same(typed.Value, catalog.GetAll<IWidget, Label>()[0].Value);
    }

    [Fact]
    public void Singleton_IsCreatedOnce_EvenConcurrently()
    {
        var owner = new FakeOwner("A");
        var calls = 0;
        var extension = (Extension<IWidget>)owner.Factory<IWidget>(() =>
        {
            Interlocked.Increment(ref calls);
            Thread.Sleep(20);
            return new Widget("w");
        });

        var values = new ConcurrentBag<IWidget>();
        Parallel.For(0, 16, _ => values.Add(extension.Value));

        Assert.Equal(1, calls);
        Assert.Single(values.Distinct());
        Assert.Equal([values.First()], owner.Singletons);
        Assert.Contains("singleton", Assert.Throws<InvalidOperationException>(() => extension.CreateInstance()).Message);
    }

    [Fact]
    public void ASingletonNeedingItselfWhileBeingCreated_IsAClearError()
    {
        var owner = new FakeOwner("A");
        Extension<IWidget>? self = null;
        self = (Extension<IWidget>)owner.Factory<IWidget>(() => self!.Value);

        var ex = Assert.Throws<PluginException>(() => self.Value);

        Assert.Contains("needs itself", ex.InnerException!.Message);
    }

    [Fact]
    public void ASingletonTheOwnerRefuses_IsHandedBackForDisposal_AndNotReturned()
    {
        // Round 2, finding 9: the host refuses a singleton finished after it disposed the plugin's singletons.
        var owner = new FakeOwner("A") { SingletonsClosed = true };
        var extension = (Extension<IWidget>)owner.Factory<IWidget>(() => new Widget("late"));

        Assert.Throws<ObjectDisposedException>(() => extension.Value);

        Assert.Equal("late", Assert.IsType<Widget>(Assert.Single(owner.Refused)).Name);
        Assert.Empty(owner.Singletons);
        Assert.Empty(owner.Faults);
    }

    [Fact]
    public void Transient_GivesANewInstanceEachTime()
    {
        var owner = new FakeOwner("A");
        var extension = (Extension<IWidget>)owner.Factory<IWidget>(() => new Widget("w"), lifetime: ExtensionLifetime.Transient);

        Assert.NotSame(extension.CreateInstance(), extension.CreateInstance());
        Assert.Contains("CreateInstance", Assert.Throws<InvalidOperationException>(() => extension.Value).Message);
        Assert.Empty(owner.Singletons);   // the caller owns transients
    }

    [Fact]
    public void GetInstance_FollowsTheLifetime()
    {
        var owner = new FakeOwner("A");
        var singleton = (Extension<IWidget>)owner.Factory<IWidget>(() => new Widget("s"));
        var transient = (Extension<IWidget>)owner.Factory<IWidget>(() => new Widget("t"), lifetime: ExtensionLifetime.Transient);

        Assert.Same(singleton.Value, singleton.GetInstance());
        Assert.Same(singleton.GetInstance(), singleton.GetInstance());
        Assert.NotSame(transient.GetInstance(), transient.GetInstance());
        Assert.Equal("t", transient.GetInstance().Name);
        Assert.Single(owner.Singletons);   // transients from GetInstance belong to the caller
    }

    [Fact]
    public void GetInstance_ReportsCreationFailuresAndDisposal()
    {
        var owner = new FakeOwner("A");
        var failing = (Extension<IWidget>)owner.Factory<IWidget>(() => throw new FormatException(), lifetime: ExtensionLifetime.Transient);
        var singleton = (Extension<IWidget>)owner.Factory<IWidget>(() => new Widget("s"));

        Assert.IsType<FormatException>(Assert.Throws<PluginException>(() => failing.GetInstance()).InnerException);
        Assert.Single(owner.Faults);

        owner.IsDisposed = true;
        Assert.Throws<ObjectDisposedException>(() => singleton.GetInstance());
        Assert.Throws<ObjectDisposedException>(() => failing.GetInstance());
    }

    [Fact]
    public void ACreationFailure_FaultsThePlugin_AndIsntCached()
    {
        var owner = new FakeOwner("A");
        var attempts = 0;
        var extension = (Extension<IWidget>)owner.Factory<IWidget>(() => ++attempts == 1 ? throw new FormatException("bad") : new Widget("ok"));

        var ex = Assert.Throws<PluginException>(() => extension.Value);

        Assert.Equal("A", ex.PluginId);
        Assert.IsType<FormatException>(ex.InnerException);
        Assert.IsType<FormatException>(Assert.Single(owner.Faults));
        Assert.Equal("ok", extension.Value.Name);
    }

    [Fact]
    public void ASingletonFault_IsReportedOutsideTheRegistrationLock()
    {
        // Review finding 7: PluginFaulted handlers are app code. While one runs, another thread can use the extension.
        var owner = new FakeOwner("A");
        var attempts = 0;
        var extension = (Extension<IWidget>)owner.Factory<IWidget>(() => Interlocked.Increment(ref attempts) == 1 ? throw new FormatException("bad") : new Widget("ok"));
        string? fromHandler = "blocked";
        owner.OnFault = _ =>
        {
            using var done = new ManualResetEventSlim();
            var other = new Thread(() =>
            {
                fromHandler = extension.Value.Name;
                done.Set();
            }) { IsBackground = true };
            other.Start();
            done.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        };

        Assert.Throws<PluginException>(() => extension.Value);

        Assert.Equal("ok", fromHandler);
        Assert.Equal("ok", extension.Value.Name);
    }

    [Fact]
    public async Task Observe_DisposingFromAnotherThread_DoesntWaitForARunningCallback()
    {
        // Review finding 7: a callback running on a loading thread (e.g. waiting for the UI thread) must not block
        // Dispose on another thread; nothing is delivered after Dispose returns.
        var catalog = new ExtensionCatalog();
        using var inCallback = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var delivered = new List<string>();
        var subscription = catalog.Observe<IWidget>(e =>
        {
            lock (delivered)
            {
                delivered.Add(e.Plugin.Id);
            }

            inCallback.Set();
            release.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        });
        var a = new FakeOwner("A");
        var adding = Task.Factory.StartNew(() => catalog.Add(a.Plugin, [a.Factory<IWidget>(() => new Widget("a"))]), TestContext.Current.CancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Assert.True(inCallback.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));

        var disposing = Task.Factory.StartNew(subscription.Dispose, TestContext.Current.CancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var disposedPromptly = await Task.WhenAny(disposing, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)) == disposing;
        release.Set();
        await adding;
        await disposing;

        Assert.True(disposedPromptly, "Dispose waited for the callback running on another thread.");
        var b = new FakeOwner("B");
        catalog.Add(b.Plugin, [b.Factory<IWidget>(() => new Widget("b"))]);
        Assert.Equal(["A"], delivered);
    }

    [Fact]
    public void TheActivator_GetsTheRegistrationAndMustReturnTheExtensionPoint()
    {
        var activator = new RecordingActivator(new Other());
        var owner = new FakeOwner("A", activator);
        var registrar = new ExtensionRegistrar(owner);
        registrar.Add<IWidget, Widget>("meta", ExtensionLifetime.Transient);
        var extension = (Extension<IWidget>)Assert.Single(registrar.Extensions);

        var ex = Assert.Throws<PluginException>(() => extension.CreateInstance());

        Assert.IsType<InvalidCastException>(ex.InnerException);
        var context = Assert.Single(activator.Contexts);
        Assert.Equal((typeof(IWidget), typeof(Widget), (Func<object>?)null, ExtensionLifetime.Transient), (context.ExtensionPoint, context.ImplementationType, context.Factory, context.Lifetime));
        Assert.Same(owner.Plugin, context.Plugin);
        Assert.Same(owner.Services, context.Services);
    }

    [Fact]
    public void TheDefaultActivator_NeedsAPublicParameterlessConstructor()
    {
        var owner = new FakeOwner("A");
        var registrar = new ExtensionRegistrar(owner);
        registrar.Add<IWidget, Widget>();
        registrar.Add<IWidget, ParameterlessWidget>();

        Assert.IsType<MissingMethodException>(Assert.Throws<PluginException>(() => ((Extension<IWidget>)registrar.Extensions[0]).Value).InnerException);
        Assert.Equal("parameterless", ((Extension<IWidget>)registrar.Extensions[1]).Value.Name);
    }

    [Fact]
    public void TheRegistrar_RejectsLateAndInvalidRegistrations()
    {
        var owner = new FakeOwner("A");
        var registrar = new ExtensionRegistrar(owner);

        Assert.Throws<ArgumentNullException>(() => registrar.Add<IWidget>(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => registrar.Add<IWidget>(() => new Widget("w"), lifetime: (ExtensionLifetime)7));
        Assert.Throws<ArgumentException>(() => registrar.Add(typeof(IWidget), typeof(Other), null, ExtensionLifetime.Singleton));
        registrar.Complete();
        Assert.Throws<InvalidOperationException>(() => registrar.Add<IWidget>(() => new Widget("late")));
    }

    [Fact]
    public void AfterTheHostIsDisposed_NothingIsCreated()
    {
        var owner = new FakeOwner("A");
        var singleton = (Extension<IWidget>)owner.Factory<IWidget>(() => new Widget("w"));
        _ = singleton.Value;

        owner.IsDisposed = true;

        Assert.Throws<ObjectDisposedException>(() => singleton.Value);
    }

    [Fact]
    public void Observe_DeliversExistingThenNew_UntilDisposed()
    {
        var catalog = new ExtensionCatalog();
        var a = new FakeOwner("A");
        catalog.Add(a.Plugin, [a.Factory<IWidget>(() => new Widget("a")), a.Factory<IOther>(() => new Other())]);
        var seen = new List<string>();

        var subscription = catalog.Observe<IWidget>(e => seen.Add(e.Plugin.Id));
        Assert.Equal(["A"], seen);

        var b = new FakeOwner("B");
        catalog.Add(b.Plugin, [b.Factory<IWidget>(() => new Widget("b")), b.Factory<IWidget>(() => new Widget("b2"))]);
        Assert.Equal(["A", "B", "B"], seen);

        subscription.Dispose();
        var c = new FakeOwner("C");
        catalog.Add(c.Plugin, [c.Factory<IWidget>(() => new Widget("c"))]);
        Assert.Equal(["A", "B", "B"], seen);
    }

    [Fact]
    public void Observe_AFailingCallbackForAnExistingExtension_Throws_AndUnsubscribes()
    {
        var catalog = new ExtensionCatalog();
        var a = new FakeOwner("A");
        catalog.Add(a.Plugin, [a.Factory<IWidget>(() => new Widget("a"))]);
        var calls = 0;

        Assert.Throws<FormatException>(() => catalog.Observe<IWidget>(_ => { calls++; throw new FormatException(); }));
        catalog.Add(a.Plugin, [a.Factory<IWidget>(() => new Widget("a2"))]);

        Assert.Equal(1, calls);
    }

    [Fact]
    public void CallbacksAndHandlersThatThrowDuringAnAdd_DontStopIt()
    {
        var catalog = new ExtensionCatalog();
        var seen = new List<string>();
        using var failing = catalog.Observe<IWidget>(_ => throw new FormatException());
        using var working = catalog.Observe<IWidget>(e => seen.Add("observed " + e.Plugin.Id));
        catalog.ExtensionsAdded += (_, _) => throw new FormatException();
        catalog.ExtensionsAdded += (_, e) => seen.Add($"event {e.Plugin.Id} {e.Extensions.Count} {Environment.CurrentManagedThreadId}");
        var a = new FakeOwner("A");

        catalog.Add(a.Plugin, [a.Factory<IWidget>(() => new Widget("a")), a.Factory<IOther>(() => new Other())]);

        Assert.Equal(["observed A", $"event A 2 {Environment.CurrentManagedThreadId}"], seen);
    }

    [Fact]
    public void Observe_ACallbackThatAddsExtensions_GetsThemToo()
    {
        var catalog = new ExtensionCatalog();
        var seen = new List<string>();
        var b = new FakeOwner("B");
        using var subscription = catalog.Observe<IWidget>(e =>
        {
            seen.Add(e.Plugin.Id);
            if (e.Plugin.Id == "A")
            {
                catalog.Add(b.Plugin, [b.Factory<IWidget>(() => new Widget("b"))]);
            }
        });
        var a = new FakeOwner("A");

        catalog.Add(a.Plugin, [a.Factory<IWidget>(() => new Widget("a"))]);

        Assert.Equal(["A", "B"], seen);
    }

    [Fact]
    public void Observe_ACallbackWaitingForAnotherThreadThatAddsExtensions_DoesntDeadlock()
    {
        // Round 2, finding 1: a callback that waits for another thread (e.g. marshals to the UI thread) while that
        // thread adds a plugin blocked forever: the other thread waited to deliver to the same subscription. Now the
        // delivering thread delivers the other thread's extensions after the callback returns.
        var catalog = new ExtensionCatalog();
        var a = new FakeOwner("A");
        var b = new FakeOwner("B");
        var seen = new List<(string Plugin, int Thread)>();
        var waited = true;
        using var subscription = catalog.Observe<IWidget>(e =>
        {
            seen.Add((e.Plugin.Id, Environment.CurrentManagedThreadId));
            if (e.Plugin.Id == "A")
            {
                var other = new Thread(() => catalog.Add(b.Plugin, [b.Factory<IWidget>(() => new Widget("b"))]));
                other.Start();
                waited = other.Join(TimeSpan.FromSeconds(10));
            }
        });

        catalog.Add(a.Plugin, [a.Factory<IWidget>(() => new Widget("a"))]);

        Assert.True(waited, "The other thread's Add waited for the callback that was waiting for it.");
        Assert.Equal(["A", "B"], seen.Select(s => s.Plugin));
        Assert.All(seen, s => Assert.Equal(Environment.CurrentManagedThreadId, s.Thread));
    }

    [Fact]
    public async Task Observe_UnderConcurrentAdds_DeliversEveryExtensionExactlyOnce_NeverConcurrently()
    {
        const int Adders = 8;
        const int BatchesPerAdder = 50;
        const int Observers = 16;
        var catalog = new ExtensionCatalog();
        var start = new ManualResetEventSlim();
        var subscriptions = new ConcurrentBag<(ConcurrentDictionary<Extension, int> Seen, IDisposable Subscription, int[] Overlaps)>();

        var adders = Enumerable.Range(0, Adders).Select(i => Task.Run(() =>
        {
            var owner = new FakeOwner($"P{i}");
            start.Wait();
            for (var batch = 0; batch < BatchesPerAdder; batch++)
            {
                catalog.Add(owner.Plugin, [owner.Factory<IWidget>(() => new Widget("w")), owner.Factory<IWidget>(() => new Widget("w2"))]);
            }
        }));
        var observers = Enumerable.Range(0, Observers).Select(i => Task.Run(() =>
        {
            start.Wait();
            Thread.Sleep(i % 4);
            var seen = new ConcurrentDictionary<Extension, int>();
            var active = 0;
            var overlaps = new int[1];
            var subscription = catalog.Observe<IWidget>(e =>
            {
                if (Interlocked.Increment(ref active) > 1)
                {
                    Interlocked.Increment(ref overlaps[0]);
                }

                seen.AddOrUpdate(e, 1, (_, n) => n + 1);
                Interlocked.Decrement(ref active);
            });
            subscriptions.Add((seen, subscription, overlaps));
        }));

        start.Set();
        await Task.WhenAll(adders.Concat(observers));

        var all = catalog.GetAll<IWidget>();
        Assert.Equal(Adders * BatchesPerAdder * 2, all.Count);
        foreach (var (seen, subscription, overlaps) in subscriptions)
        {
            Assert.Equal(all.Count, seen.Count);
            Assert.All(seen.Values, n => Assert.Equal(1, n));
            Assert.All(all, e => Assert.True(seen.ContainsKey(e)));
            Assert.Equal(0, overlaps[0]);
            subscription.Dispose();
        }
    }

    public sealed record Label(string Text);

    public sealed class Widget(string name) : IWidget
    {
        public string Name { get; } = name;
    }

    public sealed class ParameterlessWidget : IWidget
    {
        public string Name => "parameterless";
    }

    public sealed class Other : IOther;

    private sealed class RecordingActivator(object result) : IExtensionActivator
    {
        public List<ExtensionActivationContext> Contexts { get; } = [];

        public object CreateInstance(ExtensionActivationContext context)
        {
            Contexts.Add(context);
            return result;
        }
    }

    private sealed class FakeOwner(string id, IExtensionActivator? activator = null) : IExtensionOwner
    {
        private readonly List<object> _singletons = [];

        public PluginInfo Plugin { get; } = new(id, NuGetVersion.Parse("1.0.0"), id, PluginStatus.Loaded, null);

        public IServiceProvider Services { get; } = EmptyServiceProvider.Instance;

        public IExtensionActivator Activator { get; } = activator ?? DefaultExtensionActivator.Instance;

        public AssemblyLoadContext? Cell => null;

        public bool IsDisposed { get; set; }

        public List<Exception> Faults { get; } = [];

        /// <summary>Runs when an extension faults, like the host's PluginFaulted handlers.</summary>
        public Action<Exception>? OnFault { get; set; }

        public IReadOnlyList<object> Singletons
        {
            get
            {
                lock (_singletons)
                {
                    return _singletons.ToList();
                }
            }
        }

        public Extension Factory<T>(Func<T> factory, object? metadata = null, ExtensionLifetime lifetime = ExtensionLifetime.Singleton)
            where T : class
        {
            var registrar = new ExtensionRegistrar(this);
            registrar.Add(factory, metadata, lifetime);
            return registrar.Extensions[0];
        }

        /// <summary>Whether the owner refuses new singletons (its host disposed them).</summary>
        public bool SingletonsClosed { get; set; }

        public List<object> Refused { get; } = [];

        public bool SingletonCreated(ExtensionRegistration registration, object instance)
        {
            lock (_singletons)
            {
                if (SingletonsClosed)
                {
                    return false;
                }

                _singletons.Add(instance);
                return true;
            }
        }

        public void DisposeRefused(ExtensionRegistration registration, object instance)
        {
            lock (Refused)
            {
                Refused.Add(instance);
            }
        }

        public void ExtensionFaulted(ExtensionRegistration registration, Exception exception)
        {
            lock (Faults)
            {
                Faults.Add(exception);
            }

            OnFault?.Invoke(exception);
        }
    }
}
