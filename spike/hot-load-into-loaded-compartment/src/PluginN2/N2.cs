using System.Runtime.InteropServices;
namespace PluginN2;
public static class N2
{
    [DllImport("nat")] private static extern int nat_version();
    public static int Version() => nat_version();
}
