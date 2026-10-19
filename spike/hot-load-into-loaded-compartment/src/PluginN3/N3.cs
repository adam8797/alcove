using System.Runtime.InteropServices;
namespace PluginN3;
public static class N3
{
    [DllImport("natuser")] private static extern int natuser_sees_nat_version();
    public static int Version() => natuser_sees_nat_version();
}
