using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Alcove.Internal.Metadata;

/// <summary>
/// Whether a plugin's entry point type implements one of a set of interfaces, named by full name (e.g.
/// <c>Alcove.DependencyInjection.IServicePlugin</c>), read from metadata only: nothing is loaded, so a hot install
/// can decide before it touches a running cell (<see cref="ICellServicesFactory.ServicePluginInterfaces"/>).
/// </summary>
/// <remarks>
/// The entry point's interfaces and base types are followed, recursively, into every assembly
/// <c>resolveAssembly</c> finds a file for (the plugin's own assemblies, the other private assemblies of its cell and
/// the host's assemblies). A type whose assembly has no file is matched by its name only: its own base types and
/// interfaces aren't followed.
/// </remarks>
internal static class EntryPointInterfaceDetector
{
    /// <summary>Whether the entry point implements one of <paramref name="interfaceNames"/>, directly or inherited.</summary>
    /// <param name="entryPoint">The entry point's full type name, nested types with <c>+</c>.</param>
    /// <param name="ownAssemblyPaths">The plugin's own assemblies, where the entry point is defined.</param>
    /// <param name="interfaceNames">The interfaces' full names.</param>
    /// <param name="resolveAssembly">The file of an assembly referenced by simple name, or <see langword="null"/> if it isn't followed.</param>
    /// <returns><see langword="false"/> also if the entry point isn't found or the metadata can't be read (activation reports those).</returns>
    public static bool Implements(
        string entryPoint, IEnumerable<string> ownAssemblyPaths, IReadOnlyCollection<string> interfaceNames, Func<string, string?> resolveAssembly)
    {
        ArgumentNullException.ThrowIfNull(entryPoint);
        ArgumentNullException.ThrowIfNull(ownAssemblyPaths);
        ArgumentNullException.ThrowIfNull(interfaceNames);
        ArgumentNullException.ThrowIfNull(resolveAssembly);
        if (interfaceNames.Count == 0)
        {
            return false;
        }

        var wanted = interfaceNames.ToHashSet(StringComparer.Ordinal);
        using var readers = new Readers(resolveAssembly);
        try
        {
            var start = ownAssemblyPaths
                .Select(readers.OpenPath)
                .OfType<MetadataReader>()
                .Select(md => (Reader: md, Type: Find(md, entryPoint)))
                .FirstOrDefault(t => t.Type is not null);
            return start.Type is { } startType && ImplementsAny(start.Reader, startType, wanted, readers);
        }
        catch (BadImageFormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// The public, non-abstract, non-generic classes of <paramref name="ownAssemblyPaths"/> that implement
    /// <paramref name="interfaceName"/>, directly or inherited (as <see cref="Implements"/> follows them), by full name
    /// (nested types with <c>+</c>), in assembly and metadata order. Used to find a plugin's entry point when its
    /// manifest names none.
    /// </summary>
    /// <exception cref="BadImageFormatException">An assembly's metadata can't be read.</exception>
    public static IReadOnlyList<string> FindImplementations(IEnumerable<string> ownAssemblyPaths, string interfaceName, Func<string, string?> resolveAssembly)
    {
        ArgumentNullException.ThrowIfNull(ownAssemblyPaths);
        ArgumentNullException.ThrowIfNull(interfaceName);
        ArgumentNullException.ThrowIfNull(resolveAssembly);
        var wanted = new HashSet<string>(StringComparer.Ordinal) { interfaceName };
        var found = new List<string>();
        using var readers = new Readers(resolveAssembly);
        foreach (var md in ownAssemblyPaths.Select(readers.OpenPath).OfType<MetadataReader>())
        {
            foreach (var handle in md.TypeDefinitions)
            {
                if (IsInstantiablePublicClass(md, handle) && ImplementsAny(md, handle, wanted, readers))
                {
                    found.Add(FullName(md, handle));
                }
            }
        }

        return found;
    }

    private static bool IsInstantiablePublicClass(MetadataReader md, TypeDefinitionHandle handle)
    {
        var type = md.GetTypeDefinition(handle);
        if ((type.Attributes & (TypeAttributes.Interface | TypeAttributes.Abstract)) != 0 || type.GetGenericParameters().Count > 0)
        {
            return false;
        }

        // Public, or nested public in public types all the way out.
        for (var current = type; ; current = md.GetTypeDefinition(current.GetDeclaringType()))
        {
            var visibility = current.Attributes & TypeAttributes.VisibilityMask;
            if (current.GetDeclaringType().IsNil)
            {
                if (visibility != TypeAttributes.Public)
                {
                    return false;
                }

                break;
            }

            if (visibility != TypeAttributes.NestedPublic)
            {
                return false;
            }
        }

        // Not a struct or an enum.
        return type.BaseType.IsNil || type.BaseType.Kind != HandleKind.TypeReference
            || FullName(md, (TypeReferenceHandle)type.BaseType) is not ("System.ValueType" or "System.Enum");
    }

    /// <summary>Whether a type implements one of <paramref name="wanted"/>, following its supertypes as far as <paramref name="readers"/> resolves them.</summary>
    private static bool ImplementsAny(MetadataReader startReader, TypeDefinitionHandle startType, HashSet<string> wanted, Readers readers)
    {
        var visited = new HashSet<(string Assembly, string Type)>();
        var pending = new Stack<(MetadataReader Reader, TypeDefinitionHandle Type)>();
        pending.Push((startReader, startType));
        while (pending.TryPop(out var current))
        {
            var (md, handle) = current;
            if (!visited.Add((MetadataFile.SimpleName(md), FullName(md, handle))))
            {
                continue;
            }

            var type = md.GetTypeDefinition(handle);
            var supertypes = type.GetInterfaceImplementations().Select(i => md.GetInterfaceImplementation(i).Interface).ToList();
            if (!type.BaseType.IsNil)
            {
                supertypes.Add(type.BaseType);
            }

            foreach (var supertype in supertypes)
            {
                if (Resolve(md, supertype, readers, wanted) is not { } resolved)
                {
                    continue;
                }

                if (wanted.Contains(resolved.Name))
                {
                    return true;
                }

                if (resolved.Definition is { } definition)
                {
                    pending.Push(definition);
                }
            }
        }

        return false;
    }

    /// <summary>A supertype's full name and, if its assembly is followed, its definition.</summary>
    private static (string Name, (MetadataReader, TypeDefinitionHandle)? Definition)? Resolve(MetadataReader md, EntityHandle handle, Readers readers, HashSet<string> wanted)
    {
        switch (handle.Kind)
        {
            case HandleKind.TypeDefinition:
                var definition = (TypeDefinitionHandle)handle;
                return (FullName(md, definition), (md, definition));

            case HandleKind.TypeReference:
                var reference = (TypeReferenceHandle)handle;
                var name = FullName(md, reference);
                if (wanted.Contains(name))
                {
                    return (name, null);   // found: no need to open its assembly
                }

                var target = ScopeAssembly(md, reference) is { } assembly ? readers.OpenAssembly(assembly) : md;
                return target is not null && Find(target, name) is { } found ? (name, (target, found)) : (name, null);

            case HandleKind.TypeSpecification:
                // A generic instantiation (Base<T>, IFoo<T>): follow its generic type definition.
                var blob = md.GetBlobReader(md.GetTypeSpecification((TypeSpecificationHandle)handle).Signature);
                if (blob.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance)
                {
                    return null;
                }

                blob.ReadSignatureTypeCode();   // class or value type
                var generic = blob.ReadTypeHandle();
                return generic.Kind == HandleKind.TypeSpecification ? null : Resolve(md, (EntityHandle)generic, readers, wanted);

            default:
                return null;
        }
    }

    /// <summary>The simple name of the assembly a type reference points to; <see langword="null"/> for the same assembly.</summary>
    private static string? ScopeAssembly(MetadataReader md, TypeReferenceHandle handle)
    {
        var scope = md.GetTypeReference(handle).ResolutionScope;
        while (scope.Kind == HandleKind.TypeReference)
        {
            scope = md.GetTypeReference((TypeReferenceHandle)scope).ResolutionScope;
        }

        return scope.Kind == HandleKind.AssemblyReference
            ? md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)scope).Name)
            : null;
    }

    private static TypeDefinitionHandle? Find(MetadataReader md, string fullName)
    {
        foreach (var handle in md.TypeDefinitions)
        {
            if (string.Equals(FullName(md, handle), fullName, StringComparison.Ordinal))
            {
                return handle;
            }
        }

        return null;
    }

    private static string FullName(MetadataReader md, TypeDefinitionHandle handle)
    {
        var type = md.GetTypeDefinition(handle);
        var declaring = type.GetDeclaringType();
        return declaring.IsNil
            ? Qualify(md.GetString(type.Namespace), md.GetString(type.Name))
            : FullName(md, declaring) + "+" + md.GetString(type.Name);
    }

    private static string FullName(MetadataReader md, TypeReferenceHandle handle)
    {
        var type = md.GetTypeReference(handle);
        return type.ResolutionScope.Kind == HandleKind.TypeReference
            ? FullName(md, (TypeReferenceHandle)type.ResolutionScope) + "+" + md.GetString(type.Name)
            : Qualify(md.GetString(type.Namespace), md.GetString(type.Name));
    }

    private static string Qualify(string ns, string name) => ns.Length == 0 ? name : ns + "." + name;

    /// <summary>The metadata readers opened so far, by simple name and by path; disposes them.</summary>
    private sealed class Readers(Func<string, string?> resolveAssembly) : IDisposable
    {
        private readonly Dictionary<string, MetadataReader?> _byPath = new(StringComparer.Ordinal);
        private readonly Dictionary<string, MetadataReader?> _byName = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<PEReader> _open = [];

        public MetadataReader? OpenAssembly(string simpleName)
        {
            if (!_byName.TryGetValue(simpleName, out var md))
            {
                md = resolveAssembly(simpleName) is { } path ? OpenPath(path) : null;
                _byName[simpleName] = md;
            }

            return md;
        }

        public MetadataReader? OpenPath(string path)
        {
            if (_byPath.TryGetValue(path, out var md))
            {
                return md;
            }

            PEReader? pe = null;
            try
            {
                pe = File.Exists(path) ? MetadataFile.TryOpen(path, out _) : null;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            if (pe is not null)
            {
                _open.Add(pe);
                md = pe.GetMetadataReader();
                _byName.TryAdd(MetadataFile.SimpleName(md), md);
            }

            _byPath[path] = md;
            return md;
        }

        public void Dispose()
        {
            foreach (var pe in _open)
            {
                pe.Dispose();
            }
        }
    }
}
