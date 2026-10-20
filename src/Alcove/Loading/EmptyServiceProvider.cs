namespace Alcove.Loading;

/// <summary>The services of a host that supplies none: every request returns <see langword="null"/>.</summary>
internal sealed class EmptyServiceProvider : IServiceProvider
{
    public static EmptyServiceProvider Instance { get; } = new();

    public object? GetService(Type serviceType) => null;
}
