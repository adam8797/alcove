using System.Runtime.InteropServices;

namespace FixtureNativeUser1;

/// <summary>Calls the native test library 'alcovenat' (built by the test with gcc).</summary>
public static class NativeUser
{
    [DllImport("alcovenat")]
    private static extern int alcovenat_version();

    public static int Version() => alcovenat_version();
}
