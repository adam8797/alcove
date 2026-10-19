namespace Alcove;

/// <summary>How many instances of an extension the catalog creates.</summary>
public enum ExtensionLifetime
{
    /// <summary>One shared instance, created on first use. The default.</summary>
    Singleton = 0,

    /// <summary>A new instance each time one is requested, e.g. a window that can be open twice.</summary>
    Transient = 1,
}
