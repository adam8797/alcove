using System.Runtime.InteropServices;
namespace PluginN1;
public static class N1
{
    [DllImport("nat")] private static extern int nat_version();
    public static int Version() => nat_version();
}
