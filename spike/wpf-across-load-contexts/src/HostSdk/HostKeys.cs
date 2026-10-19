using System.Windows;

namespace HostSdk;

/// <summary>Typed resource keys defined by the host (what the WPF add-on's ResourceKey helpers would produce).</summary>
public static class HostKeys
{
    public static readonly ComponentResourceKey AccentBrush = new(typeof(HostKeys), nameof(AccentBrush));

    /// <summary>Plain string key, for comparison with the typed key.</summary>
    public const string Title = "Host.Title";

    public static readonly Uri ThemeUri = new("pack://application:,,,/HostSdk;component/Themes/HostStyles.xaml", UriKind.Absolute);
}
