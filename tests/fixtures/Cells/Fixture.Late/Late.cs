namespace FixtureLate;

/// <summary>A dependency that only becomes resolvable later (e.g. installed afterwards).</summary>
public static class Late
{
    public static string Hello() => "Late says hello";
}
