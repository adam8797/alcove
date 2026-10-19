namespace Fixture.Leak.InternalDependency;

public class Hidden
{
    public static Hidden Create() => new();

    public override string ToString() => "hidden";
}

public interface IHidden;

[AttributeUsage(AttributeTargets.All)]
public sealed class HiddenAttribute : Attribute;
