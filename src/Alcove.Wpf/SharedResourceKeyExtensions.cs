using System.Diagnostics.CodeAnalysis;
using System.Windows;

namespace Alcove.Wpf;

/// <summary>Typed lookups and registrations with <see cref="SharedResourceKey{T}"/>.</summary>
public static class SharedResourceKeyExtensions
{
    /// <summary>Finds the resource for <paramref name="key"/> from <paramref name="element"/> up to the application's resources.</summary>
    /// <typeparam name="T">The resource's type.</typeparam>
    /// <param name="element">Where the lookup starts.</param>
    /// <param name="key">The key.</param>
    /// <param name="value">The resource, if one of type <typeparamref name="T"/> was found.</param>
    /// <returns>Whether it was found with that type.</returns>
    public static bool TryFindResource<T>(this FrameworkElement element, SharedResourceKey<T> key, [MaybeNullWhen(false)] out T value)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(key);
        return As(element.TryFindResource(key), out value);
    }

    /// <summary>Finds the resource for <paramref name="key"/> in the application's resources.</summary>
    /// <typeparam name="T">The resource's type.</typeparam>
    /// <param name="application">The application.</param>
    /// <param name="key">The key.</param>
    /// <param name="value">The resource, if one of type <typeparamref name="T"/> was found.</param>
    /// <returns>Whether it was found with that type.</returns>
    public static bool TryFindResource<T>(this Application application, SharedResourceKey<T> key, [MaybeNullWhen(false)] out T value)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(key);
        return As(application.TryFindResource(key), out value);
    }

    /// <summary>Adds or replaces the resource for <paramref name="key"/>, e.g. in the application's resources at startup.</summary>
    /// <typeparam name="T">The resource's type.</typeparam>
    /// <param name="dictionary">The dictionary.</param>
    /// <param name="key">The key.</param>
    /// <param name="value">The resource.</param>
    public static void SetResource<T>(this ResourceDictionary dictionary, SharedResourceKey<T> key, T value)
    {
        ArgumentNullException.ThrowIfNull(dictionary);
        ArgumentNullException.ThrowIfNull(key);
        dictionary[key] = value;
    }

    /// <summary>Binds a property of <paramref name="element"/> to the resource, like <c>{DynamicResource}</c> (it follows later changes).</summary>
    /// <typeparam name="T">The resource's type.</typeparam>
    /// <param name="element">The element.</param>
    /// <param name="property">The property to bind.</param>
    /// <param name="key">The key.</param>
    public static void SetResourceReference<T>(this FrameworkElement element, DependencyProperty property, SharedResourceKey<T> key)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(key);
        element.SetResourceReference(property, key);
    }

    private static bool As<T>(object? found, [MaybeNullWhen(false)] out T value)
    {
        if (found is T typed)
        {
            value = typed;
            return true;
        }

        value = default;
        return false;
    }
}
