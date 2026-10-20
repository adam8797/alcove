using Alcove.Storage;
using Alcove.Tests.TestSupport;

namespace Alcove.Tests.Storage;

public sealed class AtomicFileTests
{
    [Fact]
    public void WriteAllBytes_CreatesTheDirectoryAndTheFile()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("a", "b", "file.json");

        AtomicFile.WriteAllBytes(path, "first"u8);

        Assert.Equal("first", File.ReadAllText(path));
    }

    [Fact]
    public void WriteAllBytes_ReplacesExistingContentAndLeavesNoTemporaryFile()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("file.json");
        File.WriteAllText(path, "a much longer old content");

        AtomicFile.WriteAllBytes(path, "new"u8);

        Assert.Equal("new", File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFiles(temp.Path));
    }

    [Fact]
    public void WriteAllBytes_WhenTheRenameFails_KeepsTheOldFileAndCleansUp()
    {
        using var temp = new TempDirectory();
        var target = temp.Combine("target");
        Directory.CreateDirectory(target);   // a directory can't be replaced by a file

        // Linux and macOS report an IOException; Windows reports access denied (after the rename retries).
        var exception = Assert.ThrowsAny<Exception>(() => AtomicFile.WriteAllBytes(target, "content"u8));
        Assert.True(exception is IOException or UnauthorizedAccessException, exception.ToString());

        Assert.True(Directory.Exists(target));
        Assert.Empty(Directory.GetFiles(temp.Path));
    }

    [Fact]
    public async Task AWriter_ReplacesAFileBeingRead_OnceTheReaderCloses()
    {
        // Review finding 5. Linux and macOS let the rename replace a file being read right away. Windows refuses it
        // while the reader has the file open, so the writer retries until the reader closes (well within its ~1 s).
        Assert.Equal(FileShare.ReadWrite | FileShare.Delete, AtomicFile.ReadShare);
        using var temp = new TempDirectory();
        var path = temp.Combine("state.json");
        AtomicFile.WriteAllBytes(path, "old"u8);

        Task writer;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, AtomicFile.ReadShare))
        {
            writer = Task.Run(() => AtomicFile.WriteAllBytes(path, "new"u8), TestContext.Current.CancellationToken);
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        await writer;
        Assert.Equal("new"u8.ToArray(), AtomicFile.ReadAllBytes(path));
    }

    [Fact]
    public void MoveWithRetry_RetriesSharingViolations_ThenGivesUp()
    {
        var sharingViolation = new IOException("in use", unchecked((int)0x80070020));
        var calls = 0;
        var pauses = new List<TimeSpan>();

        AtomicFile.MoveWithRetry("a", "b", (_, _) => { if (++calls < 3) { throw sharingViolation; } }, pauses.Add);

        Assert.Equal(3, calls);
        Assert.Equal([TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(20)], pauses);

        calls = 0;
        Assert.Same(sharingViolation, Assert.Throws<IOException>(() => AtomicFile.MoveWithRetry("a", "b", (_, _) => { calls++; throw sharingViolation; }, _ => { })));
        Assert.Equal(AtomicFile.MoveAttempts, calls);

        calls = 0;
        Assert.Throws<IOException>(() => AtomicFile.MoveWithRetry("a", "b", (_, _) => { calls++; throw new IOException("disk full"); }, _ => { }));
        Assert.Equal(1, calls);
    }
}
