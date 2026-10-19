using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NuGet.Packaging;
using NuGet.Versioning;

namespace Alcove.IntegrationTests.Infrastructure;

/// <summary>
/// A throwaway NuGet V3 feed on localhost (HttpListener) serving the <c>.nupkg</c> files of a folder: the service
/// index, the flat container (PackageBaseAddress: version lists, packages, nuspecs) and search (SearchQueryService
/// 3.5.0, with package type filtering). Optionally requires Basic authentication. Never touches the network.
/// </summary>
internal sealed class LocalNuGetFeed : IDisposable
{
    private readonly HttpListener _listener;
    private readonly Dictionary<string, List<FeedEntry>> _packages = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _authorization;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly List<string> _requests = [];

    public LocalNuGetFeed(string packagesDirectory, NetworkCredential? requiredCredentials = null)
    {
        foreach (var path in Directory.GetFiles(packagesDirectory, "*.nupkg"))
        {
            using var reader = new PackageArchiveReader(path);
            var nuspec = reader.NuspecReader;
            var identity = reader.GetIdentity();
            using var nuspecStream = reader.GetNuspec();
            using var buffer = new MemoryStream();
            nuspecStream.CopyTo(buffer);
            var entry = new FeedEntry(identity.Id, identity.Version, path, buffer.ToArray(),
                nuspec.GetTitle(), nuspec.GetDescription(), nuspec.GetAuthors(), reader.GetPackageTypes().Select(t => t.Name).ToList());
            if (!_packages.TryGetValue(identity.Id, out var versions))
            {
                _packages[identity.Id] = versions = [];
            }

            versions.Add(entry);
        }

        if (requiredCredentials is not null)
        {
            _authorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{requiredCredentials.UserName}:{requiredCredentials.Password}"));
        }

        (_listener, BaseAddress) = Start();
        _loop = Task.Run(ServeAsync);
    }

    /// <summary>The feed's root, e.g. <c>http://127.0.0.1:12345/</c>.</summary>
    public Uri BaseAddress { get; }

    /// <summary>The service index URL to configure as a feed.</summary>
    public string ServiceIndex => new Uri(BaseAddress, "v3/index.json").ToString();

    /// <summary>Every request so far (<c>STATUS METHOD path</c>).</summary>
    public IReadOnlyList<string> Requests
    {
        get
        {
            lock (_requests)
            {
                return _requests.ToList();
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Close();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }

        _stop.Dispose();
    }

    private static (HttpListener, Uri) Start()
    {
        for (var attempt = 0; ; attempt++)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            var address = new Uri($"http://127.0.0.1:{port}/");
            var listener = new HttpListener();
            listener.Prefixes.Add(address.ToString());
            try
            {
                listener.Start();
                return (listener, address);
            }
            catch (HttpListenerException) when (attempt < 5)
            {
                listener.Close();
            }
        }
    }

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            _ = Task.Run(() => Handle(context));
        }
    }

    private void Handle(HttpListenerContext context)
    {
        var response = context.Response;
        var path = context.Request.Url!.AbsolutePath;
        try
        {
            if (_authorization is not null && context.Request.Headers["Authorization"] != _authorization)
            {
                response.StatusCode = 401;
                response.AddHeader("WWW-Authenticate", "Basic realm=\"alcove-test-feed\"");
                return;
            }

            var segments = path.Trim('/').Split('/');
            switch (segments)
            {
                case ["v3", "index.json"]:
                    Json(response, ServiceIndexJson());
                    break;
                case ["flat", var id, "index.json"] when _packages.TryGetValue(id, out var versions):
                    Json(response, new JsonObject { ["versions"] = new JsonArray([.. versions.OrderBy(v => v.Version).Select(v => (JsonNode)Lower(v.Version))]) });
                    break;
                case ["flat", var id, var version, var file] when Find(id, version) is { } entry:
                    if (file.Equals($"{id}.{version}.nupkg", StringComparison.OrdinalIgnoreCase))
                    {
                        Bytes(response, File.ReadAllBytes(entry.Path), "application/octet-stream");
                    }
                    else if (file.Equals($"{id}.nuspec", StringComparison.OrdinalIgnoreCase))
                    {
                        Bytes(response, entry.Nuspec, "application/xml");
                    }
                    else
                    {
                        response.StatusCode = 404;
                    }

                    break;
                case ["query"]:
                    Json(response, Search(context.Request.QueryString));
                    break;
                default:
                    response.StatusCode = 404;
                    break;
            }
        }
        catch (Exception ex)
        {
            response.StatusCode = 500;
            Bytes(response, Encoding.UTF8.GetBytes(ex.ToString()), "text/plain");
        }
        finally
        {
            lock (_requests)
            {
                _requests.Add($"{response.StatusCode} {context.Request.HttpMethod} {path}");
            }

            try
            {
                response.Close();
            }
            catch (HttpListenerException)
            {
                // The client went away.
            }
        }
    }

    private JsonObject ServiceIndexJson() => new()
    {
        ["version"] = "3.0.0",
        ["resources"] = new JsonArray(
            Resource("flat/", "PackageBaseAddress/3.0.0"),
            Resource("query", "SearchQueryService"),
            Resource("query", "SearchQueryService/3.0.0-rc"),
            Resource("query", "SearchQueryService/3.5.0")),
    };

    private JsonObject Resource(string relative, string type) => new() { ["@id"] = new Uri(BaseAddress, relative).ToString(), ["@type"] = type };

    private JsonObject Search(System.Collections.Specialized.NameValueCollection query)
    {
        var term = query["q"] ?? "";
        var packageType = query["packageType"];
        var prerelease = string.Equals(query["prerelease"], "true", StringComparison.OrdinalIgnoreCase);
        var skip = int.TryParse(query["skip"], out var s) ? s : 0;
        var take = int.TryParse(query["take"], out var t) ? t : 20;
        var hits = _packages.Values
            .Select(versions => versions.Where(v => prerelease || !v.Version.IsPrerelease).MaxBy(v => v.Version))
            .OfType<FeedEntry>()
            .Where(e => e.Id.Contains(term, StringComparison.OrdinalIgnoreCase))
            .Where(e => string.IsNullOrEmpty(packageType) || e.PackageTypes.Contains(packageType, StringComparer.OrdinalIgnoreCase))
            .OrderBy(e => e.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new JsonObject
        {
            ["totalHits"] = hits.Count,
            ["data"] = new JsonArray([.. hits.Skip(skip).Take(take).Select(e => (JsonNode)new JsonObject
            {
                ["@id"] = new Uri(BaseAddress, $"registration/{e.Id.ToLowerInvariant()}/index.json").ToString(),
                ["@type"] = "Package",
                ["id"] = e.Id,
                ["version"] = e.Version.ToNormalizedString(),
                ["title"] = e.Title,
                ["description"] = e.Description,
                ["authors"] = new JsonArray(e.Authors),
                ["totalDownloads"] = 0,
                ["packageTypes"] = new JsonArray([.. e.PackageTypes.Select(p => (JsonNode)new JsonObject { ["name"] = p })]),
                ["versions"] = new JsonArray([.. _packages[e.Id].OrderBy(v => v.Version).Select(v => (JsonNode)new JsonObject
                {
                    ["version"] = v.Version.ToNormalizedString(),
                    ["downloads"] = 0,
                    ["@id"] = new Uri(BaseAddress, $"registration/{e.Id.ToLowerInvariant()}/{Lower(v.Version)}.json").ToString(),
                })]),
            })]),
        };
    }

    private FeedEntry? Find(string id, string version) =>
        _packages.TryGetValue(id, out var versions) && NuGetVersion.TryParse(version, out var parsed)
            ? versions.FirstOrDefault(v => v.Version == parsed)
            : null;

    private static string Lower(NuGetVersion version) => version.ToNormalizedString().ToLowerInvariant();

    private static void Json(HttpListenerResponse response, JsonNode json) =>
        Bytes(response, JsonSerializer.SerializeToUtf8Bytes(json), "application/json");

    private static void Bytes(HttpListenerResponse response, byte[] content, string contentType)
    {
        response.ContentType = contentType;
        response.ContentLength64 = content.Length;
        response.OutputStream.Write(content);
    }

    private sealed record FeedEntry(string Id, NuGetVersion Version, string Path, byte[] Nuspec, string? Title, string? Description, string? Authors, IReadOnlyList<string> PackageTypes);
}
