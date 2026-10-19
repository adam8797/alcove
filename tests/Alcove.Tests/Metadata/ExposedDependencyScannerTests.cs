using System.Diagnostics;
using Alcove.Internal.Metadata;

namespace Alcove.Tests.Metadata;

/// <summary>
/// The public-surface scanner against Fixture.Leak.Surface, which exposes Fixture.Leak.Dependency in one way per
/// public type, forwards a type to Fixture.Leak.ForwardTarget, and uses Fixture.Leak.InternalDependency only internally.
/// </summary>
public sealed class ExposedDependencyScannerTests
{
    private const string Dependency = "Fixture.Leak.Dependency";
    private const string Surface = "Fixture.Leak.Surface.";

    private static readonly Lazy<ExposedDependencyScanResult> Result =
        new(() => ExposedDependencyScanner.Scan([MetadataFixtures.LeakSurface], MetadataFixtures.IsFramework));

    [Fact]
    public void Scan_ReportsExactlyTheExposedDependencies()
    {
        Assert.Equal(["Fixture.Leak.Dependency", "Fixture.Leak.ForwardTarget"], Result.Value.Dependencies.Select(d => d.AssemblyName));
        Assert.Empty(Result.Value.Skipped);
    }

    [Fact]
    public void Scan_InternalOnlyUse_DoesNotCount()
    {
        Assert.DoesNotContain("Fixture.Leak.InternalDependency", Result.Value.AssemblyNames);
    }

    [Fact]
    public void Scan_RecordsTheReferencedVersion()
    {
        Assert.Equal(new Version(1, 0, 0, 0), Result.Value.Dependencies.Single(d => d.AssemblyName == Dependency).ReferencedVersion);
    }

    public static TheoryData<string, string> Leaks => new()
    {
        { "ReturnTypeLeak.Get", nameof(ExposureKind.Method) },
        { "ParameterLeak.Take", nameof(ExposureKind.Method) },
        { "GenericArgumentLeak.Items", nameof(ExposureKind.Method) },
        { "BaseClassLeak", nameof(ExposureKind.BaseType) },
        { "InterfaceLeak", nameof(ExposureKind.Interface) },
        { "ConstraintLeak`1", nameof(ExposureKind.GenericConstraint) },
        { "MethodConstraintLeak.Use", nameof(ExposureKind.GenericConstraint) },
        { "PropertyLeak.Value", nameof(ExposureKind.Property) },
        { "PropertyLeak.get_Value", nameof(ExposureKind.Method) },
        { "FieldLeak.Value", nameof(ExposureKind.Field) },
        { "EventLeak.Changed", nameof(ExposureKind.Event) },
        { "EventLeak.add_Changed", nameof(ExposureKind.Method) },
        { "Outer+NestedLeak.Value", nameof(ExposureKind.Property) },
        { "ProtectedLeak.Get", nameof(ExposureKind.Method) },
        { "AttributeLeak", nameof(ExposureKind.CustomAttribute) },
        { "TypeOfAttributeLeak", nameof(ExposureKind.CustomAttribute) },
    };

    [Theory]
    [MemberData(nameof(Leaks))]
    public void Scan_FindsEachLeakKind(string member, string kind)
    {
        var sites = Result.Value.Dependencies.Single(d => d.AssemblyName == Dependency).Sites;

        Assert.Contains(sites, s => s.Member == Surface + member && s.Kind == Enum.Parse<ExposureKind>(kind) && s.Assembly == "Fixture.Leak.Surface.dll");
    }

    [Fact]
    public void Scan_TypeForwarders_AreExposed()
    {
        var forward = Result.Value.Dependencies.Single(d => d.AssemblyName == "Fixture.Leak.ForwardTarget");

        Assert.All(forward.Sites, s => Assert.Equal(ExposureKind.TypeForwarder, s.Kind));
        Assert.Contains(forward.Sites, s => s.Member == "Fixture.Leak.ForwardTarget.ForwardedType");
        Assert.Contains(forward.Sites, s => s.Member == "Fixture.Leak.ForwardTarget.ForwardedType+NestedForwardedType");
    }

    [Fact]
    public void Scan_NonVisibleTypesAndMembers_HaveNoSites()
    {
        var members = Result.Value.Dependencies.SelectMany(d => d.Sites).Select(s => s.Member).ToList();

        Assert.DoesNotContain(members, m => m.StartsWith(Surface + "InternalUses", StringComparison.Ordinal));
        Assert.DoesNotContain(members, m => m.StartsWith(Surface + "SealedWithProtected", StringComparison.Ordinal));
        Assert.DoesNotContain(members, m => m.StartsWith(Surface + "InternalType", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_PluginsOwnAssemblies_AreNotExposed()
    {
        // Scanning the dependency together with the plugin: it's then one of the plugin's own assemblies.
        var result = ExposedDependencyScanner.Scan(
            [MetadataFixtures.LeakSurface, MetadataFixtures.Path("Fixture.Leak.Surface", Dependency)],
            MetadataFixtures.IsFramework);

        Assert.Equal(["Fixture.Leak.ForwardTarget"], result.Dependencies.Select(d => d.AssemblyName));
    }

    [Fact]
    public void Scan_HostOwnedPredicate_ExcludesNames()
    {
        var result = ExposedDependencyScanner.Scan(
            [MetadataFixtures.LeakSurface],
            name => MetadataFixtures.IsFramework(name) || name == Dependency);

        Assert.Equal(["Fixture.Leak.ForwardTarget"], result.Dependencies.Select(d => d.AssemblyName));
    }

    [Fact]
    public void Scan_WithoutAPredicateThatExcludesTheFramework_ReportsIt()
    {
        var result = ExposedDependencyScanner.Scan([MetadataFixtures.LeakSurface], _ => false);

        Assert.Contains("System.Runtime", result.AssemblyNames);
    }

    [Fact]
    public void Scan_NonManagedFile_IsSkippedWithAReason()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"alcove-native-{Guid.NewGuid():N}.dll");
        File.WriteAllBytes(path, [0x7F, (byte)'E', (byte)'L', (byte)'F', 0, 0, 0, 0]);
        try
        {
            var result = ExposedDependencyScanner.Scan([path, MetadataFixtures.LeakSurface], MetadataFixtures.IsFramework);

            Assert.Equal(path, Assert.Single(result.Skipped).Path);
            Assert.Equal(2, result.Dependencies.Length);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Scan_RealLibrary_FindsKnownExposure()
    {
        // NuGet.Versioning exposes nothing but the framework; Microsoft.Extensions.Logging.Abstractions exposes the DI abstractions.
        var logging = typeof(Microsoft.Extensions.Logging.ILogger).Assembly.Location;
        var versioning = typeof(NuGet.Versioning.NuGetVersion).Assembly.Location;

        var result = ExposedDependencyScanner.Scan([logging, versioning], n => n is "System.Runtime" or "netstandard" or "mscorlib" || n.StartsWith("System.", StringComparison.Ordinal));

        Assert.Contains("Microsoft.Extensions.DependencyInjection.Abstractions", result.AssemblyNames);
    }

    [Fact]
    public void Scan_LargeAssembly_IsFast()
    {
        var large = typeof(System.Text.Json.JsonSerializer).Assembly.Location;
        ExposedDependencyScanner.Scan([MetadataFixtures.LeakSurface], MetadataFixtures.IsFramework); // warm up

        var stopwatch = Stopwatch.StartNew();
        var result = ExposedDependencyScanner.Scan([large], _ => false);
        stopwatch.Stop();

        Assert.NotEmpty(result.Dependencies);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"Scanning {large} took {stopwatch.Elapsed.TotalMilliseconds:F0} ms.");
    }
}
