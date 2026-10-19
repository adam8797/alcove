namespace Alcove.IntegrationTests.Infrastructure;

/// <summary>A fresh directory under the system temp folder, deleted on dispose.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory(string prefix = "alcove")
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    /// <summary>The directory's full path.</summary>
    public string Path { get; }

    /// <summary>Combines <see cref="Path"/> with relative parts.</summary>
    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }

                return;
            }
            catch (Exception ex) when (attempt < 5 && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(200 * (attempt + 1));
            }
        }
    }

    /// <summary>
    /// Deletes the directory, ignoring files that can't be deleted. Assemblies loaded into a (non-collectible)
    /// cell stay mapped until the process exits, and Windows doesn't let mapped files be deleted.
    /// </summary>
    public void DisposeBestEffort()
    {
        try
        {
            Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Copies a directory tree, skipping build output (bin/, obj/).</summary>
    public static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, System.IO.Path.Combine(destination, System.IO.Path.GetFileName(file)));
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            var name = System.IO.Path.GetFileName(directory);
            if (name is "bin" or "obj")
            {
                continue;
            }

            CopyTree(directory, System.IO.Path.Combine(destination, name));
        }
    }
}
