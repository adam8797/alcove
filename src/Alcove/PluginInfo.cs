using NuGet.Versioning;

namespace Alcove;

/// <summary>
/// An installed plugin as this host sees it: identity, cell and load status. The same instance is updated
/// when the status changes (e.g. to <see cref="PluginStatus.Faulted"/> when an extension can't be created).
/// </summary>
public sealed class PluginInfo
{
    private readonly Lock _gate = new();
    private PluginStatus _status;
    private Exception? _fault;
    private string? _reason;

    internal PluginInfo(string id, NuGetVersion version, string? cell, PluginStatus status, string? reason)
    {
        Id = id;
        Version = version;
        Cell = cell;
        _status = status;
        _reason = reason;
    }

    /// <summary>The plugin's ID (its NuGet package ID).</summary>
    public string Id { get; }

    /// <summary>The installed version.</summary>
    public NuGetVersion Version { get; }

    /// <summary>The cell the plugin is assigned to, or <see langword="null"/> if it wasn't assigned one (e.g. disabled).</summary>
    public string? Cell { get; }

    /// <summary>The load status.</summary>
    public PluginStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    /// <summary>The exception that faulted the plugin, if <see cref="Status"/> is <see cref="PluginStatus.Faulted"/>.</summary>
    public Exception? Fault
    {
        get
        {
            lock (_gate)
            {
                return _fault;
            }
        }
    }

    /// <summary>Why the plugin isn't loaded or is faulted, or <see langword="null"/> when it's loaded.</summary>
    public string? Reason
    {
        get
        {
            lock (_gate)
            {
                return _reason;
            }
        }
    }

    /// <inheritdoc />
    public override string ToString()
    {
        lock (_gate)
        {
            return _reason is null ? $"{Id} {Version}: {_status}" : $"{Id} {Version}: {_status} ({_reason})";
        }
    }

    internal void SetLoaded()
    {
        lock (_gate)
        {
            _status = PluginStatus.Loaded;
            _reason = null;
        }
    }

    internal void SetNotLoaded(string reason)
    {
        lock (_gate)
        {
            _status = PluginStatus.NotLoaded;
            _reason = reason;
        }
    }

    /// <summary>Marks the plugin Faulted; returns <see langword="false"/> if it already was (the first fault is kept).</summary>
    internal bool TrySetFaulted(Exception fault, string reason)
    {
        lock (_gate)
        {
            if (_status == PluginStatus.Faulted)
            {
                return false;
            }

            _status = PluginStatus.Faulted;
            _fault = fault;
            _reason = reason;
            return true;
        }
    }
}
