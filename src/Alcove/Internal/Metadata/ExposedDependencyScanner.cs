using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Alcove.Internal.Metadata;

/// <summary>
/// Finds a plugin's <b>exposed dependencies</b> (ADR 0014): the referenced assemblies whose types appear in the
/// public surface of the plugin's assemblies. Reads metadata only (spike: cross-compartment-deps, Q3).
/// </summary>
/// <remarks>
/// <para>The public surface is everything code outside the assembly can see:</para>
/// <list type="bullet">
///   <item>public types, and nested public/protected types reachable from them (protected only in unsealed types);</item>
///   <item>their base types, interfaces, generic parameter constraints and custom attributes;</item>
///   <item>public and protected (unsealed types only) methods, properties, events and fields: their full signatures
///   including generic arguments, generic method constraints, and the custom attributes on them and their parameters;</item>
///   <item>type forwarders.</item>
/// </list>
/// <para>Method bodies, private/internal/private-protected members and non-visible types never count. The plugin's own
/// assemblies (all of <c>assemblyPaths</c>) are never reported, nor anything <c>isHostOwned</c> accepts.</para>
/// </remarks>
internal static class ExposedDependencyScanner
{
    /// <summary>Scans a plugin's assemblies.</summary>
    /// <param name="assemblyPaths">The plugin's own assemblies (e.g. its <c>lib/&lt;tfm&gt;/*.dll</c>).</param>
    /// <param name="isHostOwned">
    /// Returns <see langword="true"/> for simple names the host provides to every cell (shared frameworks,
    /// <c>Alcove.Abstractions</c>, host-owned packages); those are never exposed dependencies.
    /// </param>
    public static ExposedDependencyScanResult Scan(IEnumerable<string> assemblyPaths, Func<string, bool> isHostOwned)
    {
        ArgumentNullException.ThrowIfNull(assemblyPaths);
        ArgumentNullException.ThrowIfNull(isHostOwned);

        var readers = new List<(string Path, PEReader Reader)>();
        var skipped = ImmutableArray.CreateBuilder<SkippedFile>();
        try
        {
            foreach (var path in assemblyPaths)
            {
                var reader = MetadataFile.TryOpen(path, out var reason);
                if (reader is null)
                {
                    skipped.Add(new SkippedFile(path, reason!));
                    continue;
                }

                readers.Add((path, reader));
            }

            var own = readers.Select(r => MetadataFile.SimpleName(r.Reader.GetMetadataReader()))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var found = new Dictionary<string, (Version? Version, List<ExposureSite> Sites)>(StringComparer.OrdinalIgnoreCase);
            foreach (var (path, reader) in readers)
            {
                ScanAssembly(reader.GetMetadataReader(), Path.GetFileName(path), (name, version, site) =>
                {
                    if (own.Contains(name) || isHostOwned(name))
                    {
                        return;
                    }

                    if (!found.TryGetValue(name, out var entry))
                    {
                        entry = (version, []);
                    }
                    else if (version is not null && (entry.Version is null || version > entry.Version))
                    {
                        entry.Version = version;
                    }

                    entry.Sites.Add(site);
                    found[name] = entry;
                });
            }

            var dependencies = found
                .OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
                .Select(e => new ExposedDependency(e.Key, e.Value.Version, [.. e.Value.Sites]))
                .ToImmutableArray();
            return new ExposedDependencyScanResult(dependencies, skipped.ToImmutable());
        }
        finally
        {
            foreach (var (_, reader) in readers)
            {
                reader.Dispose();
            }
        }
    }

    /// <summary>Reports every (referenced assembly, version, site) in one assembly's public surface.</summary>
    internal static void ScanAssembly(MetadataReader md, string assemblyFile, Action<string, Version?, ExposureSite> report)
    {
        var collector = new ReferencedAssemblyCollector(md);

        void Flush(string member, ExposureKind kind)
        {
            foreach (var (name, version) in collector.Assemblies)
            {
                report(name, version, new ExposureSite(assemblyFile, member, kind));
            }

            collector.Clear();
        }

        void VisitAttributes(CustomAttributeHandleCollection attributes)
        {
            foreach (var attribute in attributes)
            {
                collector.VisitAttribute(attribute);
            }
        }

        void VisitGenericParameters(GenericParameterHandleCollection parameters, string member)
        {
            foreach (var handle in parameters)
            {
                var parameter = md.GetGenericParameter(handle);
                foreach (var constraintHandle in parameter.GetConstraints())
                {
                    var constraint = md.GetGenericParameterConstraint(constraintHandle);
                    collector.Visit(constraint.Type);
                    VisitAttributes(constraint.GetCustomAttributes());
                }

                Flush(member, ExposureKind.GenericConstraint);
                VisitAttributes(parameter.GetCustomAttributes());
                Flush(member, ExposureKind.CustomAttribute);
            }
        }

        foreach (var typeHandle in md.TypeDefinitions)
        {
            var type = md.GetTypeDefinition(typeHandle);
            if (!Visibility.IsTypeVisible(md, type))
            {
                continue;
            }

            var typeName = TypeName(md, type);
            var isSealed = Visibility.IsSealed(type);

            if (!type.BaseType.IsNil)
            {
                collector.Visit(type.BaseType);
                Flush(typeName, ExposureKind.BaseType);
            }

            foreach (var implementation in type.GetInterfaceImplementations())
            {
                collector.Visit(md.GetInterfaceImplementation(implementation).Interface);
            }

            Flush(typeName, ExposureKind.Interface);
            VisitGenericParameters(type.GetGenericParameters(), typeName);
            VisitAttributes(type.GetCustomAttributes());
            Flush(typeName, ExposureKind.CustomAttribute);

            foreach (var fieldHandle in type.GetFields())
            {
                var field = md.GetFieldDefinition(fieldHandle);
                if (!Visibility.IsVisible(field.Attributes, isSealed))
                {
                    continue;
                }

                var member = $"{typeName}.{md.GetString(field.Name)}";
                field.DecodeSignature(collector, null);
                Flush(member, ExposureKind.Field);
                VisitAttributes(field.GetCustomAttributes());
                Flush(member, ExposureKind.CustomAttribute);
            }

            foreach (var methodHandle in type.GetMethods())
            {
                var method = md.GetMethodDefinition(methodHandle);
                if (!Visibility.IsVisible(method.Attributes, isSealed))
                {
                    continue;
                }

                var member = $"{typeName}.{md.GetString(method.Name)}";
                method.DecodeSignature(collector, null);
                Flush(member, ExposureKind.Method);
                VisitGenericParameters(method.GetGenericParameters(), member);
                VisitAttributes(method.GetCustomAttributes());
                foreach (var parameterHandle in method.GetParameters())
                {
                    VisitAttributes(md.GetParameter(parameterHandle).GetCustomAttributes());
                }

                Flush(member, ExposureKind.CustomAttribute);
            }

            foreach (var propertyHandle in type.GetProperties())
            {
                var property = md.GetPropertyDefinition(propertyHandle);
                var accessors = property.GetAccessors();
                if (!IsAnyVisible(md, isSealed, accessors.Others, accessors.Getter, accessors.Setter))
                {
                    continue;
                }

                var member = $"{typeName}.{md.GetString(property.Name)}";
                property.DecodeSignature(collector, null);
                Flush(member, ExposureKind.Property);
                VisitAttributes(property.GetCustomAttributes());
                Flush(member, ExposureKind.CustomAttribute);
            }

            foreach (var eventHandle in type.GetEvents())
            {
                var @event = md.GetEventDefinition(eventHandle);
                var accessors = @event.GetAccessors();
                if (!IsAnyVisible(md, isSealed, accessors.Others, accessors.Adder, accessors.Remover, accessors.Raiser))
                {
                    continue;
                }

                var member = $"{typeName}.{md.GetString(@event.Name)}";
                collector.Visit(@event.Type);
                Flush(member, ExposureKind.Event);
                VisitAttributes(@event.GetCustomAttributes());
                Flush(member, ExposureKind.CustomAttribute);
            }
        }

        foreach (var exportedHandle in md.ExportedTypes)
        {
            var exported = md.GetExportedType(exportedHandle);

            // Nested forwarders point at their declaring exported type; the outermost one names the assembly.
            // Exported types of other modules of this assembly end at a File handle and are ignored.
            var implementation = exported.Implementation;
            for (var depth = 0; depth < 64 && implementation.Kind == HandleKind.ExportedType; depth++)
            {
                implementation = md.GetExportedType((ExportedTypeHandle)implementation).Implementation;
            }

            if (implementation.Kind == HandleKind.AssemblyReference)
            {
                var reference = md.GetAssemblyReference((AssemblyReferenceHandle)implementation);
                var member = ExportedTypeName(md, exported);
                report(md.GetString(reference.Name), reference.Version, new ExposureSite(assemblyFile, member, ExposureKind.TypeForwarder));
            }
        }
    }

    /// <summary>Whether any accessor (named ones, then <paramref name="others"/>) is visible.</summary>
    private static bool IsAnyVisible(
        MetadataReader md, bool declaringTypeSealed, ImmutableArray<MethodDefinitionHandle> others, params ReadOnlySpan<MethodDefinitionHandle> accessors)
    {
        foreach (var accessor in accessors)
        {
            if (!accessor.IsNil && Visibility.IsVisible(md.GetMethodDefinition(accessor).Attributes, declaringTypeSealed))
            {
                return true;
            }
        }

        return others.Any(a => Visibility.IsVisible(md.GetMethodDefinition(a).Attributes, declaringTypeSealed));
    }

    private static string TypeName(MetadataReader md, TypeDefinition type)
    {
        var name = md.GetString(type.Name);
        return type.IsNested
            ? $"{TypeName(md, md.GetTypeDefinition(type.GetDeclaringType()))}+{name}"
            : JoinName(md.GetString(type.Namespace), name);
    }

    private static string ExportedTypeName(MetadataReader md, ExportedType type)
    {
        var name = md.GetString(type.Name);
        return type.Implementation.Kind == HandleKind.ExportedType
            ? $"{ExportedTypeName(md, md.GetExportedType((ExportedTypeHandle)type.Implementation))}+{name}"
            : JoinName(md.GetString(type.Namespace), name);
    }

    private static string JoinName(string ns, string name) => ns.Length == 0 ? name : $"{ns}.{name}";
}
