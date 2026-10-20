using System.Collections.Concurrent;
using System.Net;
using NuGet.Configuration;

namespace Alcove.Feeds;

/// <summary>
/// NuGet's credential hook for one plugin host: asks the app's <see cref="ICredentialProvider"/> when a feed answers
/// 401/403 and its own credentials (from <see cref="PackageFeed.Credentials"/> or the <c>nuget.config</c>) are missing or
/// rejected. Set per source repository, never through NuGet's process-wide <c>HttpHandlerResourceV3.CredentialService</c>.
/// </summary>
internal sealed class CredentialService(ICredentialProvider? provider, IReadOnlySet<Uri> sourcesWithCredentials) : ICredentialService
{
    private readonly ConcurrentDictionary<Uri, int> _requests = new();

    /// <summary>Default network credentials (Windows integrated authentication) are tried first, as NuGet does.</summary>
    public bool HandlesDefaultCredentials => false;

    public async Task<ICredentials?> GetCredentialsAsync(Uri uri, IWebProxy? proxy, CredentialRequestType type, string message, CancellationToken cancellationToken)
    {
        if (provider is null || type == CredentialRequestType.Proxy)
        {
            return null;
        }

        var requests = _requests.AddOrUpdate(uri, 1, (_, count) => count + 1);
        var isRetry = requests > 1 || sourcesWithCredentials.Contains(uri);
        return await provider.GetCredentialsAsync(uri, isRetry, cancellationToken).ConfigureAwait(false);
    }

    public bool TryGetLastKnownGoodCredentialsFromCache(Uri uri, bool isProxy, out ICredentials? credentials)
    {
        credentials = null;
        return false;
    }
}
