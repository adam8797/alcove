using System.Threading.Channels;
using Alcove.Internal.Hosting;
using Alcove.Internal.IO;
using Alcove.Internal.State;
using Alcove.IntegrationTests.Infrastructure;
using Fixture.HostContracts;

namespace Alcove.IntegrationTests.Install;

/// <summary>
/// Hot installs and loads racing plugin code, other installs, cancellation and disposal (code review 2026-10-19,
/// findings 1, 2 and 6; round 2, findings 2, 3, 5, 6, 8, 9, 10 and 11). The Hook fixture runs a hook the test offers as a host service in its InitializeAsync, and
/// records its disposal in the host's <see cref="IEventLog"/>; HookUser depends on it (so installing HookUser loads
/// both, Hook first).
/// </summary>
public sealed class HotInstallConcurrencyTests(HostingFixtures fixtures) : IDisposable
{
    private const string Hook = "Fixture.Hosting.Hook";
    private const string HookUser = "Fixture.Hosting.HookUser";
    private const string UserA = "Fixture.Hosting.UserA";
    private const string UserB = "Fixture.Hosting.UserB";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    private static readonly Lazy<HostInventory> s_inventory = new(() => HostInventory.FromCurrentProcess([HostingFixtures.ContractsPackage]));

    private readonly PluginRootBuilder _root = new();
    private readonly ListLoggerFactory _logs = new();
    private readonly TestServices _services = new();
    private readonly List<PluginHost> _hosts = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _services.Hook = null;
        foreach (var host in _hosts)
        {
            host.DisposeAsync().AsTask().Wait(Timeout);
        }

        _root.Dispose();
    }

    [Fact]
    public async Task APluginInstallingAnotherFromInitializeAsync_DuringAHotInstall_DoesntDeadlock()
    {
        // Finding 1: the install gate was held across plugin activation, so an install from InitializeAsync waited
        // for itself.
        var host = await LoadedHostAsync();
        InstallResult? nested = null;
        _services.Hook = async (context, ct) => nested = await host.InstallAsync(UserA, ct);

        var result = await host.InstallAsync(Hook, cancellationToken: Ct).WaitAsync(Timeout, Ct);

        Assert.True(result.Status == InstallStatus.Loaded, result.ToString());
        Assert.NotNull(nested);
        Assert.True(nested.Status == InstallStatus.Loaded, nested.ToString());
        Assert.All(host.Plugins, p => Assert.Equal(PluginStatus.Loaded, p.Status));
        Assert.Equal([UserA, Hook], host.Catalog.GetAll<IDescriber>().Select(e => e.Plugin.Id));   // UserA loaded within Hook's InitializeAsync
        Assert.Equal([Hook, UserA], new StateStore(_root.Root).Read().Plugins.Select(p => p.Id));
    }

    [Fact]
    public async Task ANestedInstallDependingOnThePluginInitializing_IsPendingRestart_NotADeadlock()
    {
        // From Hook's InitializeAsync, install HookUser, which depends on Hook: it can't wait for Hook to finish.
        var host = await LoadedHostAsync();
        InstallResult? nested = null;
        _services.Hook = async (context, ct) => nested = await host.InstallAsync(HookUser, ct);

        var result = await host.InstallAsync(Hook, cancellationToken: Ct).WaitAsync(Timeout, Ct);

        Assert.True(result.Status == InstallStatus.Loaded, result.ToString());
        Assert.NotNull(nested);
        Assert.True(nested.Status == InstallStatus.PendingRestart, nested.ToString());
        Assert.Contains($"depends on the plugin {Hook}, which another install is still loading", nested.Reason);
        Assert.Equal(PluginStatus.NotLoaded, Plugin(host, HookUser).Status);
    }

    [Fact]
    public async Task AnInstallDependingOnAPluginAnotherInstallIsStillLoading_WaitsForIt_AndLoads()
    {
        // Installs still activate in the order they merged: the second waits for the first's plugins, and installing
        // the same plugin again reports how its load ended, as when the whole install was serialised.
        var host = await LoadedHostAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _services.Hook = async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
        };

        var first = host.InstallAsync(Hook, cancellationToken: Ct);
        await entered.Task.WaitAsync(Timeout, Ct);
        var second = host.InstallAsync(HookUser, cancellationToken: Ct);
        var again = host.InstallAsync(Hook, cancellationToken: Ct);   // already installed, still loading
        await Task.Delay(300, Ct);
        Assert.False(second.IsCompleted, "The dependent install didn't wait for its dependency to load.");
        Assert.False(again.IsCompleted, "Installing a plugin that's still loading didn't wait for it.");
        release.SetResult();

        var results = await Task.WhenAll(first, second, again).WaitAsync(Timeout, Ct);

        Assert.All(results, r => Assert.True(r.Status == InstallStatus.Loaded, r.ToString()));
        Assert.Equal([Hook, HookUser], host.Catalog.GetAll<IDescriber>().Select(e => e.Plugin.Id));
    }

    [Fact]
    public async Task DisposeDuringLoadAsync_WaitsForTheActivation_ThenDisposesIt_AndLoadsNothingMore()
    {
        // Finding 2: DisposeAsync disposed only what was activated when it started, didn't wait, and deleted the
        // in-use record while plugins were still being loaded.
        await _root.InstallAsync(s_inventory.Value, fixtures.Packages, Hook, UserA);
        var host = Create();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _services.Hook = async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;   // ignores the cancellation: disposal must still wait
        };

        var load = host.LoadAsync(Ct);
        await entered.Task.WaitAsync(Timeout, Ct);
        var dispose = host.DisposeAsync().AsTask();
        await Task.Delay(300, Ct);
        Assert.False(dispose.IsCompleted, "DisposeAsync didn't wait for the plugin being loaded.");
        Assert.NotNull(host.InUseRecordPath);
        Assert.Empty(_services.Events);
        release.SetResult();

        await dispose.WaitAsync(Timeout, Ct);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load.WaitAsync(Timeout, Ct));

        Assert.Equal([$"{Hook} disposed"], _services.Events);
        Assert.Equal(PluginStatus.NotLoaded, Plugin(host, Hook).Status);
        Assert.Equal(PluginStatus.NotLoaded, Plugin(host, UserA).Status);
        Assert.Contains("disposed", Plugin(host, UserA).Reason);
        Assert.Empty(host.CellEnvironment.FindCell(UserA)!.Assemblies);   // never activated
        Assert.Empty(host.Catalog.GetAll<IDescriber>());
        Assert.Null(host.InUseRecordPath);
    }

    [Fact]
    public async Task DisposeDuringAHotInstall_WaitsForIt_AndTheRestOfTheBatchDoesntLoad()
    {
        var host = await LoadedHostAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _services.Hook = async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
        };

        var install = host.InstallAsync(HookUser, cancellationToken: Ct);
        await entered.Task.WaitAsync(Timeout, Ct);
        var dispose = host.DisposeAsync().AsTask();
        await Task.Delay(300, Ct);
        Assert.False(dispose.IsCompleted, "DisposeAsync didn't wait for the install.");
        release.SetResult();
        await dispose.WaitAsync(Timeout, Ct);
        var result = await install.WaitAsync(Timeout, Ct);

        Assert.True(result.Status == InstallStatus.PendingRestart, result.ToString());
        Assert.All(result.Plugins, p => Assert.Equal(PluginStatus.NotLoaded, p.Status));
        Assert.Contains("disposed", Plugin(host, HookUser).Reason);
        Assert.Equal([$"{Hook} disposed"], _services.Events);
        Assert.Empty(host.Catalog.GetAll<IDescriber>());
        Assert.Equal([Hook, HookUser], new StateStore(_root.Root).Read().Plugins.Select(p => p.Id));   // installed: loads at the next startup
    }

    [Fact]
    public async Task CancellingAfterTheStateWrite_ReturnsAResult_AndMarksTheWholeBatch()
    {
        // Finding 6: cancellation during activation threw OperationCanceledException although the install was
        // recorded, and left the rest of the batch "Not loaded yet." without a pending restart.
        var host = await LoadedHostAsync();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        _services.Hook = (_, ct) =>
        {
            cancellation.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };

        var result = await host.InstallAsync(HookUser, cancellationToken: cancellation.Token);

        Assert.True(result.Status == InstallStatus.PendingRestart, result.ToString());
        Assert.Equal([Hook, HookUser], result.Plugins.Select(p => p.Id));
        Assert.All(result.Plugins, p =>
        {
            Assert.Equal(PluginStatus.NotLoaded, p.Status);
            Assert.Contains("cancelled", p.Reason);
        });
        var pending = await host.GetPendingChangesAsync(Ct);
        Assert.Equal([(PendingChangeKind.PendingRestart, Hook), (PendingChangeKind.PendingRestart, HookUser)], pending.Select(c => (c.Kind, c.PluginId)));
        Assert.Empty(host.Catalog.GetAll<IDescriber>());
    }

    [Fact]
    public async Task CancellingRightAfterTheStateWrite_ReturnsPendingRestart_WithoutLoadingAnything()
    {
        var host = await LoadedHostAsync();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var progress = new SyncProgress<InstallProgress>(p =>
        {
            if (p.Step == InstallStep.Loading)
            {
                cancellation.Cancel();
            }
        });

        var result = await host.InstallAsync(UserA, progress: progress, cancellationToken: cancellation.Token);

        Assert.True(result.Status == InstallStatus.PendingRestart, result.ToString());
        Assert.Contains("cancelled after it was recorded", result.Reason);
        Assert.Equal(PluginStatus.NotLoaded, Plugin(host, UserA).Status);
        Assert.Null(host.CellEnvironment.FindCell(UserA));
        Assert.Equal(PendingChangeKind.PendingRestart, Assert.Single(await host.GetPendingChangesAsync(Ct)).Kind);
    }

    [Fact]
    public async Task APluginInstallingAnUnrelatedPluginThroughAnAppWorker_DuringAHotInstall_DoesntDeadlock()
    {
        // Round 2, finding 2: every install waited for every earlier activation. Hook's InitializeAsync asks an app
        // worker (its own thread, so the install's async flow doesn't reach it) to install UserA and awaits it; UserA
        // neither depends on Hook nor joins its cell, so it must not wait for Hook's activation.
        var host = await LoadedHostAsync();
        var requests = Channel.CreateUnbounded<(string Id, TaskCompletionSource<InstallResult> Done)>();
        Thread worker;
        using (ExecutionContext.SuppressFlow())
        {
            worker = new Thread(() =>
            {
                while (requests.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
                {
                    while (requests.Reader.TryRead(out var request))
                    {
                        try
                        {
                            request.Done.SetResult(host.InstallAsync(request.Id, CancellationToken.None).GetAwaiter().GetResult());
                        }
                        catch (Exception ex)
                        {
                            request.Done.SetException(ex);
                        }
                    }
                }
            }) { IsBackground = true, Name = "App install worker" };
            worker.Start();
        }

        try
        {
            InstallResult? nested = null;
            _services.Hook = async (_, ct) =>
            {
                var done = new TaskCompletionSource<InstallResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                await requests.Writer.WriteAsync((UserA, done), ct);
                nested = await done.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
            };

            var result = await host.InstallAsync(Hook, cancellationToken: Ct).WaitAsync(Timeout, Ct);

            Assert.True(result.Status == InstallStatus.Loaded, result.ToString());
            Assert.NotNull(nested);
            Assert.True(nested.Status == InstallStatus.Loaded, nested.ToString());
            Assert.Equal([UserA, Hook], host.Catalog.GetAll<IDescriber>().Select(e => e.Plugin.Id));
        }
        finally
        {
            requests.Writer.Complete();
            worker.Join(Timeout);
        }
    }

    [Fact]
    public async Task AnInstallWaitingForADependencyWhoseActivationIsCancelled_IsPendingRestart_NotFaulted()
    {
        // Round 2, finding 3: HookUser waits for Hook's activation, which is cancelled; HookUser then isn't loaded
        // without a fault, so the install is PendingRestart (and recorded as such), not Faulted.
        var host = await LoadedHostAsync();
        using var hookCancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _services.Hook = async (_, ct) =>
        {
            entered.TrySetResult();
            await release.Task;
            ct.ThrowIfCancellationRequested();
        };

        var first = host.InstallAsync(Hook, cancellationToken: hookCancellation.Token);
        await entered.Task.WaitAsync(Timeout, Ct);
        var deciding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = host.InstallAsync(HookUser, progress: new SyncProgress<InstallProgress>(p =>
        {
            if (p.Step == InstallStep.Loading)
            {
                deciding.TrySetResult();   // the hot-load decision follows synchronously: it waits for Hook's activation
            }
        }), cancellationToken: Ct);
        await deciding.Task.WaitAsync(Timeout, Ct);
        hookCancellation.Cancel();
        release.SetResult();

        var hook = await first.WaitAsync(Timeout, Ct);
        var user = await second.WaitAsync(Timeout, Ct);

        Assert.True(hook.Status == InstallStatus.PendingRestart, hook.ToString());
        Assert.True(user.Status == InstallStatus.PendingRestart, user.ToString());
        Assert.Contains($"Its plugin dependency {Hook} isn't loaded", Plugin(host, HookUser).Reason);
        var pending = await host.GetPendingChangesAsync(Ct);
        Assert.Equal([(PendingChangeKind.PendingRestart, Hook), (PendingChangeKind.PendingRestart, HookUser)],
            pending.Select(c => (c.Kind, c.PluginId)).Order());
    }

    [Fact]
    public async Task DisposeWhileTheOnlyPluginOfAnInstallInitializes_IsPendingRestart_NotFaulted()
    {
        // Round 2, finding 3: InitializeAsync ignores the cancellation and completes; the plugin is NotLoaded
        // (disposed), not faulted.
        var host = await LoadedHostAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _services.Hook = async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
        };

        var install = host.InstallAsync(Hook, cancellationToken: Ct);
        await entered.Task.WaitAsync(Timeout, Ct);
        var dispose = host.DisposeAsync().AsTask();
        release.SetResult();
        await dispose.WaitAsync(Timeout, Ct);
        var result = await install.WaitAsync(Timeout, Ct);

        Assert.True(result.Status == InstallStatus.PendingRestart, result.ToString());
        Assert.Contains("disposed", Assert.Single(result.Plugins).Reason);
    }

    [Fact]
    public async Task DisposeFromAFireAndForgetTaskOfPluginCode_StillDisposesWhatTheLoadCreatesAfterwards()
    {
        // Round 2, finding 5: a task started from the load's flow (here, by the cell services factory) is treated as
        // part of the load, so DisposeAsync doesn't wait for it; what the load created afterwards (Hook's entry point
        // and its cell's provider) leaked.
        await _root.InstallAsync(s_inventory.Value, fixtures.Packages, Hook);
        PluginHost? host = null;
        var factory = new LateFactory(_services, () =>
        {
            var dispose = Task.Run(() => host!.DisposeAsync().AsTask());
            Assert.True(dispose.Wait(Timeout, Ct), "DisposeAsync waited for the load it was started from.");
        });
        host = Create(o => o.CellServicesFactory = factory);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.LoadAsync(Ct).WaitAsync(Timeout, Ct));

        Assert.NotNull(factory.Provider);
        Assert.True(factory.Provider.IsDisposed, "The cell's provider created after the disposal started wasn't disposed.");
        Assert.Equal([$"{Hook} disposed"], _services.Events);
        Assert.Equal(PluginStatus.NotLoaded, Plugin(host, Hook).Status);
    }

    [Fact]
    public async Task ASecondDisposeAsync_CompletesOnlyWhenTheDisposalHasFinished()
    {
        // Round 2, finding 6.
        await _root.InstallAsync(s_inventory.Value, fixtures.Packages, Hook);
        var host = Create();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _services.Hook = async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;   // ignores the cancellation: the disposal waits for it
        };

        var load = host.LoadAsync(Ct);
        await entered.Task.WaitAsync(Timeout, Ct);
        var first = host.DisposeAsync().AsTask();
        var second = host.DisposeAsync().AsTask();
        await Task.Delay(300, Ct);
        Assert.False(second.IsCompleted, "A second DisposeAsync returned while the first was still disposing.");
        release.SetResult();

        await Task.WhenAll(first, second).WaitAsync(Timeout, Ct);
        Assert.Equal([$"{Hook} disposed"], _services.Events);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load.WaitAsync(Timeout, Ct));
    }

    [Fact]
    public async Task DisposeAsyncFromPluginCodeOfALoadThatTheDisposalWaitsFor_DoesntDeadlock()
    {
        await _root.InstallAsync(s_inventory.Value, fixtures.Packages, Hook);
        var host = Create();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _services.Hook = async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            await host.DisposeAsync();   // the disposal below waits for this load: this call can't wait for it
        };

        var load = host.LoadAsync(Ct);
        await entered.Task.WaitAsync(Timeout, Ct);
        var dispose = host.DisposeAsync().AsTask();
        release.SetResult();

        await dispose.WaitAsync(Timeout, Ct);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load.WaitAsync(Timeout, Ct));
        Assert.Equal([$"{Hook} disposed"], _services.Events);
    }

    [Fact]
    public async Task DisposeFromTheLastPluginsInitializeAsync_FailsLoadAsync_AndSkipsTheCleanup()
    {
        // Round 2, finding 8: LoadAsync completed normally after disposal, and deleted unused packages.
        await _root.InstallAsync(s_inventory.Value, fixtures.Packages, Hook);
        var orphan = Directory.CreateDirectory(Path.Combine(_root.Root, "packages", "fixture.orphan", "1.0.0")).FullName;
        File.WriteAllText(Path.Combine(orphan, "leftover.txt"), "unused");
        var host = Create();
        _services.Hook = async (_, _) => await host.DisposeAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.LoadAsync(Ct).WaitAsync(Timeout, Ct));

        Assert.True(Directory.Exists(orphan), "LoadAsync ran the cleanup after the host was disposed.");
        Assert.Equal(PluginStatus.NotLoaded, Plugin(host, Hook).Status);
    }

    [Fact]
    public async Task ASingletonCreatedWhileTheHostIsDisposed_IsDisposed_AndNotHandedOut()
    {
        // Round 2, finding 9: the singleton finished construction after the disposal had disposed its plugin.
        var activator = new BlockingActivator();
        await _root.InstallAsync(s_inventory.Value, fixtures.Packages, Hook);
        var host = Create(o => o.Activator = activator);
        await host.LoadAsync(Ct);
        var extension = Assert.Single(host.Catalog.GetAll<IDescriber>());

        var get = Task.Run(() => extension.Value);
        await activator.Entered.Task.WaitAsync(Timeout, Ct);
        await host.DisposeAsync().AsTask().WaitAsync(Timeout, Ct);
        activator.Release.Set();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => get.WaitAsync(Timeout, Ct));
        Assert.NotNull(activator.Created);
        Assert.True(activator.Created.IsDisposed, "The singleton created during disposal wasn't disposed.");
    }

    [Fact]
    public async Task TheInitializationToken_FollowsTheAppsTokenDuringTheCall_AndTheHostsDisposalAfterIt()
    {
        // Round 2, finding 10 (as revised in round 3): during the call the app's token cancels the plugin's token;
        // after the call only disposal does, so background work tied to it still stops when the host is disposed.
        await _root.InstallAsync(s_inventory.Value, fixtures.Packages, Hook);
        using var app = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var cancelledDuringTheCall = false;
        _services.Hook = (_, ct) =>
        {
            app.Cancel();
            cancelledDuringTheCall = ct.IsCancellationRequested;
            return Task.CompletedTask;
        };
        try
        {
            await Create().LoadAsync(app.Token);
        }
        catch (OperationCanceledException)
        {
            // Either way: the plugin's token followed the app's.
        }

        Assert.True(cancelledDuringTheCall, "The app's token didn't cancel the plugin's token during the call.");

        var tokens = new List<CancellationToken>();
        _services.Hook = (_, ct) =>
        {
            tokens.Add(ct);
            return Task.CompletedTask;
        };
        var host = Create();
        await host.LoadAsync(CancellationToken.None);   // not cancellable: the token follows disposal only
        Assert.False(Assert.Single(tokens).IsCancellationRequested);

        await host.DisposeAsync();

        Assert.True(tokens[0].IsCancellationRequested, "Disposing the host didn't cancel the plugin's token.");
    }

    [Fact]
    public async Task CancellingTheAppsToken_AfterTheCallReturned_DoesntCancelThePluginsToken()
    {
        // Round 3, finding 1: the plugin's token stayed linked to the app's per-call token (e.g. a 30 s timeout) after
        // the call returned.
        await _root.InstallAsync(s_inventory.Value, fixtures.Packages, Hook);
        CancellationToken token = default;
        _services.Hook = (_, ct) =>
        {
            token = ct;
            return Task.CompletedTask;
        };
        using var app = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var host = Create();
        await host.LoadAsync(app.Token);

        app.Cancel();
        Assert.False(token.IsCancellationRequested, "Cancelling the app's token after LoadAsync returned cancelled the plugin's token.");

        await host.DisposeAsync();
        Assert.True(token.IsCancellationRequested, "Disposing the host didn't cancel the plugin's token.");
    }

    [Fact]
    public async Task AnInstallDecidedWhileLoadAsyncRan_ThatListsAfterLoadAsyncLoadedItsPlugin_DoesntListItTwice()
    {
        // Round 3, finding 2: the install read "LoadAsync is running", LoadAsync then loaded its plugin and ended, and
        // only then did the install list its plugin (again, NotLoaded) and record a pending restart.
        await _root.InstallAsync(s_inventory.Value, fixtures.Packages, Hook, UserB);
        Assert.Equal(ChangeStatus.Staged, (await Create().DisableAsync(UserB, Ct)).Status);   // so LoadAsync waits for the root lock
        var host = Create();
        var blocked = new ManualResetEventSlim();
        var resume = new ManualResetEventSlim();
        Task? load = null;
        _logs.Observer = entry =>
        {
            if (entry.EventId.Id == 3020 && entry.Message.Contains(UserA, StringComparison.Ordinal) && !blocked.IsSet)
            {
                blocked.Set();   // the install holds the root lock, before writing the state
                resume.Wait(Timeout);
            }
        };
        var progress = new SyncProgress<InstallProgress>(p =>
        {
            if (p.Step == InstallStep.Loading)
            {
                load!.Wait(Timeout);   // decided "LoadAsync is running"; let it load the plugin and end first
            }
        });

        try
        {
            var install = Task.Run(() => host.InstallAsync(UserA, progress: progress, cancellationToken: Ct), Ct);
            Assert.True(blocked.Wait(Timeout, Ct));
            load = Task.Run(() => host.LoadAsync(Ct), Ct);
            await Task.Delay(300, Ct);   // LoadAsync is running, waiting for the root lock
            resume.Set();
            var result = await install.WaitAsync(Timeout, Ct);
            await load.WaitAsync(Timeout, Ct);

            Assert.True(result.Status == InstallStatus.PendingRestart, result.ToString());
            var plugin = Assert.Single(host.Plugins, p => p.Id == UserA);
            Assert.Equal(PluginStatus.Loaded, plugin.Status);
            Assert.DoesNotContain(await host.GetPendingChangesAsync(Ct), c => c.PluginId == UserA);
        }
        finally
        {
            _logs.Observer = null;
            resume.Set();
        }
    }

    [Fact]
    public async Task AnInstallDuringLoadAsync_ThatLoadAsyncDidntSee_IsListedInPlugins()
    {
        // Round 2, finding 11: the PendingRestart plugin was missing from Plugins.
        await _root.InstallAsync(s_inventory.Value, fixtures.Packages, Hook);
        var host = Create();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _services.Hook = async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
        };

        var load = host.LoadAsync(Ct);
        await entered.Task.WaitAsync(Timeout, Ct);
        var result = await host.InstallAsync(UserA, cancellationToken: Ct).WaitAsync(Timeout, Ct);
        release.SetResult();
        await load.WaitAsync(Timeout, Ct);

        Assert.True(result.Status == InstallStatus.PendingRestart, result.ToString());
        Assert.Equal([Hook, UserA], host.Plugins.Select(p => p.Id));
        Assert.Equal(PluginStatus.NotLoaded, Plugin(host, UserA).Status);
        Assert.Contains("LoadAsync was running", Plugin(host, UserA).Reason);
        var change = Assert.Single(await host.GetPendingChangesAsync(Ct));
        Assert.Equal((PendingChangeKind.PendingRestart, UserA), (change.Kind, change.PluginId));
    }

    [Fact]
    public async Task AnInstallDuringLoadAsync_ThatLoadAsyncLoaded_IsntPendingARestart()
    {
        // Round 2, finding 11: the install wrote the state before LoadAsync read it, so LoadAsync loaded the plugin;
        // the install (deciding while LoadAsync ran) still recorded it as pending a restart.
        await _root.InstallAsync(s_inventory.Value, fixtures.Packages, Hook, UserB);
        Assert.Equal(ChangeStatus.Staged, (await Create().DisableAsync(UserB, Ct)).Status);   // so LoadAsync waits for the root lock
        var host = Create();
        var blocked = new ManualResetEventSlim();
        var resume = new ManualResetEventSlim();
        _logs.Observer = entry =>
        {
            if (entry.EventId.Id == 3020 && entry.Message.Contains(UserA, StringComparison.Ordinal) && !blocked.IsSet)
            {
                blocked.Set();   // the install holds the root lock, before writing the state
                resume.Wait(Timeout);
            }
        };

        try
        {
            var install = Task.Run(() => host.InstallAsync(UserA, cancellationToken: Ct), Ct);
            Assert.True(blocked.Wait(Timeout, Ct));
            var load = Task.Run(() => host.LoadAsync(Ct), Ct);
            await Task.Delay(300, Ct);   // LoadAsync is running, waiting for the root lock
            resume.Set();
            var result = await install.WaitAsync(Timeout, Ct);
            await load.WaitAsync(Timeout, Ct);

            Assert.True(result.Status == InstallStatus.PendingRestart, result.ToString());
            Assert.Equal(PluginStatus.Loaded, Plugin(host, UserA).Status);
            Assert.Single(host.Plugins, p => p.Id == UserA);
            Assert.DoesNotContain(await host.GetPendingChangesAsync(Ct), c => c.PluginId == UserA);
        }
        finally
        {
            _logs.Observer = null;
            resume.Set();
        }
    }

    private async Task<PluginHost> LoadedHostAsync()
    {
        var host = Create();
        await host.LoadAsync(Ct);
        return host;
    }

    private PluginHost Create(Action<PluginHostOptions>? configure = null)
    {
        var options = new PluginHostOptions
        {
            Root = _root.Root,
            PluginPackageType = "FixturePlugin",
            HostOwnedPackages = { HostingFixtures.ContractsPackage },
            Feeds = { new PackageFeed(fixtures.PackagesDirectory) },
            LoggerFactory = _logs,
            Services = _services,
        };
        configure?.Invoke(options);
        var host = PluginHost.Create(options);
        _hosts.Add(host);
        return host;
    }

    private static PluginInfo Plugin(PluginHost host, string id) => host.Plugins.Single(p => p.Id == id);

    /// <summary>The host services the Hook fixture uses: its hook, and an event log.</summary>
    private sealed class TestServices : IServiceProvider, IEventLog
    {
        private readonly List<string> _events = [];

        public Func<IPluginContext, CancellationToken, Task>? Hook { get; set; }

        public IReadOnlyList<string> Events
        {
            get
            {
                lock (_events)
                {
                    return _events.ToList();
                }
            }
        }

        public object? GetService(Type serviceType) =>
            serviceType == typeof(Func<IPluginContext, CancellationToken, Task>) ? Hook
            : serviceType == typeof(IEventLog) ? this
            : null;

        public void Write(string entry)
        {
            lock (_events)
            {
                _events.Add(entry);
            }
        }
    }

    /// <summary>A cell services factory that runs an action while it creates a cell's provider.</summary>
    private sealed class LateFactory(IServiceProvider services, Action onCreate) : ICellServicesFactory
    {
        public TrackingProvider? Provider { get; private set; }

        public IReadOnlyCollection<string> ServicePluginInterfaces { get; } = [];

        public IServiceProvider CreateServices(CellServicesContext context)
        {
            onCreate();
            return Provider = new TrackingProvider(services);
        }
    }

    private sealed class TrackingProvider(IServiceProvider inner) : IServiceProvider, IDisposable
    {
        public bool IsDisposed { get; private set; }

        public object? GetService(Type serviceType) => inner.GetService(serviceType);

        public void Dispose() => IsDisposed = true;
    }

    /// <summary>Blocks creating an extension until released, then creates a disposable one.</summary>
    private sealed class BlockingActivator : IExtensionActivator
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ManualResetEventSlim Release { get; } = new();

        public DisposableDescriber? Created { get; private set; }

        public object CreateInstance(ExtensionActivationContext context)
        {
            Entered.TrySetResult();
            Release.Wait(Timeout);
            return Created = new DisposableDescriber();
        }
    }

    private sealed class DisposableDescriber : IDescriber, IDisposable
    {
        public bool IsDisposed { get; private set; }

        public string Describe() => "disposable";

        public void Dispose() => IsDisposed = true;
    }

    /// <summary>Reports synchronously (<see cref="Progress{T}"/> posts to the thread pool).</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
