using System.Collections.Immutable;
using System.Reflection.Metadata;

namespace Alcove.Internal.Metadata;

/// <summary>A decoded type, as far as the collector needs it: its handle, or that it is <c>System.Type</c>.</summary>
internal readonly record struct CollectedType(EntityHandle Handle, bool IsSystemType = false);

/// <summary>
/// Decodes signatures and custom attributes and records the simple name of every <em>other</em> assembly a
/// type comes from. Type definitions of the scanned assembly are never recorded.
/// </summary>
internal sealed class ReferencedAssemblyCollector(MetadataReader md)
    : ISignatureTypeProvider<CollectedType, object?>, ICustomAttributeTypeProvider<CollectedType>
{
    private const int MaxScopeDepth = 64;

    /// <summary>Assembly names recorded since the last <see cref="Clear"/>, with the version named (if any).</summary>
    public Dictionary<string, Version?> Assemblies { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void Clear() => Assemblies.Clear();

    /// <summary>Records the assembly of a TypeDef/TypeRef/TypeSpec handle (base types, interfaces, constraints, events).</summary>
    public void Visit(EntityHandle handle)
    {
        switch (handle.Kind)
        {
            case HandleKind.TypeReference:
                RecordScope((TypeReferenceHandle)handle);
                break;
            case HandleKind.TypeSpecification:
                md.GetTypeSpecification((TypeSpecificationHandle)handle).DecodeSignature(this, null);
                break;
        }
    }

    /// <summary>
    /// Records the attribute's type, its constructor's parameter types and, as far as they can be decoded,
    /// the types named by its arguments (<c>typeof(...)</c> and enum values).
    /// </summary>
    public void VisitAttribute(CustomAttributeHandle handle)
    {
        var attribute = md.GetCustomAttribute(handle);
        switch (attribute.Constructor.Kind)
        {
            case HandleKind.MemberReference:
                var reference = md.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
                Visit(reference.Parent);
                reference.DecodeMethodSignature(this, null);
                break;
            case HandleKind.MethodDefinition:
                md.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).DecodeSignature(this, null);
                break;
        }

        try
        {
            attribute.DecodeValue(this);
        }
        catch (Exception ex) when (ex is BadImageFormatException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            // An argument of an enum type from another assembly whose underlying type isn't int, or a
            // malformed blob: the attribute type itself has been recorded above, which is what matters most.
        }
    }

    private void RecordScope(TypeReferenceHandle handle)
    {
        var current = handle;
        for (var depth = 0; depth < MaxScopeDepth; depth++)
        {
            var scope = md.GetTypeReference(current).ResolutionScope;
            switch (scope.Kind)
            {
                case HandleKind.AssemblyReference:
                    var reference = md.GetAssemblyReference((AssemblyReferenceHandle)scope);
                    Record(md.GetString(reference.Name), reference.Version);
                    return;
                case HandleKind.TypeReference:
                    current = (TypeReferenceHandle)scope; // nested type: the outermost type's scope decides
                    continue;
                default:
                    return; // this module, a module reference, or the exported-type table: not another assembly
            }
        }
    }

    private void Record(string name, Version? version)
    {
        if (!Assemblies.TryGetValue(name, out var existing) || (version is not null && (existing is null || version > existing)))
        {
            Assemblies[name] = version;
        }
    }

    // ISignatureTypeProvider

    public CollectedType GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
    {
        RecordScope(handle);
        return new CollectedType(handle, IsSystemType(handle));
    }

    public CollectedType GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => new(handle);

    public CollectedType GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
        reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

    public CollectedType GetPrimitiveType(PrimitiveTypeCode typeCode) => default;

    public CollectedType GetSZArrayType(CollectedType elementType) => default;

    public CollectedType GetArrayType(CollectedType elementType, ArrayShape shape) => default;

    public CollectedType GetByReferenceType(CollectedType elementType) => default;

    public CollectedType GetPointerType(CollectedType elementType) => default;

    public CollectedType GetPinnedType(CollectedType elementType) => default;

    public CollectedType GetGenericInstantiation(CollectedType genericType, ImmutableArray<CollectedType> typeArguments) => default;

    public CollectedType GetGenericTypeParameter(object? genericContext, int index) => default;

    public CollectedType GetGenericMethodParameter(object? genericContext, int index) => default;

    public CollectedType GetFunctionPointerType(MethodSignature<CollectedType> signature) => default;

    public CollectedType GetModifiedType(CollectedType modifier, CollectedType unmodifiedType, bool isRequired) => unmodifiedType;

    // ICustomAttributeTypeProvider

    public CollectedType GetSystemType() => new(default, IsSystemType: true);

    public bool IsSystemType(CollectedType type) => type.IsSystemType;

    public CollectedType GetTypeFromSerializedName(string name)
    {
        // "Ns.Type, Assembly, Version=..., ...": a typeof(...) argument or a boxed enum's type. No assembly
        // part means the scanned assembly or CoreLib.
        if (TypeName.TryParse(name.AsSpan(), out var typeName) && typeName.AssemblyName is { } assembly && !string.IsNullOrEmpty(assembly.Name))
        {
            Record(assembly.Name, assembly.Version);
        }

        return default;
    }

    public PrimitiveTypeCode GetUnderlyingEnumType(CollectedType type)
    {
        if (type.Handle.Kind == HandleKind.TypeDefinition)
        {
            var definition = md.GetTypeDefinition((TypeDefinitionHandle)type.Handle);
            foreach (var fieldHandle in definition.GetFields())
            {
                var field = md.GetFieldDefinition(fieldHandle);
                if ((field.Attributes & System.Reflection.FieldAttributes.Static) == 0)
                {
                    // Field signature: 0x06, then the element type, whose codes PrimitiveTypeCode shares.
                    var blob = md.GetBlobReader(field.Signature);
                    blob.ReadByte();
                    return (PrimitiveTypeCode)blob.ReadByte();
                }
            }
        }

        // An enum from another assembly can't be resolved without loading it. Almost all are int-based; if
        // not, decoding fails and is ignored by VisitAttribute.
        return PrimitiveTypeCode.Int32;
    }

    private bool IsSystemType(TypeReferenceHandle handle)
    {
        var reference = md.GetTypeReference(handle);
        return reference.ResolutionScope.Kind == HandleKind.AssemblyReference
            && md.StringComparer.Equals(reference.Name, "Type")
            && md.StringComparer.Equals(reference.Namespace, "System");
    }
}

/// <summary>Visibility rules for the public surface (what code outside the assembly can see).</summary>
internal static class Visibility
{
    public static bool IsTypeVisible(MetadataReader md, TypeDefinition type)
    {
        var current = type;
        for (var depth = 0; depth < 64; depth++)
        {
            switch (current.Attributes & System.Reflection.TypeAttributes.VisibilityMask)
            {
                case System.Reflection.TypeAttributes.Public:
                    return true;
                case System.Reflection.TypeAttributes.NestedPublic:
                    current = md.GetTypeDefinition(current.GetDeclaringType());
                    continue;
                case System.Reflection.TypeAttributes.NestedFamily:
                case System.Reflection.TypeAttributes.NestedFamORAssem:
                    var declaring = md.GetTypeDefinition(current.GetDeclaringType());
                    if (IsSealed(declaring))
                    {
                        return false;
                    }

                    current = declaring;
                    continue;
                default:
                    return false;
            }
        }

        return false;
    }

    public static bool IsSealed(TypeDefinition type) => (type.Attributes & System.Reflection.TypeAttributes.Sealed) != 0;

    public static bool IsVisible(System.Reflection.MethodAttributes attributes, bool declaringTypeSealed) =>
        (attributes & System.Reflection.MethodAttributes.MemberAccessMask) switch
        {
            System.Reflection.MethodAttributes.Public => true,
            System.Reflection.MethodAttributes.Family or System.Reflection.MethodAttributes.FamORAssem => !declaringTypeSealed,
            _ => false,
        };

    public static bool IsVisible(System.Reflection.FieldAttributes attributes, bool declaringTypeSealed) =>
        (attributes & System.Reflection.FieldAttributes.FieldAccessMask) switch
        {
            System.Reflection.FieldAttributes.Public => true,
            System.Reflection.FieldAttributes.Family or System.Reflection.FieldAttributes.FamORAssem => !declaringTypeSealed,
            _ => false,
        };
}
