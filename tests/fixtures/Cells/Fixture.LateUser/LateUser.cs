using System.Runtime.CompilerServices;
using FixtureLate;

namespace FixtureLateUser;

public static class LateUser
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string UseLate() => Late.Hello();

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string UseLateOther() => "other: " + Late.Hello();
}
