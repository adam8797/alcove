using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Resources;
using System.Text;
using System.Text.RegularExpressions;

namespace Alcove.Internal.Metadata;

/// <summary>
/// Reads the WPF-related facts of an assembly from metadata, without loading it
/// (spike: wpf-across-load-contexts, §6 and <c>tools/XamlAsmInspect</c>).
/// </summary>
internal static partial class WpfAssemblyInspector
{
    private const string WpfResourcesSuffix = ".g.resources";
    private const byte BamlAssemblyInfoRecord = 0x1C;
    private const int MaxAssemblyNameLength = 1024;

    /// <summary>Inspects the assembly at <paramref name="path"/>; <see langword="null"/> if it isn't a .NET assembly.</summary>
    public static WpfAssemblyInfo? Inspect(string path) => Inspect(File.OpenRead(path), path);

    /// <summary>
    /// Inspects an assembly read from <paramref name="stream"/> (which is disposed); <paramref name="path"/> is only
    /// recorded. Returns <see langword="null"/> if it isn't a .NET assembly.
    /// </summary>
    public static WpfAssemblyInfo? Inspect(Stream stream, string path)
    {
        using var pe = MetadataFile.TryOpen(stream, out _);
        if (pe is null)
        {
            return null;
        }

        var md = pe.GetMetadataReader();
        var identity = AssemblyIdentity.FromDefinition(md);
        var references = md.AssemblyReferences
            .Select(h => AssemblyIdentity.FromReference(md, md.GetAssemblyReference(h)))
            .ToImmutableArray();

        var resources = FindWpfResources(md, identity.Name);
        if (resources is not { } resource)
        {
            return new WpfAssemblyInfo(path, identity, false, references, [], []);
        }

        return new WpfAssemblyInfo(
            path,
            identity,
            true,
            references,
            ReadBamlAssemblyReferences(pe, resource),
            FindUnversionedLocators(md, identity.Name));
    }

    private static ManifestResource? FindWpfResources(MetadataReader md, string simpleName)
    {
        var expected = simpleName + WpfResourcesSuffix;
        foreach (var handle in md.ManifestResources)
        {
            var resource = md.GetManifestResource(handle);
            if (md.StringComparer.Equals(resource.Name, expected, ignoreCase: true))
            {
                return resource;
            }
        }

        return null;
    }

    /// <summary>Reads the assembly names recorded in the BAML parts of the embedded <c>.g.resources</c>.</summary>
    private static ImmutableArray<AssemblyIdentity> ReadBamlAssemblyReferences(PEReader pe, ManifestResource resource)
    {
        if (!resource.Implementation.IsNil)
        {
            return []; // linked from another file: not something the SDK produces
        }

        byte[] resourceBytes;
        try
        {
            // Embedded manifest resource: a 4-byte length, then the data, at the CLI resources directory + offset.
            var directory = pe.PEHeaders.CorHeader!.ResourcesDirectory;
            var block = pe.GetSectionData(directory.RelativeVirtualAddress);
            var offset = checked((int)resource.Offset);
            var reader = block.GetReader(offset, block.Length - offset);
            var length = reader.ReadInt32();
            resourceBytes = reader.ReadBytes(length);
        }
        catch (Exception ex) when (ex is BadImageFormatException or ArgumentOutOfRangeException or OverflowException)
        {
            return [];
        }

        var found = new HashSet<AssemblyIdentity>();
        try
        {
            using var resources = new ResourceReader(new MemoryStream(resourceBytes, writable: false));
            var names = new List<string>();
            var enumerator = resources.GetEnumerator();
            while (enumerator.MoveNext())
            {
                if (enumerator.Key is string name && name.EndsWith(".baml", StringComparison.OrdinalIgnoreCase))
                {
                    names.Add(name);
                }
            }

            foreach (var name in names)
            {
                // Raw bytes only: nothing is deserialized.
                resources.GetResourceData(name, out _, out var data);
                CollectBamlAssemblyRecords(data, found);
            }
        }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException or ArgumentException or IOException or FormatException)
        {
            // A malformed resource file: report what was read so far.
        }

        return [.. found.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Finds BAML <c>AssemblyInfo</c> records: record type 0x1C, a 7-bit-encoded record size, a 2-byte assembly id,
    /// then the full assembly name as a 7-bit-length-prefixed UTF-8 string. Without parsing every BAML record type,
    /// a candidate counts only if the size is consistent and the string is a full assembly name.
    /// </summary>
    internal static void CollectBamlAssemblyRecords(ReadOnlySpan<byte> baml, ISet<AssemblyIdentity> found)
    {
        for (var i = 0; i < baml.Length; i++)
        {
            if (baml[i] != BamlAssemblyInfoRecord)
            {
                continue;
            }

            var position = i + 1;
            if (!TryRead7BitInt(baml, ref position, out var recordSize))
            {
                continue;
            }

            var sizeEnd = position;
            position += 2; // assembly id
            if (!TryRead7BitInt(baml, ref position, out var nameLength)
                || nameLength is <= 0 or > MaxAssemblyNameLength
                || position + nameLength > baml.Length)
            {
                continue;
            }

            var recordEnd = position + nameLength;
            // The size is that of the record after its type byte; accept either convention for the size field itself.
            if (recordSize != recordEnd - (i + 1) && recordSize != recordEnd - sizeEnd)
            {
                continue;
            }

            string text;
            try
            {
                text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(baml.Slice(position, nameLength));
            }
            catch (DecoderFallbackException)
            {
                continue;
            }

            if (FullAssemblyName().IsMatch(text)
                && AssemblyNameInfo.TryParse(text.AsSpan(), out var info)
                && AssemblyIdentity.FromNameInfo(info) is { } identity)
            {
                found.Add(identity);
                i = recordEnd - 1;
            }
        }
    }

    private static bool TryRead7BitInt(ReadOnlySpan<byte> data, ref int position, out int value)
    {
        value = 0;
        for (var shift = 0; shift < 35; shift += 7)
        {
            if (position >= data.Length)
            {
                return false;
            }

            var b = data[position++];
            value |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return value >= 0;
            }
        }

        return false;
    }

    /// <summary>User strings naming this assembly's own components without a version.</summary>
    private static ImmutableArray<string> FindUnversionedLocators(MetadataReader md, string simpleName)
    {
        if (md.GetHeapSize(HeapIndex.UserString) <= 1)
        {
            return [];
        }

        var marker = $"/{simpleName};";
        var locators = ImmutableArray.CreateBuilder<string>();
        var handle = MetadataTokens.UserStringHandle(1);
        while (!handle.IsNil)
        {
            var value = md.GetUserString(handle);
            if (value.Contains(marker, StringComparison.OrdinalIgnoreCase) && IsUnversionedLocator(value, simpleName))
            {
                locators.Add(value);
            }

            handle = md.GetNextHandle(handle);
        }

        return locators.ToImmutable();
    }

    /// <summary>
    /// Whether <paramref name="value"/> contains <c>/&lt;simpleName&gt;[;token];component/</c> with no
    /// <c>;V&lt;version&gt;</c> part, i.e. a pack-URI path WPF resolves by name only.
    /// </summary>
    internal static bool IsUnversionedLocator(string value, string simpleName)
    {
        var search = 0;
        while (true)
        {
            var start = value.IndexOf($"/{simpleName};", search, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                return false;
            }

            var rest = value.AsSpan(start + simpleName.Length + 2);
            var end = rest.IndexOf("component/", StringComparison.OrdinalIgnoreCase);
            if (end >= 0)
            {
                var versioned = false;
                foreach (var part in rest[..end].ToString().Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (part.Length > 1 && part[0] is 'v' or 'V' && char.IsAsciiDigit(part[1]))
                    {
                        versioned = true;
                    }
                }

                if (!versioned && rest[..end].IndexOf('/') < 0)
                {
                    return true;
                }
            }

            search = start + 1;
        }
    }

    [GeneratedRegex(@"^[^,=]+, Version=\d+(\.\d+){1,3}(, [A-Za-z]+=[^,]*)*$", RegexOptions.CultureInvariant)]
    private static partial Regex FullAssemblyName();
}
