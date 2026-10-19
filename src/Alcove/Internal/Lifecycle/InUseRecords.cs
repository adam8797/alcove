using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Alcove.Internal.IO;
using NuGet.Versioning;

namespace Alcove.Internal.Lifecycle;

/// <summary>
/// Which package folders running hosts use, so a startup's cleanup in another process (or another host of this one)
/// never deletes them: one file per host, <c>&lt;root&gt;/.inuse/&lt;pid&gt;-&lt;start&gt;-&lt;host&gt;.json</c>, listing
/// the package folders its plans reference. Written at load and after each hot install, deleted when the host is
/// disposed. A record whose process is gone (checked by process ID <em>and</em> start time, so a reused ID doesn't
/// count) is stale and removed; a crash only leaves a stale record, which delays nothing once noticed.
/// </summary>
internal static class InUseRecords
{
    /// <summary>The folder in the plugin root.</summary>
    public const string DirectoryName = ".inuse";

    /// <summary>How far a recorded start time may be from the one the OS reports now (rounding differs between reads).</summary>
    private static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(2);

    /// <summary>A new record path for a host of this process.</summary>
    public static string NewPath(string root)
    {
        var (id, start) = Current();
        return Path.Combine(root, DirectoryName, $"{id}-{start.Ticks.ToString(CultureInfo.InvariantCulture)}-{Guid.NewGuid():N}.json");
    }

    /// <summary>Writes (replaces) a host's record atomically.</summary>
    public static void Write(string path, IEnumerable<(string Id, NuGetVersion Version)> packages)
    {
        var (id, start) = Current();
        Write(path, id, start, packages);
    }

    /// <summary>Writes a record for any process (tests fabricate records).</summary>
    public static void Write(string path, int processId, DateTime processStartUtc, IEnumerable<(string Id, NuGetVersion Version)> packages)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("processId", processId);
            writer.WriteString("processStartTime", processStartUtc);
            writer.WriteStartArray("packages");
            foreach (var (id, version) in packages.Distinct().OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Version))
            {
                writer.WriteStringValue($"{id.ToLowerInvariant()}/{version.ToNormalizedString().ToLowerInvariant()}");
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        AtomicFile.WriteAllBytes(path, buffer.ToArray());
    }

    /// <summary>Deletes a host's record (best effort).</summary>
    public static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Stale once this process exits; removed by the next cleanup.
        }
    }

    /// <summary>
    /// The package folders (full paths) that live processes' records list, removing records of processes that are
    /// gone. Returns <see langword="null"/> if a live process's record can't be read: then nothing may be deleted.
    /// </summary>
    public static HashSet<string>? ReadLive(PackageLayout layout, Action<string> log)
    {
        var inUse = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directory = Path.Combine(layout.Root, DirectoryName);
        if (!Directory.Exists(directory))
        {
            return inUse;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            Record? record;
            try
            {
                record = Parse(AtomicFile.ReadAllBytes(file));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                record = null;
            }

            if (record is null)
            {
                // Unreadable: decide by the name's process ID alone, conservatively.
                if (ProcessIdFromName(file) is { } pid && !IsAlive(pid, null))
                {
                    Delete(file);
                    continue;
                }

                log($"The in-use record '{file}' can't be read");
                return null;
            }

            if (!IsAlive(record.ProcessId, record.ProcessStartUtc))
            {
                Delete(file);
                continue;
            }

            foreach (var package in record.Packages)
            {
                var slash = package.IndexOf('/', StringComparison.Ordinal);
                if (slash > 0 && NuGetVersion.TryParse(package[(slash + 1)..], out var version))
                {
                    inUse.Add(Path.GetFullPath(layout.GetPackageDirectory(package[..slash], version)));
                }
            }
        }

        return inUse;
    }

    /// <summary>Whether a process is running, and (if <paramref name="startUtc"/> is given) is the one started then.</summary>
    internal static bool IsAlive(int processId, DateTime? startUtc)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                return false;
            }

            return startUtc is not { } expected || (process.StartTime.ToUniversalTime() - expected).Duration() <= StartTimeTolerance;
        }
        catch (ArgumentException)
        {
            return false;   // no such process
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return true;   // exists but can't be inspected (another user's): assume it's the one
        }
    }

    private static (int Id, DateTime StartUtc) Current()
    {
        using var process = Process.GetCurrentProcess();
        return (process.Id, process.StartTime.ToUniversalTime());
    }

    private static int? ProcessIdFromName(string file)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        var dash = name.IndexOf('-', StringComparison.Ordinal);
        return dash > 0 && int.TryParse(name[..dash], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) ? pid : null;
    }

    private static Record? Parse(byte[] content)
    {
        try
        {
            using var json = JsonDocument.Parse(content);
            var root = json.RootElement;
            return new Record(
                root.GetProperty("processId").GetInt32(),
                root.GetProperty("processStartTime").GetDateTime().ToUniversalTime(),
                root.GetProperty("packages").EnumerateArray().Select(p => p.GetString() ?? "").ToList());
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private sealed record Record(int ProcessId, DateTime ProcessStartUtc, IReadOnlyList<string> Packages);
}
