using System.Net;
using NuGet.Configuration;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;

namespace Alcove.Internal.Feeds;

/// <summary>
/// Creates the HTTP handler of an HTTP feed like NuGet's <see cref="HttpHandlerResourceV3Provider"/>, but with this
/// plugin host's <see cref="CredentialService"/> instead of NuGet's process-wide one. Proxies come from the system
/// settings (<c>HTTP_PROXY</c>, ...), not from NuGet's user settings.
/// </summary>
internal sealed class HttpHandlerResourceProvider(ICredentialService credentials)
    : ResourceProvider(typeof(HttpHandlerResource), nameof(HttpHandlerResourceProvider), NuGetResourceProviderPositions.First)
{
    public override Task<Tuple<bool, INuGetResource?>> TryCreate(SourceRepository source, CancellationToken token)
    {
        if (!source.PackageSource.IsHttp)
        {
            return Task.FromResult(new Tuple<bool, INuGetResource?>(false, null));
        }

        var packageSource = source.PackageSource;
#pragma warning disable CA2000 // Owned by the resource, which lives as long as the source repository.
        var clientHandler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate };
#pragma warning restore CA2000
        if (packageSource.DisableTLSCertificateValidation)
        {
            clientHandler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        if (packageSource.ClientCertificates is { } certificates)
        {
            clientHandler.ClientCertificates.AddRange(certificates.ToArray());
        }

        HttpMessageHandler handler = new HttpSourceAuthenticationHandler(packageSource, clientHandler, credentials)
        {
            InnerHandler = new ServerWarningLogHandler(clientHandler),
        };
        return Task.FromResult(new Tuple<bool, INuGetResource?>(true, new HttpHandlerResourceV3(clientHandler, handler)));
    }
}
