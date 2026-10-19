using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Alcove.Internal.Metadata;

/// <summary>Opens a file's ECMA-335 metadata for reading. Nothing is loaded for execution.</summary>
internal static class MetadataFile
{
    /// <summary>
    /// Opens <paramref name="stream"/> as a .NET assembly. Returns <see langword="null"/> with a reason if it
    /// isn't a PE file, has no metadata, or is a module without an assembly manifest. The caller disposes the
    /// returned reader, which takes ownership of the stream.
    /// </summary>
    public static PEReader? TryOpen(Stream stream, out string? reason)
    {
        PEReader pe;
        try
        {
            pe = new PEReader(stream, PEStreamOptions.PrefetchEntireImage);
        }
        catch (BadImageFormatException ex)
        {
            stream.Dispose();
            reason = $"the file isn't a valid PE image: {ex.Message}";
            return null;
        }

        try
        {
            if (!pe.HasMetadata)
            {
                reason = "the file has no .NET metadata";
                pe.Dispose();
                return null;
            }

            if (!pe.GetMetadataReader().IsAssembly)
            {
                reason = "the file is a module without an assembly manifest";
                pe.Dispose();
                return null;
            }

            reason = null;
            return pe;
        }
        catch (BadImageFormatException ex)
        {
            reason = $"the file isn't a valid PE image: {ex.Message}";
            pe.Dispose();
            return null;
        }
    }

    /// <summary>Opens the file at <paramref name="path"/>; see <see cref="TryOpen(Stream, out string?)"/>.</summary>
    public static PEReader? TryOpen(string path, out string? reason) => TryOpen(File.OpenRead(path), out reason);

    /// <summary>The scanned assembly's simple name.</summary>
    public static string SimpleName(MetadataReader md) => md.GetString(md.GetAssemblyDefinition().Name);
}
