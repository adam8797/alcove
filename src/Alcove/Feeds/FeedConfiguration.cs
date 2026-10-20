using NuGet.Configuration;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;

namespace Alcove.Feeds;

/// <summary>
/// The feeds of one plugin host: <see cref="PluginHostOptions.Feeds"/> plus the enabled sources of
/// <see cref="PluginHostOptions.NuGetConfigPath"/>, as NuGet source repositories. Built once per host, so each
/// feed keeps one HTTP handler (and the credentials it obtained) for the host's lifetime.
/// </summary>
internal sealed class FeedConfiguration
{
    private FeedConfiguration(IReadOnlyList<SourceRepository> repositories)
    {
        Repositories = repositories;
    }

    /// <summary>The feeds, in order: the options' feeds first, then the <c>nuget.config</c>'s.</summary>
    public IReadOnlyList<SourceRepository> Repositories { get; }

    /// <summary>Builds the feeds of a host.</summary>
    /// <exception cref="InvalidOperationException">The <c>nuget.config</c> can't be read.</exception>
    public static FeedConfiguration Create(PluginHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var sources = new List<PackageSource>();
        foreach (var feed in options.Feeds)
        {
            if (string.IsNullOrWhiteSpace(feed.Source))
            {
                continue;
            }

            var source = new PackageSource(feed.Source, feed.Name ?? feed.Source);
            if (feed.Credentials is { } credentials)
            {
                source.Credentials = PackageSourceCredential.FromUserInput(
                    source.Name, credentials.UserName, credentials.Password, storePasswordInClearText: true, validAuthenticationTypesText: null);
            }

            sources.Add(source);
        }

        if (!string.IsNullOrWhiteSpace(options.NuGetConfigPath))
        {
            ISettings settings;
            try
            {
                settings = Settings.LoadSettingsGivenConfigPaths([Path.GetFullPath(options.NuGetConfigPath)]);
            }
            catch (Exception ex) when (ex is NuGetConfigurationException or IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException($"The nuget.config '{options.NuGetConfigPath}' can't be read: {ex.Message}", ex);
            }

            foreach (var source in new PackageSourceProvider(settings).LoadPackageSources().Where(s => s.IsEnabled))
            {
                if (!sources.Any(s => string.Equals(s.Source, source.Source, StringComparison.OrdinalIgnoreCase)))
                {
                    sources.Add(source);
                }
            }
        }

        var withCredentials = sources.Where(s => s.IsHttp && s.Credentials is not null).Select(s => s.SourceUri).ToHashSet();
        var httpHandlers = new HttpHandlerResourceProvider(new CredentialService(options.CredentialProvider, withCredentials));
        var providers = Repository.Provider.GetCoreV3()
            .Where(p => p.Value is not HttpHandlerResourceV3Provider)
            .Append(new Lazy<INuGetResourceProvider>(() => httpHandlers))
            .ToList();
        return new FeedConfiguration(sources.Select(s => new SourceRepository(s, providers)).ToList());
    }
}
