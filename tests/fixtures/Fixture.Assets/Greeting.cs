using System.Globalization;
using System.Resources;

namespace FixtureAssets;

/// <summary>Reads a localised string, so a test can check that satellite assemblies resolve.</summary>
public static class Greeting
{
    private static readonly ResourceManager Resources = new("Fixture.Assets.Strings", typeof(Greeting).Assembly);

    public static string Hello(string culture) => Resources.GetString("Hello", CultureInfo.GetCultureInfo(culture)) ?? "(missing)";
}
