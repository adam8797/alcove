namespace Alcove;

/// <summary>The load status of an installed plugin in this process.</summary>
public enum PluginStatus
{
    /// <summary>Not loaded; <see cref="PluginInfo.Reason"/> says why (e.g. a validation error, or a plugin dependency that isn't loaded).</summary>
    NotLoaded = 0,

    /// <summary>Loaded: its extensions are in the catalog.</summary>
    Loaded,

    /// <summary>
    /// The plugin threw while loading, registering, initialising or creating an extension.
    /// <see cref="PluginInfo.Fault"/> holds the exception. The library never disables it by itself.
    /// </summary>
    Faulted,

    /// <summary>Installed but disabled, so not loaded.</summary>
    Disabled,
}
