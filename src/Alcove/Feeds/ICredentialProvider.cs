using System.Net;

namespace Alcove.Feeds;

/// <summary>
/// Supplies credentials for an authenticated feed, e.g. from a login dialog or a secret store. Set it as
/// <see cref="PluginHostOptions.CredentialProvider"/>. It is asked only when a feed answers 401 (or 403) and the
/// feed's own credentials (<see cref="PackageFeed.Credentials"/>, or the <c>nuget.config</c>'s) were missing or rejected.
/// </summary>
public interface ICredentialProvider
{
    /// <summary>Gets credentials for a feed.</summary>
    /// <param name="feed">The feed's source URL.</param>
    /// <param name="isRetry"><see langword="true"/> when credentials returned earlier for this feed were rejected.</param>
    /// <param name="cancellationToken">Cancels the request (e.g. the install was cancelled).</param>
    /// <returns>The credentials, or <see langword="null"/> to give up (the feed request then fails as unauthorized).</returns>
    Task<NetworkCredential?> GetCredentialsAsync(Uri feed, bool isRetry, CancellationToken cancellationToken = default);
}
