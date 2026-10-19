namespace Alcove;

/// <summary>
/// Marks a type as an extension of an extension point. A plugin without an entry point is scanned for
/// these; implementing an interface alone isn't enough.
/// </summary>
/// <remarks>
/// Hosts subclass this to add typed metadata, e.g. <c>[ToolWindow(Title = "Charts")]</c>. The attribute
/// instance is the extension's metadata, readable without creating the extension. Only attributes declared
/// on the extension type itself count, never those of its base classes.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public class ExtensionAttribute : Attribute
{
    /// <summary>Creates the attribute for an extension point.</summary>
    /// <param name="extensionPoint">The extension point the marked type implements.</param>
    public ExtensionAttribute(Type extensionPoint)
    {
        ArgumentNullException.ThrowIfNull(extensionPoint);
        ExtensionPoint = extensionPoint;
    }

    /// <summary>The extension point the marked type implements.</summary>
    public Type ExtensionPoint { get; }

    /// <summary>The lifetime of an attribute-discovered extension. Defaults to <see cref="ExtensionLifetime.Singleton"/>.</summary>
    public ExtensionLifetime Lifetime { get; init; } = ExtensionLifetime.Singleton;
}
