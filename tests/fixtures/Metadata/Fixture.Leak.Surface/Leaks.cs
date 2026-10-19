using System.Runtime.CompilerServices;
using Fixture.Leak.Dependency;
using Fixture.Leak.ForwardTarget;
using Fixture.Leak.InternalDependency;

[assembly: TypeForwardedTo(typeof(ForwardedType))]

namespace Fixture.Leak.Surface;

// One public type per leak kind. Each exposes a type from Fixture.Leak.Dependency in exactly one way.

public sealed class ReturnTypeLeak
{
    public ReturnedType Get() => new();
}

public sealed class ParameterLeak
{
    public void Take(ParameterType value) => GC.KeepAlive(value);
}

public sealed class GenericArgumentLeak
{
    public List<GenericArgumentType> Items() => [];
}

public class BaseClassLeak : BaseClass;

public sealed class InterfaceLeak : IExposedInterface;

public sealed class ConstraintLeak<T>
    where T : IConstraint;

public sealed class MethodConstraintLeak
{
    public void Use<T>()
        where T : IMethodConstraint
    {
    }
}

public sealed class PropertyLeak
{
    public PropertyType? Value { get; set; }
}

public sealed class FieldLeak
{
    public FieldType? Value;
}

public sealed class EventLeak
{
    public event DependencyHandler? Changed;

    public void Raise() => Changed?.Invoke(this);
}

public sealed class Outer
{
    public sealed class NestedLeak
    {
        public NestedMemberType? Value { get; set; }
    }
}

public class ProtectedLeak
{
    protected ProtectedMemberType Get() => new();
}

[Api]
public sealed class AttributeLeak;

[AttributeUsage(AttributeTargets.All)]
public sealed class MarkerAttribute(Type type) : Attribute
{
    public Type Type { get; } = type;
}

[Marker(typeof(TypeOfArgument))]
public sealed class TypeOfAttributeLeak;

// Uses of Fixture.Leak.InternalDependency that are NOT part of the public surface.

public class InternalUses
{
    private readonly Hidden _field = Hidden.Create();

    private Hidden PrivateMethod() => _field;

    internal Hidden InternalMethod() => PrivateMethod();

    private protected Hidden PrivateProtectedMethod() => InternalMethod();

    [Hidden]
    private void AttributedPrivate() => GC.KeepAlive(PrivateProtectedMethod());

    public string BodyOnly()
    {
        AttributedPrivate();
        var local = Hidden.Create();
        return local.ToString();
    }

    private sealed class PrivateNested : Hidden, IHidden
    {
        public Hidden Self() => this;
    }

    public object NotRevealed() => new PrivateNested().Self();
}

// Protected members of a sealed type can't be reached from outside the assembly.
public sealed class SealedWithProtected
{
#pragma warning disable CS0628 // new protected member declared in sealed type: that's the point
    protected Hidden Make() => Hidden.Create();

    protected Hidden? Field = null;

    protected sealed class ProtectedNested
    {
        public Hidden? Value { get; set; }
    }
#pragma warning restore CS0628

    public override string ToString() => $"{Make()}{Field}{new ProtectedNested().Value}";
}

internal sealed class InternalType : Hidden, IHidden
{
    public Hidden Value { get; } = Create();

    public sealed class PublicNestedOfInternal
    {
        public Hidden? Value { get; set; }
    }
}
