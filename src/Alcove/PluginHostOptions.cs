using System.Text.Json;
using System.Text.Json.Serialization;
using Alcove.Catalog;
using Alcove.Cells;
using Alcove.Feeds;
using Alcove.Storage;
using Alcove.Trust;
using Microsoft.Extensions.Logging;

namespace Alcove;

/// <summary>
/// The configuration of a plugin host. Whatever object the app passes to the host is final: the library never
/// reads a configuration file on its own and never merges sources.
/// </summary>
/// <remarks>
/// Build it in code, or read the JSON configuration file with <see cref="Load"/> (and optionally modify it).
/// Code-only settings such as <see cref="LoggerFactory"/> are never read from or written to the file.
/// </remarks>
public sealed class PluginHostOptions
{
    /// <summary>
    /// The plugin root: where installed plugins and library state live. Required; the library never picks a
    /// default. A relative path in a configuration file is relative to that file's directory.
    /// </summary>
    public string Root { get; set; } = "";

    /// <summary>The app's custom NuGet package type that marks plugin packages, e.g. <c>MyAppPlugin</c>.</summary>
    public string? PluginPackageType { get; set; }

    /// <summary>
    /// Package IDs the app declares as its own, such as its contracts package and an app SDK. Their assemblies
    /// (and those of their dependencies) always load from the host. The .NET shared frameworks,
    /// <c>Alcove.Abstractions</c>, <c>NuGet.Versioning</c> and <c>Microsoft.Extensions.Logging.Abstractions</c>
    /// are always host-owned and needn't be listed.
    /// </summary>
    public IList<string> HostOwnedPackages { get; init => field = value ?? new List<string>(); } = new List<string>();

    /// <summary>The feeds plugins are installed from.</summary>
    /// <remarks>
    /// Alcove doesn't use NuGet's user-wide HTTP cache (the one <c>dotnet nuget locals http-cache --list</c> shows,
    /// which NuGet clients otherwise reuse for up to 30 minutes): each search, version query or install reads the
    /// feed afresh, keeps responses in a temporary folder and deletes it when done. So an install sees a version
    /// published moments ago, and nothing outside the plugin root (and the temporary folder) is written. Packages are
    /// always extracted into the plugin root, never into NuGet's global packages folder.
    /// </remarks>
    public IList<PackageFeed> Feeds { get; init => field = value ?? new List<PackageFeed>(); } = new List<PackageFeed>();

    /// <summary>
    /// An optional <c>nuget.config</c> whose enabled package sources (with their credentials) are used in addition
    /// to <see cref="Feeds"/>. Only this file is read, never the user's or machine's NuGet settings. A relative path
    /// in a configuration file is relative to that file's directory.
    /// </summary>
    public string? NuGetConfigPath { get; set; }

    /// <summary>Named cells defined by the app. A plugin not listed here gets the cell its manifest asks for, or its own.</summary>
    public IList<CellDefinition> Cells { get; init => field = value ?? new List<CellDefinition>(); } = new List<CellDefinition>();

    /// <summary>Whether prerelease versions are considered when no version is given.</summary>
    public bool AllowPrerelease { get; set; }

    /// <summary>How many previous versions of each plugin are kept on disk for rollback. Defaults to 1.</summary>
    public int VersionsToKeep { get; set; } = 1;

    /// <summary>For development: the first plugin fault throws instead of being recorded.</summary>
    public bool FailFast { get; set; }

    /// <summary>What may be installed: allowed feeds, required signatures, allowed signers and revocation checking.</summary>
    public TrustPolicy TrustPolicy { get; set => field = value ?? new TrustPolicy(); } = new TrustPolicy();

    /// <summary>Creates the library's loggers and the plugins' loggers. Code only; never in the configuration file.</summary>
    [JsonIgnore]
    public ILoggerFactory? LoggerFactory { get; set; }

    /// <summary>
    /// The host's services, offered to plugins as <see cref="IPluginContext.Services"/> and to the activator.
    /// Without it, plugins get an empty provider. Code only.
    /// </summary>
    [JsonIgnore]
    public IServiceProvider? Services { get; set; }

    /// <summary>
    /// Creates extension instances. The default calls the registered factory or a public parameterless
    /// constructor; dependency injection add-ons replace it. Code only.
    /// </summary>
    [JsonIgnore]
    public IExtensionActivator? Activator { get; set; }

    /// <summary>
    /// Creates one service provider per cell, holding its plugins' service registrations and falling back to
    /// <see cref="Services"/>. Set by dependency injection add-ons; without it, plugins and the activator see
    /// <see cref="Services"/> directly. Code only.
    /// </summary>
    [JsonIgnore]
    public ICellServicesFactory? CellServicesFactory { get; set; }

    /// <summary>
    /// Factories of the <see cref="ICellObserver"/>s that each host made from these options creates (one observer per
    /// factory and host): they're told when cells are created or grow and when plugins' assemblies load. Set by UI
    /// add-ons such as <c>Alcove.Wpf</c>. Code only.
    /// </summary>
    [JsonIgnore]
    public IList<CellObserverFactory> CellObservers { get; init => field = value ?? new List<CellObserverFactory>(); } = new List<CellObserverFactory>();

    /// <summary>
    /// Supplies credentials for authenticated feeds when the feed's own credentials are missing or rejected, e.g.
    /// a login dialog. Without it, only <see cref="PackageFeed.Credentials"/> and the <c>nuget.config</c>'s
    /// credentials are used. Code only.
    /// </summary>
    [JsonIgnore]
    public ICredentialProvider? CredentialProvider { get; set; }

    /// <summary>Reads a configuration file. The library never does this by itself.</summary>
    /// <param name="path">The configuration file, e.g. <c>&lt;root&gt;/config.json</c>.</param>
    /// <returns>The options in the file. A relative <see cref="Root"/> is made absolute against the file's directory.</returns>
    /// <exception cref="JsonException">The file isn't valid configuration JSON.</exception>
    public static PluginHostOptions Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        using var stream = File.OpenRead(fullPath);
        var options = JsonSerializer.Deserialize(stream, AlcoveJsonContext.Default.PluginHostOptions)
            ?? throw new JsonException($"The configuration file '{fullPath}' is empty (JSON 'null').");

        if (!string.IsNullOrEmpty(options.Root) && !Path.IsPathFullyQualified(options.Root))
        {
            options.Root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(fullPath)!, options.Root));
        }

        if (!string.IsNullOrEmpty(options.NuGetConfigPath) && !Path.IsPathFullyQualified(options.NuGetConfigPath))
        {
            options.NuGetConfigPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(fullPath)!, options.NuGetConfigPath));
        }

        return options;
    }

    /// <summary>Writes the serialisable settings to a configuration file (atomically), e.g. from an app's settings UI.</summary>
    /// <param name="path">The configuration file to write.</param>
    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        AtomicFile.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(this, AlcoveJsonContext.Default.PluginHostOptions));
    }

    /// <summary>Throws if the options can't be used to create a host (no root, or a relative one).</summary>
    internal void EnsureValid()
    {
        if (string.IsNullOrWhiteSpace(Root))
        {
            throw new ArgumentException("PluginHostOptions.Root is required: the app must supply the plugin root.", nameof(Root));
        }

        if (!Path.IsPathFullyQualified(Root))
        {
            throw new ArgumentException($"PluginHostOptions.Root must be an absolute path, not '{Root}'.", nameof(Root));
        }

        if (VersionsToKeep < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(VersionsToKeep), VersionsToKeep, "VersionsToKeep can't be negative.");
        }
    }
}
