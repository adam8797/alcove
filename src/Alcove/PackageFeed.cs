using System.Net;
using System.Text.Json.Serialization;

namespace Alcove;

/// <summary>A NuGet feed plugins are installed from: a V3 service index URL or a local folder.</summary>
/// <param name="Source">The feed URL (e.g. <c>https://nuget.example.com/v3/index.json</c>) or a folder path.</param>
public sealed record PackageFeed(string Source)
{
    /// <summary>An optional display name, used in logs and messages.</summary>
    public string? Name { get; init; }

    /// <summary>
    /// Optional credentials (user name and password or API token) sent when the feed asks for authentication.
    /// Code only; never in the configuration file. Feeds without credentials can still authenticate through the
    /// <c>nuget.config</c> (<see cref="PluginHostOptions.NuGetConfigPath"/>) or the app's
    /// <see cref="PluginHostOptions.CredentialProvider"/>.
    /// </summary>
    [JsonIgnore]
    public NetworkCredential? Credentials { get; init; }
}
