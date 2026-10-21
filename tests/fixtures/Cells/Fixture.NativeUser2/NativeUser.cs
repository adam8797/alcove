using System.Runtime.InteropServices;

namespace FixtureNativeUser2;

/// <summary>Calls the native test library 'alcovenat' (built by the test with gcc, or MSVC on Windows).</summary>
public static class NativeUser
{
    [DllImport("alcovenat")]
    private static extern int alcovenat_version();

    public static int Version() => alcovenat_version();
}
