namespace Alcove.Feeds;

/// <summary>Options of <see cref="PluginHost.SearchAsync(string, SearchOptions, CancellationToken)"/>.</summary>
public sealed class SearchOptions
{
    /// <summary>How many results to skip on each feed (paging).</summary>
    public int Skip { get; set; }

    /// <summary>The most results to ask each feed for. Defaults to 20.</summary>
    public int Take { get; set; } = 20;

    /// <summary>Whether prerelease versions are included; <see langword="null"/> uses <see cref="PluginHostOptions.AllowPrerelease"/>.</summary>
    public bool? IncludePrerelease { get; set; }
}
