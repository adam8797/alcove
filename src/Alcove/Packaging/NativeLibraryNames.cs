namespace Alcove.Packaging;

/// <summary>Maps the name in a <c>DllImport</c> to the file names the runtime would probe for.</summary>
internal static class NativeLibraryNames
{
    /// <summary>
    /// The file names a native library request may refer to, in probing order, e.g. <c>nat</c> →
    /// <c>libnat.so</c>, <c>nat.so</c>, <c>libnat</c>, <c>nat</c> on Linux. Requests with a directory part
    /// return nothing: they name a specific file.
    /// </summary>
    public static IReadOnlyList<string> Candidates(string unmanagedDllName)
    {
        if (string.IsNullOrEmpty(unmanagedDllName) || unmanagedDllName.Contains('/', StringComparison.Ordinal) || unmanagedDllName.Contains('\\', StringComparison.Ordinal))
        {
            return [];
        }

        var name = unmanagedDllName;
        if (OperatingSystem.IsWindows())
        {
            return HasExtension(name, ".dll") || HasExtension(name, ".exe") ? [name] : [name + ".dll", name];
        }

        var suffix = OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst() ? ".dylib" : ".so";
        var hasSuffix = HasExtension(name, suffix) || name.Contains(suffix + ".", StringComparison.Ordinal);
        var hasPrefix = name.StartsWith("lib", StringComparison.Ordinal);
        var candidates = new List<string>(4);
        if (hasSuffix)
        {
            candidates.Add(name);
            if (!hasPrefix)
            {
                candidates.Add("lib" + name);
            }
        }
        else
        {
            if (!hasPrefix)
            {
                candidates.Add("lib" + name + suffix);
            }

            candidates.Add(name + suffix);
            if (!hasPrefix)
            {
                candidates.Add("lib" + name);
            }

            candidates.Add(name);
        }

        return candidates;
    }

    /// <summary>The comparer for native file names on this OS.</summary>
    public static StringComparer FileNameComparer =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static bool HasExtension(string name, string extension) =>
        name.EndsWith(extension, StringComparison.OrdinalIgnoreCase);
}
