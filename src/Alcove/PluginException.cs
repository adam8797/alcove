using Alcove.Validation;

namespace Alcove;

/// <summary>
/// A plugin faulted where the caller has to know: an extension couldn't be created, or a fault while
/// <see cref="PluginHostOptions.FailFast"/> is set. Also thrown by <see cref="PluginHost.LoadAsync"/> when validation
/// finds a global error (e.g. an unreadable state file); <see cref="Report"/> then holds the report.
/// </summary>
public sealed class PluginException : Exception
{
    /// <summary>Creates the exception.</summary>
    public PluginException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">What failed.</param>
    public PluginException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and the cause.</summary>
    /// <param name="message">What failed.</param>
    /// <param name="innerException">The exception the plugin threw.</param>
    public PluginException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    internal PluginException(string message, string? pluginId, Exception? innerException, ValidationReport? report = null)
        : base(message, innerException)
    {
        PluginId = pluginId;
        Report = report;
    }

    /// <summary>The plugin concerned, or <see langword="null"/> for a global failure.</summary>
    public string? PluginId { get; }

    /// <summary>The validation report, when validation made loading impossible.</summary>
    public ValidationReport? Report { get; }
}
