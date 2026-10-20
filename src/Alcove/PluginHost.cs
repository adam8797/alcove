using System.Reflection;
using Alcove.Catalog;
using Alcove.Cells;
using Alcove.Feeds;
using Alcove.Hosting;
using Alcove.Install;
using Alcove.Loading;
using Alcove.Storage;
using Alcove.Trust;
using Alcove.Validation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NuGet.Versioning;

namespace Alcove;

/// <summary>
/// Loads the plugins installed in a plugin root into cells and collects their extensions in the
/// <see cref="Catalog"/>. Several hosts can exist in one process; each has its own cells.
/// </summary>
/// <remarks>
/// <see cref="LoadAsync"/> always validates first and never contacts a feed. A plugin that faults doesn't stop
/// the others; the library reports it (<see cref="Plugins"/>, <see cref="PluginFaulted"/>) and never disables it.
/// Plugin code (<see cref="IPlugin.InitializeAsync"/>, disposal) continues on the caller's synchronization context,
/// so a UI app that awaits <see cref="LoadAsync"/> on its UI thread initialises plugins there.
/// <see cref="InstallAsync(string, VersionRange?, bool?, IProgress{InstallProgress}?, CancellationToken)"/> installs from the feeds and loads the new plugins at once when it can (hot install).
/// </remarks>
public sealed partial class PluginHost : IAsyncDisposable
{
    private readonly PluginHostOptions _options;
    private readonly HostInventory _inventory;
    private readonly PackageLayout _layout;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly IExtensionActivator _activator;
    private readonly IServiceProvider _services;
    private readonly CellEnvironment _cells;
    private readonly CellServiceProviders _cellServices;
    private readonly IReadOnlyList<ICellObserver> _cellObservers;
    private readonly Lock _gate = new();
    private readonly List<PluginRuntime> _activated = [];
    private readonly SemaphoreSlim _installGate = new(1, 1);   // an install's resolve, write and merge; not its activation
    private readonly Lazy<FeedConfiguration> _feeds;
    private readonly TrustVerdicts _trustVerdicts = new();

    // In-flight LoadAsync and InstallAsync calls (guarded by _gate): DisposeAsync cancels them and waits for them,
    // except the ones it was called from (_notAwaited), which sweep what they created when they end.
    private readonly CancellationTokenSource _disposing = new();
    private readonly List<HostOperation> _operations = [];
    private readonly AsyncLocal<HostOperation?> _currentOperation = new();
    private readonly HashSet<HostOperation> _awaited = [];
    private readonly HashSet<HostOperation> _notAwaited = [];

    // The token sources of operations whose app token can be cancelled (guarded by _gate). Each is linked to
    // _disposing for the host's lifetime (plugins may tie background work to the token they got in InitializeAsync),
    // and to the app's token only while its call runs. One cancelled during its call is disposed when the call ends.
    private readonly List<CancellationTokenSource> _operationTokens = [];

    // Serialises disposal passes (the disposal's and those of calls it didn't wait for), so each disposes in order.
    private readonly SemaphoreSlim _sweepGate = new(1, 1);

    // The disposal (guarded by _gate; set once), and whether this async flow runs it (plugin disposal code).
    private TaskCompletionSource? _disposal;
    private readonly AsyncLocal<bool> _inDisposal = new();

    // Plugins installed while LoadAsync ran whose install couldn't load them (guarded by _gate): listed when it ends.
    private readonly List<PluginInfo> _installedDuringLoad = [];
    private IReadOnlySet<string> _loadedIds = new HashSet<string>();   // the plugins LoadAsync planned (guarded by _gate)

    // Hot installs activating their plugins, in the order they merged (guarded by _gate); and the one whose plugin
    // code is running in this async flow (an install started from it is nested: it doesn't wait for anything).
    private readonly List<InstallActivation> _activations = [];
    private readonly AsyncLocal<InstallActivation?> _currentActivation = new();
    private IPackageVerifier? _verifier;
    private IReadOnlyList<PluginInfo> _plugins = [];
    private int _loadStarted;
    private int _loadState;   // LoadNotStarted, Loading or Loaded; written under _gate
    private volatile bool _disposed;

    private const int LoadNotStarted = 0;
    private const int Loading = 1;
    private const int Loaded = 2;

    private PluginHost(PluginHostOptions options, HostInventory inventory)
    {
        _options = options;
        _inventory = inventory;
        _layout = new PackageLayout(options.Root);
        _loggerFactory = options.LoggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger("Alcove.PluginHost");
        _activator = options.Activator ?? DefaultExtensionActivator.Instance;
        _services = options.Services ?? EmptyServiceProvider.Instance;
        _cells = new CellEnvironment(inventory, _loggerFactory);
        _cellServices = new CellServiceProviders(options.CellServicesFactory, _services, _logger, HostAssemblyFiles.FromCurrentProcess(inventory));
        _cellObservers = CreateObservers(options.CellObservers);
        if (_cellObservers.Count > 0)
        {
            _cells.AddCellCreatedCallback(cell => NotifyObservers(cell, "CellCreated", o => o.CellCreated(cell.Info)));
        }

        _feeds = new Lazy<FeedConfiguration>(() => FeedConfiguration.Create(options));
        Catalog = new ExtensionCatalog(_loggerFactory);
    }

    /// <summary>Raised when a plugin faults (at load, or later when one of its extensions can't be created), on the thread where it happened.</summary>
    public event EventHandler<PluginFaultedEventArgs>? PluginFaulted;

    /// <summary>The extensions of every loaded plugin.</summary>
    public ExtensionCatalog Catalog { get; }

    /// <summary>
    /// Every installed plugin with its status, in state file order. Empty until <see cref="LoadAsync"/> runs.
    /// </summary>
    public IReadOnlyList<PluginInfo> Plugins => Volatile.Read(ref _plugins);

    internal bool IsDisposed => _disposed;

    internal CellEnvironment CellEnvironment => _cells;

    /// <summary>
    /// Applies <see cref="PluginHostOptions.TrustPolicy"/> during installs (created on first use). Tests replace it.
    /// </summary>
    internal IPackageVerifier Verifier
    {
        get => _verifier ??= new PackageVerifier(_options.TrustPolicy, _loggerFactory.CreateLogger("Alcove.Trust"));
        set => _verifier = value;
    }

    /// <summary>Creates a host. Nothing is read or loaded until <see cref="ValidateAsync"/> or <see cref="LoadAsync"/>.</summary>
    /// <param name="options">The configuration; the object passed is final.</param>
    /// <returns>The host.</returns>
    /// <exception cref="ArgumentException">The options have no (absolute) <see cref="PluginHostOptions.Root"/>.</exception>
    /// <exception cref="UnsupportedHostException">The host app is single-file or has no <c>deps.json</c>, so Alcove can't tell what it provides.</exception>
    public static PluginHost Create(PluginHostOptions options) => Create(options, HostEnvironment.Current);

    /// <summary>Creates a host for a described process (tests: e.g. a single-file host).</summary>
    internal static PluginHost Create(PluginHostOptions options, HostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);
        options.EnsureValid();
        return new PluginHost(options, HostInventory.Create(environment, options.HostOwnedPackages));
    }

    /// <summary>Creates a host over a given inventory (tests).</summary>
    internal static PluginHost Create(PluginHostOptions options, HostInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(inventory);
        options.EnsureValid();
        return new PluginHost(options, inventory);
    }

    /// <summary>
    /// Validates the plugin root without loading anything: state file against the disk, manifests, the plugin
    /// dependency graph, cell rules, host compatibility, exposed-dependency conflicts, WPF and native duplicates.
    /// Local only; never uses the network.
    /// </summary>
    /// <param name="cancellationToken">Cancels validation.</param>
    /// <returns>The report.</returns>
    public Task<ValidationReport> ValidateAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<ValidationReport>(cancellationToken);
        }

        // Validation is local file I/O; it runs on the caller's thread (the core never moves work to the thread pool).
        try
        {
            return Task.FromResult(CreateValidator().Validate(cancellationToken).Report);
        }
        catch (OperationCanceledException ex) when (ex.CancellationToken == cancellationToken)
        {
            return Task.FromCanceled<ValidationReport>(cancellationToken);
        }
        catch (Exception ex)
        {
            return Task.FromException<ValidationReport>(ex);
        }
    }

    /// <summary>
    /// Validates, then loads every enabled plugin that validation lets load, dependencies first: loads its
    /// assemblies into its cell, calls its entry point's <see cref="IPlugin.Register"/> and
    /// <see cref="IPlugin.InitializeAsync"/> (or scans its own assemblies for <see cref="ExtensionAttribute"/>
    /// types), and adds its extensions to the catalog. Never contacts a feed. Can be called once per host.
    /// </summary>
    /// <param name="cancellationToken">Cancels loading; plugins not loaded yet stay <see cref="PluginStatus.NotLoaded"/>.</param>
    /// <returns>A task that completes when every plugin was loaded, faulted or skipped.</returns>
    /// <exception cref="PluginException">
    /// Validation found a global error (e.g. an unreadable state file; see <see cref="PluginException.Report"/>), or a
    /// plugin faulted while <see cref="PluginHostOptions.FailFast"/> is set.
    /// </exception>
    /// <exception cref="InvalidOperationException"><see cref="LoadAsync"/> already ran.</exception>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Interlocked.CompareExchange(ref _loadStarted, 1, 0) != 0)
        {
            throw new InvalidOperationException("LoadAsync can only be called once per PluginHost. Create a new host after a restart.");
        }

        HostOperation operation;
        try
        {
            operation = BeginOperation(cancellationToken);
        }
        catch
        {
            Volatile.Write(ref _loadStarted, 0);
            throw;
        }

        try
        {
            await LoadCoreAsync(operation.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested && ex.CancellationToken != cancellationToken)
        {
            throw new OperationCanceledException(ex.Message, ex, cancellationToken);   // the caller's token, not the linked one
        }
        finally
        {
            await EndOperationAsync(operation).ConfigureAwait(true);
        }
    }

    /// <summary><see cref="LoadAsync"/>, with a token that <see cref="DisposeAsync"/> cancels too.</summary>
    private async Task LoadCoreAsync(CancellationToken cancellationToken)
    {
        // From here on, an install can't know whether this load reads its state: it reports PendingRestart.
        lock (_gate)
        {
            _loadState = Loading;
        }

        LoadPlan plan;
        try
        {
            await ApplyStagedChangesAsync(cancellationToken).ConfigureAwait(true);
            plan = CreateValidator().Validate(cancellationToken);
            if (plan.Report.HasGlobalErrors)
            {
                throw new PluginException(
                    $"Nothing can be loaded from '{_layout.Root}': {string.Join(" ", plan.Report.Errors.Where(e => e.IsGlobal).Select(e => e.Message))}",
                    pluginId: null,
                    innerException: null,
                    plan.Report);
            }
        }
        catch
        {
            // Nothing was loaded, so the app may fix the root and try again.
            lock (_gate)
            {
                _loadState = LoadNotStarted;
            }

            Volatile.Write(ref _loadStarted, 0);
            throw;
        }

        RememberLoaded(plan, startup: true);
        var infos = plan.Plugins.ToDictionary(
            p => p.Id,
            p => new PluginInfo(p.Id, p.State.Version, p.Cell, p.Status, p.Reason ?? "Not loaded yet."),
            StringComparer.OrdinalIgnoreCase);
        Volatile.Write(ref _plugins, plan.Plugins.Select(p => infos[p.Id]).ToList());
        foreach (var blocked in plan.Plugins.Where(p => p.Status == PluginStatus.NotLoaded && p.Reason is not null))
        {
            HostLog.NotLoaded(_logger, blocked.Id, blocked.Reason!);
        }

        HostLog.Loading(_logger, plan.LoadOrder.Count, _layout.Root);
        try
        {
            await LoadPlannedAsync(plan, infos, cancellationToken).ConfigureAwait(true);

            // Disposed meanwhile (e.g. from the last plugin's InitializeAsync): the load ends as cancelled, without the cleanup.
            if (_disposed)
            {
                throw new OperationCanceledException(CancelledReason, cancellationToken);
            }
        }
        finally
        {
            FinishLoad(plan);
        }

        DeleteUnusedPackages();
        var statuses = Plugins.Select(p => p.Status).ToList();
        HostLog.LoadFinished(_logger,
            statuses.Count(s => s == PluginStatus.Loaded),
            statuses.Count(s => s == PluginStatus.Faulted),
            statuses.Count(s => s == PluginStatus.NotLoaded),
            statuses.Count(s => s == PluginStatus.Disabled));
    }

    /// <summary>
    /// The end of <see cref="LoadAsync"/>: installs from now on load at once. Installs decided while it ran couldn't know
    /// whether it read their state: the plugins it planned are its own (no pending restart); the others are listed.
    /// </summary>
    private void FinishLoad(LoadPlan plan)
    {
        var planned = plan.Plugins.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            _loadState = Loaded;
            _loadedIds = planned;
            foreach (var id in planned)
            {
                _pendingRestart.TryRemove(id, out _);
            }

            var listed = _plugins.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missing = _installedDuringLoad.Where(i => !planned.Contains(i.Id) && listed.Add(i.Id)).ToList();
            _installedDuringLoad.Clear();
            if (missing.Count > 0)
            {
                Volatile.Write(ref _plugins, _plugins.Concat(missing).ToList());
            }
        }
    }

    /// <summary>
    /// Lists the plugins of an install that can't load in this process, and records them as pending a restart, in one
    /// step with <see cref="FinishLoad"/>'s: while LoadAsync runs, they wait for it to end; after it ended, the plugins
    /// it planned are its own (loaded or not by it, never pending a restart), and a plugin already listed isn't added.
    /// </summary>
    private void ListPendingRestart(IEnumerable<PluginInfo> infos, IReadOnlyList<string> reasons)
    {
        lock (_gate)
        {
            if (_loadState != Loaded)
            {
                _installedDuringLoad.AddRange(infos);   // FinishLoad lists them, unless LoadAsync planned them
                RecordPendingRestart(infos.Select(i => i.Id), reasons);   // FinishLoad drops the ones it planned
                return;
            }

            var listed = _plugins.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var added = infos.Where(i => !_loadedIds.Contains(i.Id) && listed.Add(i.Id)).ToList();
            if (added.Count > 0)
            {
                Volatile.Write(ref _plugins, _plugins.Concat(added).ToList());
            }

            RecordPendingRestart(added.Select(i => i.Id), reasons);
        }
    }

    private async Task LoadPlannedAsync(LoadPlan plan, Dictionary<string, PluginInfo> infos, CancellationToken cancellationToken)
    {
        _cells.CreateCells(plan.Cells);
        var cells = plan.Cells.ToDictionary(c => c.Cell, StringComparer.Ordinal);
        var batch = plan.LoadOrder.Select(p => (p, infos[p.Id])).ToList();

        for (var i = 0; i < plan.LoadOrder.Count; i++)
        {
            var planned = plan.LoadOrder[i];
            var info = infos[planned.Id];
            if (cancellationToken.IsCancellationRequested)
            {
                MarkRemaining(plan.LoadOrder.Skip(i), infos, CancelledReason);
                cancellationToken.ThrowIfCancellationRequested();
            }

            var unavailable = planned.PluginDependencies.FirstOrDefault(d => !infos.TryGetValue(d, out var dependency) || dependency.Status != PluginStatus.Loaded);
            if (unavailable is not null)
            {
                var reason = $"Its plugin dependency {unavailable} is {(infos.TryGetValue(unavailable, out var d) ? d.Status : PluginStatus.NotLoaded)}.";
                info.SetNotLoaded(reason);
                HostLog.NotLoaded(_logger, planned.Id, reason);
                continue;
            }

            try
            {
                // Plugin code runs here: keep the caller's context (architecture §9), hence no ConfigureAwait(false).
                await ActivateAsync(planned, info, cells[planned.Cell!].Plan, batch, cancellationToken).ConfigureAwait(true);
            }
            catch (PluginException) when (_options.FailFast)
            {
                MarkRemaining(plan.LoadOrder.Skip(i + 1), infos, $"Loading stopped (FailFast) after {planned.Id} faulted.");
                throw;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                MarkRemaining(plan.LoadOrder.Skip(i), infos, CancelledReason);
                throw;
            }
        }
    }

    /// <summary>
    /// Searches the feeds for plugin packages: only packages with <see cref="PluginHostOptions.PluginPackageType"/>
    /// (when set), the latest version of each, with display metadata and a compatibility flag. Incompatible results
    /// are flagged (<see cref="PluginSearchResult.IsCompatible"/>), not hidden. A feed that fails is logged and skipped.
    /// </summary>
    /// <param name="query">The search terms; empty lists everything.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>The results, in the feeds' order.</returns>
    public Task<IReadOnlyList<PluginSearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default) =>
        SearchAsync(query, new SearchOptions(), cancellationToken);

    /// <summary>Searches the feeds for plugin packages, with paging and prerelease options.</summary>
    /// <param name="query">The search terms; empty lists everything.</param>
    /// <param name="options">Paging and prerelease.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>The results, in the feeds' order.</returns>
    /// <exception cref="InvalidOperationException">The <see cref="PluginHostOptions.NuGetConfigPath"/> file can't be read.</exception>
    /// <seealso cref="SearchAsync(string, CancellationToken)"/>
    public async Task<IReadOnlyList<PluginSearchResult>> SearchAsync(string query, SearchOptions options, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(options);
        return await CreateSearch().SearchAsync(query ?? "", options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Every version of a package on the feeds, ascending: stable versions, plus prerelease ones when
    /// <see cref="PluginHostOptions.AllowPrerelease"/> is set. A feed that fails is logged and skipped.
    /// </summary>
    /// <param name="packageId">The package ID.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The versions.</returns>
    public async Task<IReadOnlyList<NuGetVersion>> GetVersionsAsync(string packageId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        return await CreateSearch().GetVersionsAsync(packageId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Installs the latest version of a plugin; see <see cref="InstallAsync(string, VersionRange?, bool?, IProgress{InstallProgress}?, CancellationToken)"/>.</summary>
    /// <param name="packageId">The plugin's package ID.</param>
    /// <param name="cancellationToken">Cancels the install; nothing is written to the state file once it's cancelled before the write.</param>
    /// <returns>What happened. Expected failures are results, not exceptions.</returns>
    public Task<InstallResult> InstallAsync(string packageId, CancellationToken cancellationToken) =>
        InstallAsync(packageId, null, null, null, cancellationToken);

    /// <summary>
    /// Installs a plugin and the plugin dependencies it needs from the feeds, and loads them into this process at once
    /// when it can (hot install). The plugin's cell is resolved again together with its other plugins; the host's
    /// packages are pinned at the host's versions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Under the root lock: re-reads the state file, resolves, checks feeds and packages against the trust policy,
    /// downloads, extracts and records the packages, validates the new state and writes it. Then, if
    /// <see cref="LoadAsync"/> has run, the new plugins load (dependencies first, so catalog events fire in that order)
    /// unless a running cell already answered one of their assembly names differently, the install would change a
    /// provider's exposed dependency, or it would duplicate a WPF-relevant assembly: then the result is
    /// <see cref="InstallStatus.PendingRestart"/> and they load at the next startup. A plugin in a new cell always
    /// loads unless one of those applies. Before <see cref="LoadAsync"/>, the install is only recorded
    /// (<see cref="InstallStatus.Installed"/>).
    /// </para>
    /// <para>
    /// Installs in one host resolve, write and decide one at a time; their plugins then load outside that lock, so
    /// plugin code (e.g. <see cref="IPlugin.InitializeAsync"/>) may install another plugin. An install's plugins wait
    /// for an earlier install still loading the plugins they depend on, or loading into a cell they join (so they load,
    /// and catalog events fire, dependencies first); they don't wait for unrelated installs. An install started from
    /// the plugin code of a loading install (nested) doesn't wait for it: one depending on its plugins is
    /// <see cref="InstallStatus.PendingRestart"/>. Cancelled after the state file was written, an install returns a
    /// result: the plugins not loaded yet are <see cref="InstallStatus.PendingRestart"/>, as are plugins not loaded for
    /// another reason than a fault (e.g. a plugin dependency whose load was cancelled).
    /// </para>
    /// <para>
    /// Nesting is detected through the async flow only. Plugin code that hands an install to another flow (e.g. an app
    /// worker thread) is not nested: if that install's plugins depend on the plugin whose
    /// <see cref="IPlugin.InitializeAsync"/> awaits it, or join that plugin's cell, it waits for that plugin to finish
    /// loading, and the awaiting <see cref="IPlugin.InitializeAsync"/> waits until one of them is cancelled. Don't await
    /// such an install from <see cref="IPlugin.InitializeAsync"/>; start it and let it finish later.
    /// </para>
    /// <para>
    /// Plugin code runs on the caller's synchronization context (as in <see cref="LoadAsync"/>), so catalog events fire
    /// there; downloading and verification don't. Signature verification can be slow (online revocation checks), so a
    /// UI app shows <paramref name="progress"/>.
    /// </para>
    /// </remarks>
    /// <param name="packageId">The plugin's package ID.</param>
    /// <param name="versionRange">The versions to choose from (the highest matching one is installed); <see langword="null"/> for the latest.</param>
    /// <param name="includePrerelease">Whether prerelease versions are considered; <see langword="null"/> uses <see cref="PluginHostOptions.AllowPrerelease"/>. A range naming a prerelease version always allows them.</param>
    /// <param name="progress">Receives the steps: resolving, downloading, verifying signatures, extracting, recording, loading.</param>
    /// <param name="cancellationToken">Cancels the install; nothing is written to the state file once it's cancelled before the write, and after the write the plugins not loaded yet load at the next startup.</param>
    /// <returns>What happened. Expected failures (not found, incompatible, conflicts, rejected by the trust policy, a feed error) are results, not exceptions.</returns>
    /// <exception cref="TimeoutException">Another process held the root lock for too long.</exception>
    /// <exception cref="InvalidOperationException">The <see cref="PluginHostOptions.NuGetConfigPath"/> file can't be read.</exception>
    /// <exception cref="PluginException">A plugin faulted while loading and <see cref="PluginHostOptions.FailFast"/> is set.</exception>
    public async Task<InstallResult> InstallAsync(
        string packageId,
        VersionRange? versionRange = null,
        bool? includePrerelease = null,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        if (!PackageLayout.IsValidPackageId(packageId))
        {
            return new InstallResult(InstallStatus.Failed, packageId, null, [$"'{packageId}' isn't a valid package ID."], [], [], []);
        }

        var operation = BeginOperation(cancellationToken);
        try
        {
            var token = operation.Token;
            InstallResult? result;
            InstallActivation? activation;
            InstallOutcome outcome;
            await _installGate.WaitAsync(token).ConfigureAwait(true);
            try
            {
                try
                {
                    outcome = await new PluginInstaller(_inventory, _layout, _options, _feeds.Value, Verifier, _trustVerdicts, _logger)
                        .InstallAsync(packageId, versionRange, includePrerelease ?? _options.AllowPrerelease, LockTimeout, progress, token)
                        .ConfigureAwait(true);   // plugin code (activation) runs on the caller's context
                }
                catch (Exception ex) when (FeedException.Find(ex) is { } feedError)
                {
                    outcome = InstallOutcome.Fail(InstallStatus.Failed, [feedError.Message]);
                }

                (result, activation) = CompleteInstall(packageId, outcome, progress, token);
            }
            finally
            {
                _installGate.Release();
            }

            // Plugin code runs after the install gate is released, so a plugin can install another one from its
            // InitializeAsync (or an app handler of a catalog event) without deadlocking.
            result ??= await ActivateInstallAsync(activation!, token).ConfigureAwait(true);

            // Already installed: report it as it is now, after the gate (its install may have finished loading it
            // meanwhile). If an install is still loading it, report how that ends, as when installs ran one at a time.
            if (outcome.AlreadyInstalled is { } existing && outcome.UninstallCancelled is null)
            {
                if (ActivationLoading(existing.Id) is { } loading)
                {
                    try
                    {
                        await loading.WaitAsync(token).ConfigureAwait(true);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        // Report it as it is now.
                    }
                }

                result = AlreadyInstalled(existing, outcome);
            }

            FeedLog.InstallFinished(_logger, packageId, result.Status, result.Reason is null ? "" : $" ({result.Reason})");
            return result;
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested && ex.CancellationToken != cancellationToken)
        {
            throw new OperationCanceledException(ex.Message, ex, cancellationToken);   // the caller's token, not the linked one
        }
        finally
        {
            await EndOperationAsync(operation).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Disposes the created singleton extensions and the plugins' entry points (each that implements
    /// <see cref="IAsyncDisposable"/> or <see cref="IDisposable"/>), plugin by plugin in reverse load order, each
    /// plugin's singletons in reverse creation order before its entry point. A cell's service provider
    /// (<see cref="PluginHostOptions.CellServicesFactory"/>) is disposed right after the cell's first-loaded plugin, so
    /// after all of the cell's extensions and plugins. Cells can't be unloaded; their
    /// assemblies stay loaded until the process exits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <see cref="LoadAsync"/> or <see cref="InstallAsync(string, VersionRange?, bool?, IProgress{InstallProgress}?, CancellationToken)"/>
    /// still running is cancelled (its plugins' <see cref="IPlugin.InitializeAsync"/> get a cancelled token), and
    /// disposal waits for it; no plugin starts loading once disposal has started. Disposal waits as long as a plugin
    /// takes: one whose <see cref="IPlugin.InitializeAsync"/> or disposal ignores the cancellation delays it
    /// indefinitely.
    /// </para>
    /// <para>
    /// Called from plugin code that such a call is running (e.g. <see cref="IPlugin.InitializeAsync"/>, or a task it
    /// started), it doesn't wait for that call; what that call creates afterwards is disposed when it ends. Called
    /// again while disposal runs, it returns a task that completes when disposal has finished, except from code the
    /// disposal itself runs or waits for (a plugin's disposal, or the plugin code of a call it waits for): then it
    /// returns at once.
    /// </para>
    /// </remarks>
    /// <returns>A task that completes when everything was disposed. Exceptions are logged, not thrown.</returns>
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource disposal;
        List<HostOperation> awaited;
        lock (_gate)
        {
            if (_disposal is { } running)
            {
                return _inDisposal.Value || CurrentOperations().Any(_awaited.Contains) ? default : new ValueTask(running.Task);
            }

            _disposed = true;
            _disposal = disposal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            // Wait for in-flight loads and installs, except the ones this call is part of: they can't finish first.
            var own = CurrentOperations().ToHashSet();
            foreach (var operation in _operations)
            {
                (own.Contains(operation) ? _notAwaited : _awaited).Add(operation);
            }

            awaited = _awaited.ToList();
        }

        return new ValueTask(DisposeCoreAsync(disposal, awaited));
    }

    private async Task DisposeCoreAsync(TaskCompletionSource disposal, List<HostOperation> awaited)
    {
        _inDisposal.Value = true;
        try
        {
            try
            {
                _disposing.Cancel();
            }
            catch (AggregateException ex)
            {
                HostLog.CancellationCallbackFailed(_logger, ex);
            }

            await Task.WhenAll(awaited.Select(o => o.Done)).ConfigureAwait(true);
            await DisposeActivatedAsync().ConfigureAwait(true);
            for (var i = _cellObservers.Count - 1; i >= 0; i--)
            {
                await DisposeObserverAsync(_cellObservers[i]).ConfigureAwait(true);
            }

            DeleteInUseRecord();

            List<CancellationTokenSource> tokens;
            lock (_gate)
            {
                tokens = _operationTokens.ToList();
                _operationTokens.Clear();
            }

            foreach (var token in tokens)
            {
                token.Dispose();
            }
        }
        finally
        {
            disposal.TrySetResult();
        }
    }

    /// <summary>
    /// Disposes what the activated plugins created and wasn't disposed yet: plugin by plugin in reverse load order, its
    /// singletons (reverse creation order), then its entry point, then (after the cell's first-loaded plugin) the cell's
    /// service provider; then every provider left. Run by disposal, and again by an operation disposal didn't wait for.
    /// </summary>
    private async Task DisposeActivatedAsync()
    {
        await _sweepGate.WaitAsync().ConfigureAwait(true);
        try
        {
            await DisposeActivatedCoreAsync().ConfigureAwait(true);
        }
        finally
        {
            _sweepGate.Release();
        }
    }

    private async Task DisposeActivatedCoreAsync()
    {
        List<PluginRuntime> runtimes;
        lock (_gate)
        {
            runtimes = _activated.ToList();
        }

        for (var i = runtimes.Count - 1; i >= 0; i--)
        {
            var runtime = runtimes[i];
            var singletons = runtime.CloseSingletons();
            for (var j = singletons.Count - 1; j >= 0; j--)
            {
                await DisposeQuietlyAsync(singletons[j], runtime.Plugin.Id, "a singleton extension").ConfigureAwait(true);
            }

            if (runtime.TakeInstance() is { } instance)
            {
                await DisposeQuietlyAsync(instance, runtime.Plugin.Id, "the entry point").ConfigureAwait(true);
            }

            // After the cell's first-loaded plugin: the cell's service provider (ICellServicesFactory).
            if (runtime.Plugin.Cell is { } cell && !runtimes.Take(i).Any(r => r.Plugin.Cell == cell))
            {
                await _cellServices.DisposeCellAsync(cell).ConfigureAwait(true);
            }
        }

        await _cellServices.DisposeAllAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Disposes a singleton that finished construction after disposal took its plugin's singletons. Synchronous when it
    /// can be (<see cref="IDisposable"/> first): the caller is a property getter.
    /// </summary>
    internal void DisposeRefusedSingleton(object instance, string pluginId)
    {
        try
        {
            if (instance is IDisposable disposable)
            {
                disposable.Dispose();
            }
            else if (instance is IAsyncDisposable asyncDisposable)
            {
                var pending = asyncDisposable.DisposeAsync();
                if (!pending.IsCompletedSuccessfully)
                {
                    _ = ObserveAsync(pending.AsTask());
                }
            }
        }
        catch (Exception ex)
        {
            HostLog.DisposeFailed(_logger, ex, "a singleton extension", pluginId);
        }

        async Task ObserveAsync(Task task)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                HostLog.DisposeFailed(_logger, ex, "a singleton extension", pluginId);
            }
        }
    }

    /// <summary>Marks a plugin Faulted, raises <see cref="PluginFaulted"/>, and with FailFast throws.</summary>
    internal void ReportFault(PluginRuntime runtime, string stage, Exception exception)
    {
        var plugin = runtime.Plugin;
        if (!plugin.TrySetFaulted(exception, $"Faulted while {stage}: {exception.Message}"))
        {
            HostLog.FaultedAgain(_logger, exception, plugin.Id, stage);
            return;
        }

        HostLog.Faulted(_logger, exception, plugin.Id, stage);
        var handlers = PluginFaulted;
        if (handlers is not null)
        {
            var args = new PluginFaultedEventArgs(plugin, exception);
            foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<PluginFaultedEventArgs>>())
            {
                try
                {
                    handler(this, args);
                }
                catch (Exception ex)
                {
                    HostLog.FaultHandlerFailed(_logger, ex, plugin.Id);
                }
            }
        }
    }

    private PluginValidator CreateValidator() =>
        new(_inventory, _layout, _options.Cells, _loggerFactory);

    private PluginSearch CreateSearch() => new(_inventory, _options, _feeds.Value, Verifier, _logger);

    /// <summary>
    /// The hot-load decision of an install (architecture §6), under the install gate: the result of an install that
    /// stopped earlier or can't load into this process, or the merged activation to run (after the gate is released).
    /// From here on the install is recorded, so cancellation becomes a result, not an exception.
    /// </summary>
    private (InstallResult? Result, InstallActivation? Activation) CompleteInstall(string packageId, InstallOutcome outcome, IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
    {
        if (outcome.Failure is { } failure)
        {
            return (new InstallResult(failure, packageId, outcome.Version, outcome.Reasons, [], [], outcome.Issues), null);
        }

        if (outcome.AlreadyInstalled is { } existing)
        {
            return (AlreadyInstalled(existing, outcome), null);
        }

        if (outcome.UninstallCancelled is { } switched)
        {
            // The plugin stays, at another version: that's a switch, applied at the next startup like an update.
            var current = Plugins.FirstOrDefault(p => string.Equals(p.Id, packageId, StringComparison.OrdinalIgnoreCase));
            int state;
            lock (_gate)
            {
                state = _loadState;
            }

            return (new InstallResult(state == LoadNotStarted ? InstallStatus.Installed : InstallStatus.PendingRestart, current?.Id ?? packageId, outcome.Version,
                [switched], [], current is null ? [] : [current], outcome.Issues), null);
        }

        var plan = outcome.Plan!;
        var planned = plan.Plugins.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        var newPlugins = outcome.NewPlugins.Select(id => planned[id]).ToList();
        var infos = newPlugins.ToDictionary(p => p.Id, p => new PluginInfo(p.Id, p.State.Version, p.Cell, PluginStatus.NotLoaded, "Not loaded yet."), StringComparer.OrdinalIgnoreCase);
        var installedId = newPlugins.Last().Id;   // the requested plugin comes after its dependencies

        int loadState;
        lock (_gate)
        {
            loadState = _loadState;
        }

        if (loadState == LoadNotStarted)
        {
            foreach (var info in infos.Values)
            {
                info.SetNotLoaded("Installed; it loads when LoadAsync runs.");
            }

            return (new InstallResult(InstallStatus.Installed, installedId, outcome.Version, [], [], infos.Values.ToList(), outcome.Issues), null);
        }

        progress?.Report(new InstallProgress(InstallStep.Loading, installedId, outcome.Version));
        var reasons = new List<string>();
        var conflicts = new List<HotLoadConflict>();
        if (loadState == Loading)
        {
            reasons.Add("LoadAsync was running while the plugin was installed, so it may not have seen it.");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            reasons.Add(_disposed ? "The host was disposed during the install." : "The install was cancelled after it was recorded.");
        }

        // Installs whose plugins are still activating. This install's activation waits for those it depends on: the
        // ones loading its plugin dependencies or loading into a cell it joins (so plugins load, and catalog events
        // fire, dependencies first). Unrelated ones it doesn't wait for, so plugin code that installs through another
        // flow (e.g. an app worker) doesn't deadlock. A nested install (started from their plugin code) can't wait.
        var nested = _currentActivation.Value is { IsFinished: false };
        List<InstallActivation> pending;
        lock (_gate)
        {
            pending = _activations.ToList();
        }

        var dependencies = newPlugins.SelectMany(p => p.PluginDependencies).Where(d => !infos.ContainsKey(d)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var joined = newPlugins.Select(p => p.Cell!).ToHashSet(StringComparer.Ordinal);
        var awaited = nested ? [] : pending.Where(a => a.Infos.Keys.Any(dependencies.Contains) || a.Cells.Overlaps(joined)).ToList();
        foreach (var plugin in newPlugins)
        {
            foreach (var dependency in plugin.PluginDependencies.Where(d => !infos.ContainsKey(d)))
            {
                var loaded = Plugins.FirstOrDefault(p => string.Equals(p.Id, dependency, StringComparison.OrdinalIgnoreCase));
                var coming = awaited.Select(a => a.Infos.GetValueOrDefault(dependency)).FirstOrDefault(i => i is not null);
                var running = coming ?? loaded;
                if (loaded?.Status != PluginStatus.Loaded && coming is null)
                {
                    reasons.Add(pending.Any(a => a.Infos.ContainsKey(dependency))
                        ? $"{plugin.Id} depends on the plugin {dependency}, which another install is still loading; this install was started by plugin code of that load, so it can't wait for it."
                        : $"{plugin.Id} depends on the plugin {dependency}, which isn't loaded in this process ({loaded?.Status.ToString() ?? "installed after startup"}).");
                }
                else if (planned.TryGetValue(dependency, out var next) && next.State.Version != running!.Version)
                {
                    reasons.Add($"{plugin.Id} was resolved with the plugin {dependency} {next.State.Version}, staged for the next startup; this process runs {running.Version}.");
                }
            }
        }

        reasons.AddRange(HotLoadRule.BreakingIssues(outcome.Issues));

        // Cell service providers are immutable: a plugin registering services can't join a cell that has one. Which new
        // plugins register services is read from metadata, so a PendingRestart install loads nothing into running cells.
        var classifications = plan.Cells.ToDictionary(c => c.Cell, StringComparer.Ordinal);
        var servicePlugins = newPlugins
            .Where(p => p.Cell is { } name && classifications.TryGetValue(name, out var cell) && _cellServices.RegistersServices(p, cell.Plan))
            .ToList();

        // A cell another install is still loading plugins into creates its provider from those plugins: a plugin
        // registering services can't join it. A nested install can't wait for them, so with a cell services factory
        // it can't join such a cell before its provider exists either.
        var busyCells = pending.SelectMany(a => a.Cells).ToHashSet(StringComparer.Ordinal);
        foreach (var plugin in servicePlugins.Where(p => busyCells.Contains(p.Cell!) && !_cellServices.IsCreated(p.Cell!)))
        {
            reasons.Add($"{plugin.Id} registers services, but another install is still loading plugins into cell '{plugin.Cell}', whose service provider is created without it.");
        }

        if (nested && _cellServices.HasFactory)
        {
            foreach (var plugin in newPlugins.Where(p => busyCells.Contains(p.Cell!) && !servicePlugins.Contains(p) && !_cellServices.IsCreated(p.Cell!)))
            {
                reasons.Add($"{plugin.Id} would join cell '{plugin.Cell}' before its service provider exists, while another install is still loading plugins into it; this install was started by plugin code of that load, so it can't wait for it.");
            }
        }

        // Decide and merge under the Gate, so no cell freezes a new answer in between.
        var created = new List<Cell>();
        var extended = new List<(Cell Cell, IReadOnlyList<CellAssembly> Added)>();
        lock (_cells.Gate)
        {
            foreach (var name in outcome.Cells.Where(classifications.ContainsKey))
            {
                if (_cells.FindCell(name) is { } cell)
                {
                    conflicts.AddRange(HotLoadRule.Check(name, cell.GetCommittedAnswers(), classifications[name].Plan));
                }
            }

            foreach (var conflict in conflicts)
            {
                var provider = plan.Plugins.FirstOrDefault(p =>
                    string.Equals(p.Cell, conflict.Cell, StringComparison.Ordinal)
                    && p.State.ExposedDependencies.Contains(conflict.Name, StringComparer.OrdinalIgnoreCase));
                reasons.Add(provider is null
                    ? $"Cell '{conflict.Cell}' already uses {conflict.Committed} for {conflict.Name}, but the install needs {conflict.Needed}."
                    : $"It would change {conflict.Name}, an exposed dependency of {provider.Id} in cell '{conflict.Cell}' (from {conflict.Committed} to {conflict.Needed}).");
            }

            if (conflicts.Count > 0 && outcome.StagedInCells.Count > 0)
            {
                reasons.Add($"The install was resolved with changes staged for the next startup ({string.Join(", ", outcome.StagedInCells)}), which this process doesn't run yet.");
            }

            reasons.AddRange(_cellServices.HotLoadBlockers(servicePlugins));

            if (reasons.Count == 0)
            {
                foreach (var name in outcome.Cells.Where(classifications.ContainsKey))
                {
                    if (_cells.FindCell(name) is { } cell)
                    {
                        extended.Add((cell, cell.MergePlan(classifications[name].Plan)));
                    }
                    else
                    {
                        created.Add(_cells.RegisterCell(name, classifications[name].Plan));
                    }
                }
            }
        }

        if (reasons.Count == 0)
        {
            foreach (var cell in created)
            {
                _cells.RunCellCreated(cell);
            }

            foreach (var (cell, added) in extended)
            {
                NotifyObservers(cell, "CellExtended", o => o.CellExtended(cell.Info, added));
            }
        }

        if (reasons.Count > 0)
        {
            var pendingReason = "Installed; it loads at the next startup: " + string.Join(" ", reasons);
            foreach (var info in infos.Values)
            {
                info.SetNotLoaded(pendingReason);
            }

            ListPendingRestart(infos.Values, reasons);

            return (new InstallResult(InstallStatus.PendingRestart, installedId, outcome.Version, reasons,
                conflicts.Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), infos.Values.ToList(), outcome.Issues), null);
        }

        RememberLoaded(plan, startup: false);
        AddPlugins(infos.Values);
        var activation = new InstallActivation(
            installedId,
            outcome.Version,
            outcome.Issues,
            plan.LoadOrder.Where(p => infos.ContainsKey(p.Id)).ToList(),
            infos,
            classifications,
            awaited.Select(a => a.Done).ToList());
        lock (_gate)
        {
            _activations.Add(activation);
        }

        return (null, activation);
    }

    /// <summary>The result of an install that found the plugin installed (or staged for uninstall: then cancelled that).</summary>
    private InstallResult AlreadyInstalled(PluginState existing, InstallOutcome outcome)
    {
        var current = Plugins.FirstOrDefault(p => string.Equals(p.Id, existing.Id, StringComparison.OrdinalIgnoreCase));
        if (outcome.UninstallCancelled is { } cancelled)
        {
            return new InstallResult(InstallStatus.Installed, existing.Id, existing.Version, [cancelled], [], current is null ? [] : [current], outcome.Issues);
        }

        return current?.Status == PluginStatus.Loaded
            ? new InstallResult(InstallStatus.Loaded, existing.Id, existing.Version, [], [], [current], outcome.Issues)
            : new InstallResult(InstallStatus.Installed, existing.Id, existing.Version,
                [$"{existing.Id} {existing.Version} is already installed{(current is null ? "" : $" ({current.Status})")}."], [], current is null ? [] : [current], outcome.Issues);
    }

    /// <summary>
    /// The activation of another install that is still loading a plugin, unless this async flow runs plugin code of an
    /// install (a nested install can't wait for activations).
    /// </summary>
    private Task? ActivationLoading(string pluginId)
    {
        if (_currentActivation.Value is { IsFinished: false })
        {
            return null;
        }

        lock (_gate)
        {
            return _activations.FirstOrDefault(a => a.Infos.ContainsKey(pluginId))?.Done;
        }
    }

    /// <summary>
    /// The activation step of a hot install, after the install gate is released: waits for the earlier installs it
    /// depends on (see <see cref="CompleteInstall"/>), then activates the new plugins, dependencies first. Cancellation (or
    /// disposal) marks the plugins not loaded yet; they load at the next startup.
    /// </summary>
    private async Task<InstallResult> ActivateInstallAsync(InstallActivation activation, CancellationToken cancellationToken)
    {
        _currentActivation.Value = activation;
        try
        {
            try
            {
                await Task.WhenAll(activation.Awaited).WaitAsync(cancellationToken).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Marked below.
            }

            var infos = activation.Infos;
            var batch = activation.Order.Select(p => (p, infos[p.Id])).ToList();
            var cancelled = new List<string>();
            for (var i = 0; i < activation.Order.Count; i++)
            {
                var plugin = activation.Order[i];
                var info = infos[plugin.Id];
                if (cancellationToken.IsCancellationRequested)
                {
                    cancelled = MarkCancelled(activation.Order.Skip(i), infos);
                    break;
                }

                var unavailable = plugin.PluginDependencies.FirstOrDefault(d =>
                    Plugins.FirstOrDefault(p => string.Equals(p.Id, d, StringComparison.OrdinalIgnoreCase)) is not { Status: PluginStatus.Loaded });
                if (unavailable is not null)
                {
                    info.SetNotLoaded($"Its plugin dependency {unavailable} isn't loaded.");
                    HostLog.NotLoaded(_logger, plugin.Id, info.Reason!);
                    continue;
                }

                try
                {
                    // Plugin code runs here: keep the caller's context, as LoadAsync does.
                    await ActivateAsync(plugin, info, activation.Classifications[plugin.Cell!].Plan, batch, cancellationToken).ConfigureAwait(true);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    cancelled = MarkCancelled(activation.Order.Skip(i), infos);
                    break;
                }
            }

            // Not loaded without a fault (a dependency's activation was cancelled, the host was disposed during its
            // InitializeAsync, ...): it loads at the next startup.
            foreach (var info in infos.Values.Where(i => i.Status == PluginStatus.NotLoaded && !cancelled.Contains(i.Id, StringComparer.OrdinalIgnoreCase)))
            {
                RecordPendingRestart([info.Id], [info.Reason ?? "It wasn't loaded."]);
            }

            var failed = infos.Values.Where(i => i.Status != PluginStatus.Loaded).ToList();
            var status = failed.Count == 0 ? InstallStatus.Loaded
                : failed.Any(i => i.Status == PluginStatus.Faulted) ? InstallStatus.Faulted
                : InstallStatus.PendingRestart;
            return new InstallResult(status, activation.InstalledId, activation.Version,
                failed.Select(i => $"{i.Id} is {i.Status}: {i.Reason}").ToList(), [], infos.Values.ToList(), activation.Issues);
        }
        finally
        {
            lock (_gate)
            {
                _activations.Remove(activation);
            }

            activation.Finish();
        }
    }

    /// <summary>An install's plugins left unloaded by cancellation: not loaded, pending a restart.</summary>
    private List<string> MarkCancelled(IEnumerable<PlannedPlugin> plugins, Dictionary<string, PluginInfo> infos)
    {
        var reason = (_disposed ? "The host was disposed" : "The install was cancelled") + " before the plugin loaded; it loads at the next startup.";
        var cancelled = plugins.Select(p => infos[p.Id]).Where(i => i.Status == PluginStatus.NotLoaded).ToList();
        foreach (var info in cancelled)
        {
            info.SetNotLoaded(reason);
            HostLog.NotLoaded(_logger, info.Id, reason);
        }

        var ids = cancelled.Select(i => i.Id).ToList();
        RecordPendingRestart(ids, [reason]);
        return ids;
    }

    private void AddPlugins(IEnumerable<PluginInfo> added)
    {
        lock (_gate)
        {
            Volatile.Write(ref _plugins, _plugins.Concat(added).ToList());
        }
    }

    private async Task ActivateAsync(
        PlannedPlugin planned, PluginInfo info, CellPlan plan, IReadOnlyList<(PlannedPlugin Plugin, PluginInfo Info)> batch, CancellationToken cancellationToken)
    {
        var cell = _cells.FindCell(planned.Cell!)!;
        var runtime = new PluginRuntime(this, info, _services, _activator) { Cell = cell };
        bool disposed;
        lock (_gate)
        {
            // No plugin starts loading once disposal has started; one that started is disposed after it.
            disposed = _disposed;
            if (!disposed)
            {
                _activated.Add(runtime);
            }
        }

        if (disposed)
        {
            info.SetNotLoaded(DisposedReason);
            HostLog.NotLoaded(_logger, planned.Id, DisposedReason);
            return;
        }

        var registrar = new ExtensionRegistrar(runtime);
        var stage = "loading its assemblies";
        try
        {
            // Loads the assemblies and creates the entry point; the cell's first plugin also creates the cell's services.
            var (prepared, services) = _cellServices.Prepare(planned, cell, plan, batch);
            runtime.Instance = prepared.EntryPoint;
            if (prepared.Fault is { } fault)
            {
                stage = prepared.Stage;
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(fault);
            }

            if (_cellObservers.Count > 0)
            {
                stage = "running the cell observers";
                using (cell.EnterContextualReflection())
                {
                    foreach (var observer in _cellObservers)
                    {
                        observer.PluginLoading(cell.Info, info, prepared.Assemblies);
                    }
                }
            }

            stage = "creating its cell's services";
            runtime.Services = services();
            var context = new PluginContext(
                planned.Id,
                planned.State.Version,
                _layout.GetDataDirectory(planned.Id),
                _loggerFactory.CreateLogger(planned.Id),
                runtime.Services);

            using (cell.EnterContextualReflection())
            {
                if (prepared.EntryPoint is { } instance)
                {
                    stage = "running Register";
                    instance.Register(registrar, context);
                    registrar.Complete();

                    stage = "running InitializeAsync";
                    await instance.InitializeAsync(context, cancellationToken).ConfigureAwait(true);
                }
                else
                {
                    stage = "scanning its assemblies for [Extension] types";
                    ScanForExtensions(prepared.Assemblies, registrar);
                    registrar.Complete();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var cause = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
            ReportFault(runtime, stage, cause);
            if (_options.FailFast)
            {
                throw new PluginException($"Plugin '{planned.Id}' faulted while {stage}: {cause.Message}", planned.Id, cause);
            }

            return;
        }

        if (_disposed)
        {
            info.SetNotLoaded(DisposedReason);
            HostLog.NotLoaded(_logger, planned.Id, DisposedReason);
            return;
        }

        var extensions = registrar.Extensions;
        info.SetLoaded();
        HostLog.Loaded(_logger, planned.Id, planned.State.Version, planned.Cell!, extensions.Count);
        Catalog.Add(info, extensions);
    }

    /// <summary>Registers every type of the plugin's own assemblies that declares <see cref="ExtensionAttribute"/>s (not inherited ones).</summary>
    private static void ScanForExtensions(IReadOnlyList<Assembly> assemblies, ExtensionRegistrar registrar)
    {
        foreach (var assembly in assemblies)
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                var first = ex.LoaderExceptions.FirstOrDefault(e => e is not null);
                throw new TypeLoadException($"Types of {assembly.GetName().Name} can't be loaded: {first?.Message ?? ex.Message}", ex);
            }

            foreach (var type in types)
            {
                foreach (var attribute in type.GetCustomAttributes<ExtensionAttribute>(inherit: false))
                {
                    registrar.Add(attribute.ExtensionPoint, type, attribute, attribute.Lifetime);
                }
            }
        }
    }

    private const string DisposedReason = "The host was disposed before the plugin finished loading.";

    private string CancelledReason => _disposed ? "The host was disposed while loading." : "Loading was cancelled.";

    /// <summary>
    /// Starts a <see cref="LoadAsync"/> or <see cref="InstallAsync(string, VersionRange?, bool?, IProgress{InstallProgress}?, CancellationToken)"/>
    /// call that <see cref="DisposeAsync"/> cancels and waits for. Call it from the async method itself: the call is
    /// then current in its async flow (plugin code it runs included).
    /// </summary>
    /// <exception cref="ObjectDisposedException">The host is disposed.</exception>
    private HostOperation BeginOperation(CancellationToken cancellationToken)
    {
        HostOperation operation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!cancellationToken.CanBeCanceled)
            {
                operation = new HostOperation(_currentOperation.Value, _disposing.Token, null, default);   // follows disposal only
            }
            else
            {
                // Linked to disposal for the host's lifetime; to the app's token only while the call runs (registration
                // disposed in EndOperationAsync), so an app token cancelled later doesn't cancel the plugins' tokens.
                var source = CancellationTokenSource.CreateLinkedTokenSource(_disposing.Token);
                _operationTokens.Add(source);
                operation = new HostOperation(_currentOperation.Value, source.Token, source, cancellationToken.UnsafeRegister(static s => ((CancellationTokenSource)s!).Cancel(), source));
            }

            _operations.Add(operation);
        }

        _currentOperation.Value = operation;
        return operation;
    }

    /// <summary>
    /// Ends a call. If disposal didn't wait for it (it was called from the call's own flow), waits for disposal, then
    /// disposes what the call created meanwhile (e.g. an entry point or a cell's provider it was creating).
    /// </summary>
    private async Task EndOperationAsync(HostOperation operation)
    {
        operation.AppRegistration.Dispose();   // from now on the plugins' token follows disposal only
        bool sweep;
        Task? disposal;
        lock (_gate)
        {
            _operations.Remove(operation);
            sweep = _notAwaited.Remove(operation);
            _awaited.Remove(operation);
            disposal = _disposal?.Task;
            if (operation.Source is { IsCancellationRequested: true } cancelled && _operationTokens.Remove(cancelled))
            {
                cancelled.Dispose();   // its token stays cancelled; it needs no link to disposal any more
            }
        }

        operation.Finish();
        if (sweep && disposal is not null)
        {
            await disposal.ConfigureAwait(true);
            await DisposeActivatedAsync().ConfigureAwait(true);
        }
    }

    /// <summary>The calls this async flow runs in: the current one and those it was started from. Under <see cref="_gate"/>.</summary>
    private IEnumerable<HostOperation> CurrentOperations()
    {
        for (var operation = _currentOperation.Value; operation is not null; operation = operation.Parent)
        {
            yield return operation;
        }
    }

    private static void MarkRemaining(IEnumerable<PlannedPlugin> plugins, Dictionary<string, PluginInfo> infos, string reason)
    {
        foreach (var plugin in plugins)
        {
            if (infos[plugin.Id].Status == PluginStatus.NotLoaded)
            {
                infos[plugin.Id].SetNotLoaded(reason);
            }
        }
    }

    /// <summary>
    /// Creates the host's cell observers. If a factory throws (or returns null), the observers already created are
    /// disposed before the exception propagates, so nothing they hold (e.g. a handler on the default load context)
    /// outlives a host that was never created.
    /// </summary>
    private List<ICellObserver> CreateObservers(IEnumerable<CellObserverFactory> factories)
    {
        var observers = new List<ICellObserver>();
        try
        {
            foreach (var factory in factories)
            {
                observers.Add(factory(_loggerFactory)
                    ?? throw new InvalidOperationException("A PluginHostOptions.CellObservers factory returned null."));
            }

            return observers;
        }
        catch
        {
            for (var i = observers.Count - 1; i >= 0; i--)
            {
                // Synchronously: this is a constructor. An observer that's only IAsyncDisposable is waited for.
                DisposeObserverAsync(observers[i]).AsTask().GetAwaiter().GetResult();
            }

            throw;
        }
    }

    /// <summary>Calls the cell observers; an exception is logged and doesn't stop the others or the load.</summary>
    private void NotifyObservers(Cell cell, string what, Action<ICellObserver> notify)
    {
        foreach (var observer in _cellObservers)
        {
            try
            {
                notify(observer);
            }
            catch (Exception ex)
            {
                HostLog.CellObserverFailed(_logger, ex, observer.GetType().FullName ?? observer.GetType().Name, what, cell.Name);
            }
        }
    }

    private async ValueTask DisposeObserverAsync(ICellObserver observer)
    {
        try
        {
            if (observer is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(true);
            }
            else if (observer is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        catch (Exception ex)
        {
            HostLog.CellObserverDisposeFailed(_logger, ex, observer.GetType().FullName ?? observer.GetType().Name);
        }
    }

    private async ValueTask DisposeQuietlyAsync(object instance, string pluginId, string what)
    {
        try
        {
            if (instance is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(true);
            }
            else if (instance is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        catch (Exception ex)
        {
            HostLog.DisposeFailed(_logger, ex, what, pluginId);
        }
    }

    /// <summary>An in-flight <see cref="LoadAsync"/> or install: its token (also cancelled by disposal) and its completion.</summary>
    private sealed class HostOperation(HostOperation? parent, CancellationToken token, CancellationTokenSource? source, CancellationTokenRegistration appRegistration)
    {
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The call this one was started from (e.g. an install from plugin code during a load).</summary>
        public HostOperation? Parent { get; } = parent;

        public CancellationToken Token { get; } = token;

        /// <summary>The token's own source (linked to disposal), unless the app's token can't be cancelled.</summary>
        public CancellationTokenSource? Source { get; } = source;

        /// <summary>Cancels <see cref="Source"/> when the app's token is cancelled, while the call runs.</summary>
        public CancellationTokenRegistration AppRegistration { get; } = appRegistration;

        public Task Done => _done.Task;

        public void Finish() => _done.TrySetResult();
    }

    /// <summary>A hot install's plugins, merged into their cells and waiting for (or in) activation.</summary>
    private sealed class InstallActivation(
        string installedId,
        NuGetVersion? version,
        IReadOnlyList<ValidationIssue> issues,
        IReadOnlyList<PlannedPlugin> order,
        Dictionary<string, PluginInfo> infos,
        Dictionary<string, CellClassification> classifications,
        IReadOnlyList<Task> awaited)
    {
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string InstalledId { get; } = installedId;

        public NuGetVersion? Version { get; } = version;

        public IReadOnlyList<ValidationIssue> Issues { get; } = issues;

        /// <summary>The new plugins, dependencies first.</summary>
        public IReadOnlyList<PlannedPlugin> Order { get; } = order;

        public Dictionary<string, PluginInfo> Infos { get; } = infos;

        public Dictionary<string, CellClassification> Classifications { get; } = classifications;

        /// <summary>The cells its plugins load into.</summary>
        public IReadOnlySet<string> Cells { get; } = order.Select(p => p.Cell!).ToHashSet(StringComparer.Ordinal);

        /// <summary>The activations of earlier installs it depends on (plugin dependencies, cells it joins), which it waits for.</summary>
        public IReadOnlyList<Task> Awaited { get; } = awaited;

        public Task Done => _done.Task;

        public bool IsFinished => _done.Task.IsCompleted;

        public void Finish() => _done.TrySetResult();
    }
}
