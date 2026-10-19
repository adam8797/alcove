using Alcove.Internal.Assets;

namespace Alcove.Tests.Assets;

public sealed class NativeLibraryNamesTests
{
    [Fact]
    public void Candidates_FollowTheRuntimesProbingConventions()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(["nat.dll", "nat"], NativeLibraryNames.Candidates("nat"));
            Assert.Equal(["nat.dll"], NativeLibraryNames.Candidates("nat.dll"));
        }
        else if (OperatingSystem.IsMacOS())
        {
            Assert.Equal(["libnat.dylib", "nat.dylib", "libnat", "nat"], NativeLibraryNames.Candidates("nat"));
        }
        else
        {
            Assert.Equal(["libnat.so", "nat.so", "libnat", "nat"], NativeLibraryNames.Candidates("nat"));
            Assert.Equal(["libnat.so"], NativeLibraryNames.Candidates("libnat.so"));
            Assert.Equal(["nat.so", "libnat.so"], NativeLibraryNames.Candidates("nat.so"));
            Assert.Equal(["libnat.so.1"], NativeLibraryNames.Candidates("libnat.so.1"));
            Assert.Equal(["libnat.so", "libnat"], NativeLibraryNames.Candidates("libnat"));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("/usr/lib/libnat.so")]
    [InlineData("sub/nat")]
    [InlineData("C:\\native\\nat.dll")]
    public void Candidates_ForPathsOrEmptyNames_AreEmpty(string name) => Assert.Empty(NativeLibraryNames.Candidates(name));
}
