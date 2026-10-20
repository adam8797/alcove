namespace Alcove.Install;

/// <summary>The outcome of <see cref="PluginHost.InstallAsync(string, NuGet.Versioning.VersionRange?, bool?, IProgress{InstallProgress}?, CancellationToken)"/>.</summary>
public enum InstallStatus
{
    /// <summary>
    /// Installed and recorded in the state file, but not loaded because <see cref="PluginHost.LoadAsync"/> hasn't run
    /// yet in this host: the plugin loads when it does. Also returned when the plugin was already installed.
    /// </summary>
    Installed,

    /// <summary>Installed and loaded into this process: its extensions are in the catalog (hot install).</summary>
    Loaded,

    /// <summary>
    /// Installed and recorded, but it can't load into the running process (<see cref="InstallResult.Reasons"/> says
    /// why, e.g. its cell already loaded another version of a dependency; <see cref="InstallResult.ConflictingNames"/>
    /// lists the assemblies). It loads at the next startup.
    /// </summary>
    PendingRestart,

    /// <summary>Installed and recorded, but a plugin threw while loading (see <see cref="InstallResult.Plugins"/>). The library doesn't uninstall it.</summary>
    Faulted,

    /// <summary>Not installed: the plugin needs something this host can't provide, e.g. a newer version of the host's contracts.</summary>
    Incompatible,

    /// <summary>Not installed: the package wasn't found, isn't a plugin, its dependencies can't be resolved, a feed failed, or the trust policy rejected it.</summary>
    Failed,
}
