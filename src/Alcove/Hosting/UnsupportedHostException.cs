namespace Alcove.Hosting;

/// <summary>
/// The host app is deployed in a way Alcove can't work with, e.g. as a single-file app, which exposes no
/// <c>deps.json</c> to build the host inventory from.
/// </summary>
public sealed class UnsupportedHostException : NotSupportedException
{
    /// <summary>Creates the exception.</summary>
    public UnsupportedHostException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">What is unsupported and what to do instead.</param>
    public UnsupportedHostException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and an inner exception.</summary>
    /// <param name="message">What is unsupported and what to do instead.</param>
    /// <param name="innerException">The cause.</param>
    public UnsupportedHostException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
