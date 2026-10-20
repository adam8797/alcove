using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using Alcove.Packaging;
using Microsoft.Extensions.Logging;

namespace Alcove.Cells;

/// <summary>
/// A cell: one non-collectible <see cref="AssemblyLoadContext"/> for one or more plugins (architecture §5).
/// </summary>
/// <remarks>
/// <para><c>Load</c> resolves in this order:</para>
/// <list type="number">
///   <item>0. a frozen answer: a name that already has a successful answer gets the same answer again;</item>
///   <item>1. host-owned (shared frameworks included) → <see langword="null"/> (default context);</item>
///   <item>2. a plugin in the transitive plugin-dependency closure, or one of its exposed dependencies → the providing cell's <see cref="AssemblyLoadContext.LoadFromAssemblyName"/>;</item>
///   <item>3. cell-private → load from the plugin root (3a: an identical WPF-relevant assembly another cell already loaded is shared);</item>
///   <item>4. host-shared → <see langword="null"/>;</item>
///   <item>5. unknown → <see langword="null"/>, logged at debug level.</item>
/// </list>
/// <para>
/// After steps 2–3 the version is checked: an assembly lower than requested is rejected, because the runtime
/// would accept it and fail later with a <see cref="MissingMethodException"/>. Every answer (including native
/// libraries) is decided and frozen under <see cref="CellEnvironment.Gate"/> (the load itself runs outside it), because the runtime doesn't
/// remember "use the host's copy" answers per context: without freezing, a changed plan could give one cell two
/// copies of an assembly. Failures are recorded but never overwrite a success, and never block a later answer.
/// </para>
/// </remarks>
internal sealed class Cell : AssemblyLoadContext
{
    private readonly CellEnvironment _environment;
    private readonly ILogger _logger;

    // All guarded by _environment.Gate.
    private readonly CellPlan _plan;
    private readonly Dictionary<string, CellAnswer> _given = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CellAnswer> _givenNative = new(StringComparer.Ordinal);

    // Reserved answers a request other than the reserving one loaded successfully: a failure of the reserving
    // request (e.g. its version check) must not revert them, or the cell could answer the name differently later.
    private readonly HashSet<CellAnswer> _succeeded = new(ReferenceEqualityComparer.Instance);

    internal Cell(string name, CellPlan plan, CellEnvironment environment)
        : base(name, isCollectible: false)
    {
        _environment = environment;
        _logger = environment.Logger;
        _plan = Copy(plan);
        Info = new CellInfo(this);
        Resolving += OnResolving;
        ResolvingUnmanagedDll += OnResolvingUnmanagedDll;
    }

    /// <summary>The cell's name.</summary>
    public new string Name => base.Name!;

    /// <summary>The cell as <see cref="ICellObserver"/>s see it.</summary>
    public CellInfo Info { get; }

    /// <summary>The environment shared with the host's other cells.</summary>
    public CellEnvironment Environment => _environment;

    /// <summary>IDs of the plugins in the cell.</summary>
    public IReadOnlyCollection<string> Plugins
    {
        get
        {
            lock (_environment.Gate)
            {
                return _plan.Plugins.ToList();
            }
        }
    }

    /// <summary>
    /// The committed answers: the plan merged with every answer actually given. The hot-load rule compares a
    /// new resolution against this.
    /// </summary>
    public CommittedAnswers GetCommittedAnswers()
    {
        lock (_environment.Gate)
        {
            var assemblies = _plan.GetPlannedAssemblyAnswers();
            foreach (var (name, answer) in _given)
            {
                if (answer.Source != AnswerSource.Failed || !assemblies.ContainsKey(name))
                {
                    assemblies[name] = answer;
                }
            }

            // Native answers are given per requested name (a DllImport's "nat"), plans list file names
            // ("libnat.so"): a given answer also stands for every file name the request may refer to, so a plan
            // that adds one of them is compared with what the cell already gave for the request.
            var natives = _plan.GetPlannedNativeAnswers();
            foreach (var (requested, answer) in _givenNative)
            {
                var key = answer.Path is null ? requested : Path.GetFileName(answer.Path);
                if (answer.Source != AnswerSource.Failed || !natives.ContainsKey(key))
                {
                    natives[key] = answer;
                }
            }

            foreach (var (requested, answer) in _givenNative.Where(g => g.Value.Source != AnswerSource.Failed))
            {
                foreach (var candidate in NativeLibraryNames.Candidates(requested).Append(requested))
                {
                    natives.TryAdd(candidate, answer);
                }
            }

            return new CommittedAnswers(assemblies, natives);
        }
    }

    /// <summary>The planned cell-private assemblies, sorted by name (<see cref="CellInfo.PrivateAssemblies"/>).</summary>
    public IReadOnlyList<CellAssembly> GetPrivateAssemblies()
    {
        lock (_environment.Gate)
        {
            return [.. _plan.Private.Values.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).Select(ToPublic)];
        }
    }

    /// <summary>
    /// Adds to the plan (a hot install into this cell). Entries replace planned ones, but answers already
    /// given stay frozen: the install pipeline must apply the hot-load rule first.
    /// </summary>
    /// <returns>The private assemblies the plan didn't have before (new names, or a name with a different file), sorted by name.</returns>
    public IReadOnlyList<CellAssembly> MergePlan(CellPlan additions)
    {
        ArgumentNullException.ThrowIfNull(additions);
        lock (_environment.Gate)
        {
            var added = additions.Private.Values
                .Where(a => !_plan.Private.TryGetValue(a.Name, out var existing) || !string.Equals(existing.Path, a.Path, StringComparison.Ordinal))
                .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .Select(ToPublic)
                .ToList();
            Merge(_plan, additions);
            return added;
        }
    }

    private static CellAssembly ToPublic(PrivateAssembly asset) =>
        new(asset.Name, asset.Path, asset.PackageId, asset.PackageVersion, asset.WpfRelevant);

    /// <inheritdoc />
    /// <remarks>
    /// The Gate is held only to read the frozen answer, or to decide one and reserve it in Given, so a
    /// concurrent request for the same name gets the same answer. The load itself (from a path, or through a
    /// provider cell) runs outside the Gate: holding a process-wide lock across the runtime's loader would
    /// serialise all loading and risk lock-order deadlocks. A first answer that fails is reverted to Failed.
    /// </remarks>
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var simpleName = assemblyName.Name;
        if (string.IsNullOrEmpty(simpleName))
        {
            return null;
        }

        var key = Key(assemblyName);
        CellAnswer answer;
        bool reserved;
        PrivateAssembly? sharedCandidate;
        CellAnswer? keptOver = null;   // logged after leaving the Gate: logger providers are app code
        lock (_environment.Gate)
        {
            if (_given.TryGetValue(key, out var prior) && prior.Source != AnswerSource.Failed)
            {
                // 0. Frozen answer.
                var planned = PlannedAnswer(key);
                if (planned is not null && !planned.SameAs(prior))
                {
                    keptOver = planned;
                }

                answer = prior;
                reserved = false;
            }
            else
            {
                answer = Decide(assemblyName, key);
                _given[key] = answer;
                reserved = true;
            }

            sharedCandidate = answer.Source == AnswerSource.Private
                && _plan.Private.TryGetValue(simpleName, out var asset)
                && asset.WpfRelevant
                && string.Equals(asset.Path, answer.Path, StringComparison.Ordinal)
                    ? asset
                    : null;
        }

        if (keptOver is not null)
        {
            CellLog.FrozenAnswerKept(_logger, Name, key, keptOver, answer);
        }
        else if (reserved && answer.Reason == AnswerReason.Unknown && !IsSatellite(assemblyName))
        {
            CellLog.UnknownAssembly(_logger, Name, simpleName, assemblyName.Version);
        }

        try
        {
            _environment.LoadingHook?.Invoke(this, key);
            var (result, final) = Execute(assemblyName, answer, sharedCandidate);
            if (!reserved)
            {
                MarkSucceeded(answer);
            }
            else
            {
                if (!ReferenceEquals(final, answer))
                {
                    lock (_environment.Gate)
                    {
                        if (_given.TryGetValue(key, out var current) && ReferenceEquals(current, answer))
                        {
                            _given[key] = final;
                        }
                    }
                }

                CellLog.Resolved(_logger, Name, simpleName, assemblyName.Version, final);
            }

            return result;
        }
        catch (Exception ex)
        {
            if (reserved)
            {
                RevertReservation(_given, key, answer, ex.Message);
            }

            CellLog.ResolutionFailed(_logger, Name, key, ex.Message);
            throw;
        }
    }

    /// <inheritdoc />
    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        CellAnswer answer;
        bool reserved;
        CellAnswer? keptOver = null;   // logged after leaving the Gate
        lock (_environment.Gate)
        {
            if (_givenNative.TryGetValue(unmanagedDllName, out var prior) && prior.Source != AnswerSource.Failed)
            {
                if (prior.Path is not null
                    && _plan.NativeLibraries.TryGetValue(Path.GetFileName(prior.Path), out var planned)
                    && !string.Equals(planned.Path, prior.Path, StringComparison.Ordinal))
                {
                    keptOver = CellPlan.PrivateAnswer(planned.Path, planned.PackageId, planned.PackageVersion, AnswerReason.CellPrivate);
                }

                answer = prior;
                reserved = false;
            }
            else
            {
                NativeLibraryAsset? asset = null;
                foreach (var candidate in NativeLibraryNames.Candidates(unmanagedDllName))
                {
                    if (_plan.NativeLibraries.TryGetValue(candidate, out asset))
                    {
                        break;
                    }
                }

                answer = asset is null
                    ? CellAnswer.Host(AnswerReason.Unknown)
                    : CellPlan.PrivateAnswer(asset.Path, asset.PackageId, asset.PackageVersion, AnswerReason.CellPrivate);
                _givenNative[unmanagedDllName] = answer;
                reserved = true;
            }
        }

        if (keptOver is not null)
        {
            CellLog.FrozenAnswerKept(_logger, Name, unmanagedDllName, keptOver, answer);
        }

        if (reserved)
        {
            CellLog.NativeResolved(_logger, Name, unmanagedDllName, answer);
        }

        if (answer.Path is null)
        {
            return IntPtr.Zero;
        }

        IntPtr handle;
        try
        {
            _environment.LoadingHook?.Invoke(this, unmanagedDllName);
            handle = LoadUnmanagedDllFromPath(answer.Path);
            if (!reserved)
            {
                MarkSucceeded(answer);
            }
        }
        catch (Exception ex)
        {
            if (reserved)
            {
                RevertReservation(_givenNative, unmanagedDllName, answer, ex.Message);
            }

            CellLog.ResolutionFailed(_logger, Name, unmanagedDllName, ex.Message);
            throw;
        }

        if (reserved && _environment.NativeLibraries.Register(answer.Path, Name) is { } collision)
        {
            CellLog.NativeCollision(_logger, collision.FileName, collision.Path, collision.Cell, collision.OtherPath, collision.OtherCell);
        }

        return handle;
    }

    /// <inheritdoc />
    public override string ToString() => $"Cell '{Name}'";

    private CellAnswer Decide(AssemblyName assemblyName, string key)
    {
        var name = assemblyName.Name!;
        if (IsSatellite(assemblyName))
        {
            return _plan.Satellites.TryGetValue(key, out var satellite)
                ? CellPlan.PrivateAnswer(satellite.Path, satellite.PackageId, satellite.PackageVersion, AnswerReason.CellPrivate)
                : CellAnswer.Host(AnswerReason.Unknown);
        }

        // 1. Host-owned.
        if (_plan.HostOwned.Contains(name) || _environment.Host.IsHostOwnedAssembly(name))
        {
            return CellAnswer.Host(AnswerReason.HostOwned);
        }

        // 2. Plugin-provided (transitive closure + exposed dependencies).
        if (_plan.Provided.TryGetValue(name, out var provider))
        {
            return new CellAnswer(AnswerSource.Provider, AnswerReason.PluginProvided) { ProviderCell = provider };
        }

        // 3. Cell-private.
        if (_plan.Private.TryGetValue(name, out var asset))
        {
            return CellPlan.PrivateAnswer(asset.Path, asset.PackageId, asset.PackageVersion, AnswerReason.CellPrivate);
        }

        // 4. Host-shared.
        if (_plan.HostShared.Contains(name) || _environment.Host.IsHostSharedAssembly(name))
        {
            return CellAnswer.Host(AnswerReason.HostShared);
        }

        // 5. Unknown (logged by the caller, outside the Gate).
        return CellAnswer.Host(AnswerReason.Unknown);
    }

    /// <summary>Carries out an answer. Runs outside the Gate; uses only what was captured under it.</summary>
    private (Assembly? Result, CellAnswer Answer) Execute(AssemblyName assemblyName, CellAnswer answer, PrivateAssembly? sharedCandidate)
    {
        switch (answer.Source)
        {
            case AnswerSource.Provider:
            {
                var provider = _environment.FindCell(answer.ProviderCell!)
                    ?? throw new FileLoadException($"Cell '{Name}' delegates {assemblyName.Name} to cell '{answer.ProviderCell}', which doesn't exist.", assemblyName.FullName);
                return (CheckVersion(assemblyName, provider.LoadFromAssemblyName(assemblyName), answer), answer);
            }

            case AnswerSource.Private when sharedCandidate is not null:
            {
                CheckFileVersion(assemblyName, sharedCandidate.Path, answer);
                var (shared, owner) = _environment.SharedAssemblies.GetOrLoad(sharedCandidate, Name, () => LoadFromAssemblyPath(sharedCandidate.Path));
                if (!string.Equals(owner, Name, StringComparison.Ordinal))
                {
                    answer = answer with { Reason = AnswerReason.SharedWpfRelevant };
                    CellLog.SharedAssemblyReused(_logger, Name, sharedCandidate.Name, owner);
                }

                return (CheckVersion(assemblyName, shared, answer), answer);
            }

            case AnswerSource.Private:
                CheckFileVersion(assemblyName, answer.Path!, answer);
                return (CheckVersion(assemblyName, LoadFromAssemblyPath(answer.Path!), answer), answer);

            default:
                return (null, answer);
        }
    }

    private Assembly CheckVersion(AssemblyName requested, Assembly result, CellAnswer answer)
    {
        // The runtime accepts a lower version from a custom Load; the failure would only show up later.
        EnsureVersion(requested, result.GetName().Version, answer);
        return result;
    }

    /// <summary>
    /// The version check before a file is loaded, from its metadata: a lower version is rejected without loading it,
    /// so a failed request never leaves the wrong assembly in the cell.
    /// </summary>
    private void CheckFileVersion(AssemblyName requested, string path, CellAnswer answer)
    {
        if (requested.Version is not null)
        {
            EnsureVersion(requested, AssemblyName.GetAssemblyName(path).Version, answer);
        }
    }

    private void EnsureVersion(AssemblyName requested, Version? actual, CellAnswer answer)
    {
        if (requested.Version is { } wanted && (actual is null || actual < wanted))
        {
            CellLog.VersionTooLow(_logger, Name, requested.Name!, wanted, actual, answer);
            throw new FileLoadException(
                $"Cell '{Name}' can't provide {requested.Name} {wanted}: it resolves to version {actual?.ToString() ?? "(none)"} from {answer}, which is lower than requested.",
                requested.FullName);
        }
    }

    private void MarkSucceeded(CellAnswer answer)
    {
        lock (_environment.Gate)
        {
            _succeeded.Add(answer);
        }
    }

    private Assembly? OnResolving(AssemblyLoadContext context, AssemblyName assemblyName)
    {
        // Runs only after Load returned null AND the default context failed: a "host" answer that never
        // succeeded. A name the default context did supply stays frozen, even if a later request (e.g. for a
        // higher version) fails.
        var name = assemblyName.Name;
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        // Inspect the default context before taking the Gate: the Gate never wraps runtime loader calls.
        var defaultHasIt = Default.Assemblies.Any(a => string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase));
        var key = Key(assemblyName);
        lock (_environment.Gate)
        {
            var given = _given.GetValueOrDefault(key);
            if (given is null || (given.Source == AnswerSource.Host && !defaultHasIt))
            {
                _given[key] = CellAnswer.Failed($"{assemblyName.FullName} wasn't found in the cell or the host");
            }
        }

        CellLog.ResolutionFailed(_logger, Name, key, "not found in the cell or the host");
        return null;
    }

    private IntPtr OnResolvingUnmanagedDll(Assembly assembly, string unmanagedDllName)
    {
        bool failed;
        lock (_environment.Gate)
        {
            failed = _givenNative.TryGetValue(unmanagedDllName, out var given) && given.Source == AnswerSource.Host;
            if (failed)
            {
                _givenNative[unmanagedDllName] = CellAnswer.Failed($"native library '{unmanagedDllName}' wasn't found in the cell or by default probing");
            }
        }

        if (failed)
        {
            CellLog.ResolutionFailed(_logger, Name, unmanagedDllName, "native library not found");
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// A reserved first answer failed: revert it to Failed so a later request can decide again, unless it
    /// already changed or a concurrent request loaded it successfully (a success never gets overwritten).
    /// </summary>
    private void RevertReservation(Dictionary<string, CellAnswer> answers, string key, CellAnswer reservation, string error)
    {
        lock (_environment.Gate)
        {
            if (answers.TryGetValue(key, out var current) && ReferenceEquals(current, reservation) && !_succeeded.Contains(reservation))
            {
                answers[key] = CellAnswer.Failed(error);
            }
        }
    }

    private CellAnswer? PlannedAnswer(string key)
    {
        if (_plan.HostOwned.Contains(key))
        {
            return CellAnswer.Host(AnswerReason.HostOwned);
        }

        if (_plan.Provided.TryGetValue(key, out var provider))
        {
            return new CellAnswer(AnswerSource.Provider, AnswerReason.PluginProvided) { ProviderCell = provider };
        }

        if (_plan.Private.TryGetValue(key, out var asset))
        {
            return CellPlan.PrivateAnswer(asset.Path, asset.PackageId, asset.PackageVersion, AnswerReason.CellPrivate);
        }

        if (_plan.Satellites.TryGetValue(key, out var satellite))
        {
            return CellPlan.PrivateAnswer(satellite.Path, satellite.PackageId, satellite.PackageVersion, AnswerReason.CellPrivate);
        }

        return _plan.HostShared.Contains(key) ? CellAnswer.Host(AnswerReason.HostShared) : null;
    }

    private static bool IsSatellite(AssemblyName name) =>
        !string.IsNullOrEmpty(name.CultureName) && !string.Equals(name.CultureName, CultureInfo.InvariantCulture.Name, StringComparison.Ordinal);

    private static string Key(AssemblyName name) =>
        IsSatellite(name) ? CellPlan.SatelliteKey(name.Name!, name.CultureName!) : name.Name!;

    private static CellPlan Copy(CellPlan plan)
    {
        var copy = new CellPlan();
        Merge(copy, plan);
        return copy;
    }

    private static void Merge(CellPlan target, CellPlan source)
    {
        target.Plugins.UnionWith(source.Plugins);

        // A name moves to the category the new plan gives it.
        foreach (var name in source.HostOwned.Concat(source.Provided.Keys).Concat(source.Private.Keys).Concat(source.HostShared))
        {
            target.HostOwned.Remove(name);
            target.Provided.Remove(name);
            target.Private.Remove(name);
            target.HostShared.Remove(name);
        }

        target.HostOwned.UnionWith(source.HostOwned);
        target.HostShared.UnionWith(source.HostShared);
        foreach (var (name, cell) in source.Provided)
        {
            target.Provided[name] = cell;
        }

        foreach (var (name, asset) in source.Private)
        {
            target.Private[name] = asset;
        }

        foreach (var (key, satellite) in source.Satellites)
        {
            target.Satellites[key] = satellite;
        }

        foreach (var (fileName, native) in source.NativeLibraries)
        {
            target.NativeLibraries[fileName] = native;
        }
    }
}
