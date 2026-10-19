namespace AssetsLib;
public static class Which
{
#if WINDOWS10_0_19041_0_OR_GREATER
    public const string Tfm = "net9.0-windows10.0.19041.0";
#elif WINDOWS
    public const string Tfm = "net8.0-windows";
#else
    public const string Tfm = "net8.0";
#endif
}
