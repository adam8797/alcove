namespace Alcove.Wpf.Resources;

/// <summary>
/// A typed key for a resource the app shares with plugins, defined once as a constant in the app's contracts package
/// or app SDK (which are host-owned, so every cell sees the same key):
/// <code>public static readonly SharedResourceKey&lt;Brush&gt; AccentBrush = new("MyApp.AccentBrush");</code>
/// </summary>
/// <remarks>
/// Keys are equal when their <see cref="Id"/> and value type are, so a key is safe to use as a dictionary key in XAML
/// (<c>x:Key="{x:Static app:AppKeys.AccentBrush}"</c>) and in lookups (<c>{DynamicResource {x:Static app:AppKeys.AccentBrush}}</c>).
/// From code, the extensions in <see cref="SharedResourceKeyExtensions"/> return the value with its type. Use a
/// namespaced <see cref="Id"/>, e.g. <c>MyApp.AccentBrush</c>, so plugins' own keys can't collide with it.
/// </remarks>
/// <typeparam name="T">The type of the resource's value, e.g. <c>Brush</c>.</typeparam>
/// <param name="Id">The key's name, unique within the app.</param>
public sealed record SharedResourceKey<T>(string Id)
{
    /// <summary>The key's name, unique within the app.</summary>
    public string Id { get; } = !string.IsNullOrWhiteSpace(Id) ? Id : throw new ArgumentException("A resource key needs an ID.", nameof(Id));

    /// <summary>The type of the resource's value.</summary>
    public Type ValueType => typeof(T);

    /// <inheritdoc />
    public override string ToString() => $"{Id} ({typeof(T).Name})";
}
