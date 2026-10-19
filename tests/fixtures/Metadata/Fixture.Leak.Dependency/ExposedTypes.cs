namespace Fixture.Leak.Dependency;

public sealed class ReturnedType;

public sealed class ParameterType;

public sealed class GenericArgumentType;

public class BaseClass;

public interface IExposedInterface;

public interface IConstraint;

public interface IMethodConstraint;

public sealed class PropertyType;

public sealed class FieldType;

public delegate void DependencyHandler(object sender);

public sealed class NestedMemberType;

public sealed class ProtectedMemberType;

public sealed class TypeOfArgument;

[AttributeUsage(AttributeTargets.All)]
public sealed class ApiAttribute : Attribute;
