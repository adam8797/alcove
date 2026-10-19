namespace Alcove.Internal.Hosting;

/// <summary>
/// The files of the assemblies the host gives every cell (host-owned and host-shared), by simple name, for reading
/// their metadata. Nothing is loaded. Built from the process's trusted platform assemblies, which the default context
/// loads from. Shared framework assemblies are left out: no plugin-facing base type lives there, and reading them
/// (System.Private.CoreLib among them) for every check would be costly.
/// </summary>
internal sealed class HostAssemblyFiles
{
    private readonly IHostAssemblies _host;
    private readonly Lazy<IReadOnlyDictionary<string, string>> _files;

    /// <param name="host">Which names the host provides.</param>
    /// <param name="files">The candidate files by simple name; only those <paramref name="host"/> provides are returned.</param>
    internal HostAssemblyFiles(IHostAssemblies host, Func<IReadOnlyDictionary<string, string>> files)
    {
        _host = host;
        _files = new Lazy<IReadOnlyDictionary<string, string>>(files);
    }

    /// <summary>The host's assemblies of this process (<c>TRUSTED_PLATFORM_ASSEMBLIES</c>), except the shared frameworks' ones.</summary>
    public static HostAssemblyFiles FromCurrentProcess(HostInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        return new(inventory, () => TrustedPlatformAssemblies(name => !inventory.FrameworkAssemblies.ContainsKey(name)));
    }

    /// <summary>
    /// The file of a host-owned or host-shared assembly (by the inventory, or because <paramref name="isHost"/> says
    /// so), or <see langword="null"/> if the host doesn't provide it or carries no file for it.
    /// </summary>
    /// <param name="simpleName">The assembly's simple name.</param>
    /// <param name="isHost">Whether the caller already knows the host provides it (e.g. a cell plan's host-owned names).</param>
    public string? Find(string simpleName, bool isHost = false) =>
        (isHost || _host.IsHostOwnedAssembly(simpleName) || _host.IsHostSharedAssembly(simpleName))
        && _files.Value.TryGetValue(simpleName, out var path)
            ? path
            : null;

    private static Dictionary<string, string> TrustedPlatformAssemblies(Func<string, bool> include)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string list)
        {
            foreach (var path in list.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if (include(name))
                {
                    files.TryAdd(name, path);
                }
            }
        }

        return files;
    }
}
