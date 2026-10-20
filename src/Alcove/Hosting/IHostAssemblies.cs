namespace Alcove.Hosting;

/// <summary>What a cell asks the host inventory while resolving: is a name host-owned or host-shared?</summary>
internal interface IHostAssemblies
{
    /// <summary>Whether the assembly is host-owned (shared frameworks, Alcove.Abstractions and its dependencies, the app's host-owned packages).</summary>
    bool IsHostOwnedAssembly(string assemblyName);

    /// <summary>Whether the host provides the assembly and it isn't host-owned.</summary>
    bool IsHostSharedAssembly(string assemblyName);
}
