using System.Reflection;
using System.Runtime.CompilerServices;
using Alcove.Autofac;
using Alcove.DependencyInjection;

namespace Alcove.Tests.PublicApi;

/// <summary>
/// Snapshot of every shipping assembly's public API (checked in as <c>PublicApi.&lt;Assembly&gt;.txt</c> next to
/// this file). A difference fails the test, so public API changes are always deliberate and visible in review.
/// To accept a change, run the tests with <c>ALCOVE_UPDATE_PUBLIC_API=1</c> and commit the rewritten files.
/// </summary>
public sealed class PublicApiTests
{
    private static readonly Assembly[] Shipping =
    [
        typeof(IPlugin).Assembly,
        typeof(PluginHost).Assembly,
        typeof(PluginServiceCollectionExtensions).Assembly,
        typeof(PluginContainerBuilderExtensions).Assembly,
    ];

    public static TheoryData<string> Assemblies => new(Shipping.Select(a => a.GetName().Name!));

    [Theory]
    [MemberData(nameof(Assemblies))]
    public void PublicApi_MatchesSnapshot(string assemblyName)
    {
        var assembly = Shipping.Single(a => a.GetName().Name == assemblyName);
        var actual = PublicApiWriter.Write(assembly);
        var path = Path.Combine(RepoRoot, "tests", "Alcove.Tests", "PublicApi", $"PublicApi.{assemblyName}.txt");

        if (Environment.GetEnvironmentVariable("ALCOVE_UPDATE_PUBLIC_API") == "1")
        {
            File.WriteAllText(path, actual);
            return;
        }

        var expected = File.Exists(path) ? File.ReadAllText(path).ReplaceLineEndings("\n") : "";
        Assert.True(expected == actual,
            $"The public API of {assemblyName} changed. If that's intended, run the tests with " +
            $"ALCOVE_UPDATE_PUBLIC_API=1 and commit {path}.\n{Diff(expected, actual)}");
    }

    /// <summary>
    /// CONTRIBUTING.md: async methods end in <c>Async</c> and take a <see cref="CancellationToken"/> as the last
    /// parameter, with a default value unless it's a shorter overload of a method that has one.
    /// </summary>
    [Fact]
    public void AsyncMethods_FollowTheConventions()
    {
        var problems = new List<string>();
        foreach (var type in Shipping.SelectMany(a => a.GetExportedTypes()))
        {
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(m => !m.IsSpecialName && IsAwaitable(m.ReturnType) && m.Name != nameof(IAsyncDisposable.DisposeAsync))
                .ToList();
            foreach (var method in methods)
            {
                var name = $"{type.Name}.{method.Name}({string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name))})";
                if (!method.Name.EndsWith("Async", StringComparison.Ordinal))
                    problems.Add($"{name}: name doesn't end in Async");

                var parameters = method.GetParameters();
                var tokens = parameters.Where(p => p.ParameterType == typeof(CancellationToken)).ToList();
                if (tokens.Count != 1 || parameters[^1].ParameterType != typeof(CancellationToken))
                {
                    problems.Add($"{name}: no CancellationToken as the last parameter");
                    continue;
                }

                var hasLongerOverload = methods.Any(o => o != method && o.Name == method.Name
                    && o.GetParameters().Length > parameters.Length && o.GetParameters()[^1].HasDefaultValue);
                if (!parameters[^1].HasDefaultValue && !hasLongerOverload)
                    problems.Add($"{name}: the CancellationToken has no default value");
            }
        }

        Assert.Empty(problems);
    }

    private static bool IsAwaitable(Type type) =>
        type == typeof(Task) || type == typeof(ValueTask)
        || type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(Task<>) || type.GetGenericTypeDefinition() == typeof(ValueTask<>));

    private static string Diff(string expected, string actual)
    {
        var before = expected.Split('\n').ToHashSet(StringComparer.Ordinal);
        var after = actual.Split('\n').ToHashSet(StringComparer.Ordinal);
        return string.Join('\n',
            before.Except(after).Select(l => "- " + l).Concat(after.Except(before).Select(l => "+ " + l)));
    }

    private static string RepoRoot =>
        typeof(PublicApiTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "AlcoveRepoRoot").Value!;
}

/// <summary>Writes a stable, sorted, C#-like listing of an assembly's public and protected surface.</summary>
internal static class PublicApiWriter
{
    private const BindingFlags Declared =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static readonly NullabilityInfoContext Nullability = new();

    public static string Write(Assembly assembly)
    {
        var lines = new List<string>();
        foreach (var type in assembly.GetExportedTypes().OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            lines.Add(TypeDeclaration(type));
            lines.AddRange(Members(type)
                .OrderBy(m => m.Name, StringComparer.Ordinal)
                .ThenBy(m => m.Text, StringComparer.Ordinal)
                .Select(m => "  " + m.Text));
        }

        return string.Join('\n', lines) + "\n";
    }

    private static string TypeDeclaration(Type type)
    {
        string kind;
        var modifiers = "";
        if (type.IsEnum) kind = "enum";
        else if (type.IsInterface) kind = "interface";
        else if (type.IsValueType) kind = (type.IsDefined(typeof(IsReadOnlyAttribute)) ? "readonly " : "") + "struct";
        else if (type.IsSubclassOf(typeof(Delegate))) kind = "delegate";
        else
        {
            kind = type.GetMethod("<Clone>$") is not null ? "record" : "class";
            modifiers = type is { IsAbstract: true, IsSealed: true } ? "static " : type.IsAbstract ? "abstract " : type.IsSealed ? "sealed " : "";
        }

        var bases = new List<string>();
        if (type.BaseType is { } baseType && baseType != typeof(object) && baseType != typeof(ValueType)
            && baseType != typeof(Enum) && !type.IsSubclassOf(typeof(Delegate)))
            bases.Add(TypeName(baseType));
        if (!type.IsEnum)
            bases.AddRange(type.GetInterfaces()
            .Where(i => i.IsPublic || i.IsNestedPublic)
            .Where(i => !IsCompilerInterface(type, i))
            .Select(TypeName)
            .Order(StringComparer.Ordinal));

        var constraints = GenericConstraints(type.IsGenericTypeDefinition ? type.GetGenericArguments() : []);
        return $"{modifiers}{kind} {TypeName(type, includeNamespace: true)}" + (bases.Count > 0 ? " : " + string.Join(", ", bases) : "") + constraints;
    }

    // Records implement IEquatable<Self>; that's implied by "record".
    private static bool IsCompilerInterface(Type type, Type @interface)
        => type.GetMethod("<Clone>$") is not null && @interface.IsGenericType
           && @interface.GetGenericTypeDefinition() == typeof(IEquatable<>) && @interface.GetGenericArguments()[0] == type;

    private static IEnumerable<(string Name, string Text)> Members(Type type)
    {
        if (type.IsEnum)
        {
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
                yield return (Convert.ToInt64(field.GetRawConstantValue()).ToString("D20", System.Globalization.CultureInfo.InvariantCulture), $"{field.Name} = {Convert.ToInt64(field.GetRawConstantValue())}");
            yield break;
        }

        if (type.IsSubclassOf(typeof(Delegate)))
        {
            var invoke = type.GetMethod("Invoke")!;
            yield return ("Invoke", $"{ReturnType(invoke)} Invoke({Parameters(invoke)})");
            yield break;
        }

        foreach (var member in type.GetMembers(Declared))
        {
            if (member.Name.Contains('<') || member.Name == "EqualityContract")
                continue;
            switch (member)
            {
                case ConstructorInfo ctor when IsVisible(ctor) && !IsRecordCopyConstructor(type, ctor):
                    yield return ("", $"{Access(ctor)}{(ctor.IsStatic ? "static " : "")}.ctor({Parameters(ctor)})");
                    break;
                case MethodInfo method when IsVisible(method) && !method.IsSpecialName:
                    yield return (member.Name, Method(type, method));
                    break;
                case MethodInfo method when IsVisible(method) && method.Name.StartsWith("op_", StringComparison.Ordinal):
                    yield return (member.Name, Method(type, method));
                    break;
                case PropertyInfo property when IsVisible(property.GetMethod) || IsVisible(property.SetMethod):
                    yield return (member.Name, Property(type, property));
                    break;
                case FieldInfo field when IsVisible(field):
                    yield return (member.Name, Field(field));
                    break;
                case EventInfo @event when IsVisible(@event.AddMethod):
                    yield return (member.Name, $"{Access(@event.AddMethod!)}{Modifiers(type, @event.AddMethod!)}event {NullableTypeName(Nullability.Create(@event))} {@event.Name}");
                    break;
                case Type nested when nested.IsNestedPublic || nested.IsNestedFamily || nested.IsNestedFamORAssem:
                    yield return (member.Name, $"nested {TypeName(nested)}");
                    break;
            }
        }
    }

    private static bool IsRecordCopyConstructor(Type type, ConstructorInfo ctor)
        => type.GetMethod("<Clone>$") is not null && ctor.GetParameters() is [var p] && p.ParameterType == type;

    private static bool IsVisible(MethodBase? method)
        => method is not null && (method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly);

    private static bool IsVisible(FieldInfo field) => field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly;

    private static string Access(MethodBase method) => method.IsPublic ? "" : "protected ";

    private static string Modifiers(Type type, MethodInfo method)
    {
        if (method.IsStatic) return "static ";
        if (type.IsInterface) return method.IsAbstract ? "" : "virtual ";   // default interface implementation
        if (method.IsAbstract) return "abstract ";
        if (!method.IsVirtual || method.IsFinal) return method.GetBaseDefinition() != method && !method.IsFinal ? "override " : "";
        return method.GetBaseDefinition().DeclaringType != method.DeclaringType ? "override " : "virtual ";
    }

    private static string Method(Type type, MethodInfo method)
    {
        var generic = method.IsGenericMethodDefinition
            ? "<" + string.Join(", ", method.GetGenericArguments().Select(a => a.Name)) + ">"
            : "";
        var constraints = method.IsGenericMethodDefinition ? GenericConstraints(method.GetGenericArguments()) : "";
        var extension = method.IsDefined(typeof(System.Runtime.CompilerServices.ExtensionAttribute)) ? "this " : "";
        return $"{Access(method)}{Modifiers(type, method)}{ReturnType(method)} {method.Name}{generic}({extension}{Parameters(method)}){constraints}";
    }

    private static string ReturnType(MethodInfo method) => NullableTypeName(Nullability.Create(method.ReturnParameter));

    private static string Property(Type type, PropertyInfo property)
    {
        var accessor = property.GetMethod ?? property.SetMethod!;
        var accessors = new List<string>();
        if (IsVisible(property.GetMethod)) accessors.Add(Access(property.GetMethod!) + "get;");
        if (IsVisible(property.SetMethod))
        {
            var isInit = property.SetMethod!.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(IsExternalInit));
            accessors.Add(Access(property.SetMethod!) + (isInit ? "init;" : "set;"));
        }

        var required = property.IsDefined(typeof(RequiredMemberAttribute)) ? "required " : "";
        var indexParameters = property.GetIndexParameters();
        var name = indexParameters.Length > 0 ? $"this[{string.Join(", ", indexParameters.Select(Parameter))}]" : property.Name;
        var access = IsVisible(property.GetMethod) && property.GetMethod!.IsPublic || IsVisible(property.SetMethod) && property.SetMethod!.IsPublic ? "" : "protected ";
        return $"{access}{required}{Modifiers(type, accessor)}{NullableTypeName(Nullability.Create(property))} {name} {{ {string.Join(" ", accessors)} }}";
    }

    private static string Field(FieldInfo field)
    {
        var prefix = field.IsLiteral ? "const " : (field.IsStatic ? "static " : "") + (field.IsInitOnly ? "readonly " : "");
        var value = field.IsLiteral ? " = " + Literal(field.GetRawConstantValue()) : "";
        return $"{(field.IsPublic ? "" : "protected ")}{prefix}{NullableTypeName(Nullability.Create(field))} {field.Name}{value}";
    }

    private static string Parameters(MethodBase method) => string.Join(", ", method.GetParameters().Select(Parameter));

    private static string Parameter(ParameterInfo parameter)
    {
        var type = parameter.ParameterType;
        var modifier = parameter.IsDefined(typeof(ParamArrayAttribute)) ? "params "
            : type.IsByRef ? (parameter.IsOut ? "out " : parameter.IsIn ? "in " : "ref ") : "";
        var text = $"{modifier}{NullableTypeName(Nullability.Create(parameter))} {parameter.Name}";
        if (!parameter.HasDefaultValue)
            return text;
        var elementType = type.IsByRef ? type.GetElementType()! : type;
        var value = parameter.RawDefaultValue;
        if (value is null && elementType.IsValueType && Nullable.GetUnderlyingType(elementType) is null)
            return text + " = default";
        if (value is not null && elementType.IsEnum)
            return text + " = " + elementType.Name + "." + Enum.ToObject(elementType, value);
        return text + " = " + Literal(value);
    }

    private static string Literal(object? value) => value switch
    {
        null => "null",
        string s => $"\"{s}\"",
        bool b => b ? "true" : "false",
        _ when value.GetType() == typeof(Missing) || value == DBNull.Value => "default",
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!,
    };

    private static string GenericConstraints(Type[] arguments)
    {
        var clauses = new List<string>();
        foreach (var argument in arguments)
        {
            var constraints = new List<string>();
            var attributes = argument.GenericParameterAttributes;
            if (attributes.HasFlag(GenericParameterAttributes.NotNullableValueTypeConstraint)) constraints.Add("struct");
            else if (attributes.HasFlag(GenericParameterAttributes.ReferenceTypeConstraint)) constraints.Add("class");
            else if (argument.IsDefined(typeof(IsUnmanagedAttribute))) constraints.Add("unmanaged");
            constraints.AddRange(argument.GetGenericParameterConstraints().Where(c => c != typeof(ValueType)).Select(TypeName));
            if (attributes.HasFlag(GenericParameterAttributes.DefaultConstructorConstraint)
                && !attributes.HasFlag(GenericParameterAttributes.NotNullableValueTypeConstraint))
                constraints.Add("new()");
            if (constraints.Count > 0)
                clauses.Add($" where {argument.Name} : {string.Join(", ", constraints)}");
        }

        return string.Concat(clauses);
    }

    private static string NullableTypeName(NullabilityInfo info)
    {
        var type = info.Type;
        if (type.IsByRef)
            type = type.GetElementType()!;
        string name;
        if (Nullable.GetUnderlyingType(type) is { } underlying)
            return TypeName(underlying) + "?";
        if (type.IsArray && info.ElementType is { } element)
            name = NullableTypeName(element) + "[]";
        else if (type.IsGenericType && !type.IsGenericTypeDefinition && info.GenericTypeArguments.Length == type.GetGenericArguments().Length)
            name = GenericName(type, info.GenericTypeArguments.Select(NullableTypeName));
        else
            name = TypeName(type);
        return info.ReadState == NullabilityState.Nullable && !type.IsValueType ? name + "?" : name;
    }

    private static string TypeName(Type type) => TypeName(type, includeNamespace: false);

    private static string TypeName(Type type, bool includeNamespace)
    {
        if (type.IsGenericParameter) return type.Name;
        if (type.IsByRef) return TypeName(type.GetElementType()!);
        if (type.IsArray) return TypeName(type.GetElementType()!) + "[]";
        if (Nullable.GetUnderlyingType(type) is { } underlying) return TypeName(underlying) + "?";
        if (Keywords.TryGetValue(type, out var keyword)) return keyword;
        if (type.IsGenericType) return GenericName(type, type.GetGenericArguments().Select(TypeName), includeNamespace);

        var name = type.IsNested ? TypeName(type.DeclaringType!, includeNamespace) + "." + type.Name : type.Name;
        return includeNamespace && !type.IsNested ? type.Namespace + "." + name : name;
    }

    private static string GenericName(Type type, IEnumerable<string> arguments, bool includeNamespace = false)
    {
        var definition = type.IsGenericTypeDefinition ? type : type.GetGenericTypeDefinition();
        var name = definition.Name[..definition.Name.IndexOf('`')];
        if (type.IsNested) name = TypeName(type.DeclaringType!, includeNamespace) + "." + name;
        else if (includeNamespace) name = definition.Namespace + "." + name;
        return $"{name}<{string.Join(", ", arguments)}>";
    }

    private static readonly Dictionary<Type, string> Keywords = new()
    {
        [typeof(void)] = "void", [typeof(object)] = "object", [typeof(string)] = "string", [typeof(bool)] = "bool",
        [typeof(byte)] = "byte", [typeof(sbyte)] = "sbyte", [typeof(short)] = "short", [typeof(ushort)] = "ushort",
        [typeof(int)] = "int", [typeof(uint)] = "uint", [typeof(long)] = "long", [typeof(ulong)] = "ulong",
        [typeof(float)] = "float", [typeof(double)] = "double", [typeof(decimal)] = "decimal", [typeof(char)] = "char",
    };
}
