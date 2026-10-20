using System.Diagnostics;
using Alcove.Storage;
using Alcove.Tests.TestSupport;

namespace Alcove.Tests.Storage;

public sealed class RootLockTests
{
    [Fact]
    public async Task ASecondHandleCantTakeTheLockWhileItIsHeld()
    {
        using var temp = new TempDirectory();
        var ct = TestContext.Current.CancellationToken;

        using (var first = await RootLock.AcquireAsync(temp.Path, cancellationToken: ct))
        {
            Assert.True(first.IsHeld);
            Assert.True(File.Exists(Path.Combine(temp.Path, RootLock.FileName)));
            Assert.Null(RootLock.TryAcquire(temp.Path));
            await Assert.ThrowsAsync<TimeoutException>(() => RootLock.AcquireAsync(temp.Path, TimeSpan.FromMilliseconds(200), ct));
        }

        using var again = RootLock.TryAcquire(temp.Path);
        Assert.NotNull(again);
    }

    [Fact]
    public async Task AcquireAsync_WaitsUntilTheHolderReleasesTheLock()
    {
        using var temp = new TempDirectory();
        var ct = TestContext.Current.CancellationToken;
        var first = await RootLock.AcquireAsync(temp.Path, cancellationToken: ct);

        var waiting = RootLock.AcquireAsync(temp.Path, TimeSpan.FromSeconds(10), ct);
        await Task.Delay(200, ct);
        Assert.False(waiting.IsCompleted);
        first.Dispose();
        using var second = await waiting;

        Assert.True(second.IsHeld);
        Assert.False(first.IsHeld);
    }

    [Fact]
    public async Task AcquireAsync_CanBeCancelled()
    {
        using var temp = new TempDirectory();
        using var held = await RootLock.AcquireAsync(temp.Path, cancellationToken: TestContext.Current.CancellationToken);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RootLock.AcquireAsync(temp.Path, TimeSpan.FromSeconds(30), cancel.Token));
    }

    [Fact]
    public async Task TheLockExcludesOtherProcesses()
    {
        // On Unix, .NET's FileShare.None is an advisory flock(2); the flock(1) tool takes the same kind of lock.
        if (OperatingSystem.IsWindows() || !File.Exists("/usr/bin/flock"))
        {
            Assert.Skip("Needs the flock(1) tool (util-linux) to hold the lock from another process.");
        }

        using var temp = new TempDirectory();
        var ct = TestContext.Current.CancellationToken;
        var lockFile = Path.Combine(temp.Path, RootLock.FileName);

        // Another process holds the lock: we can't take it.
        using (var holder = StartFlock(lockFile, "--exclusive", "--", "sleep", "30"))
        {
            await WaitUntilAsync(() => RootLock.TryAcquire(temp.Path) is not { } acquired || Release(acquired), ct);
            await Assert.ThrowsAsync<TimeoutException>(() => RootLock.AcquireAsync(temp.Path, TimeSpan.FromMilliseconds(300), ct));
            holder.Kill(entireProcessTree: true);   // flock's child inherits the locked descriptor
            await holder.WaitForExitAsync(ct);
        }

        using var ours = await RootLock.AcquireAsync(temp.Path, TimeSpan.FromSeconds(10), ct);

        // We hold the lock: another process can't take it.
        using var contender = StartFlock(lockFile, "--nonblock", "--conflict-exit-code", "42", "--", "true");
        await contender.WaitForExitAsync(ct);
        Assert.Equal(42, contender.ExitCode);
    }

    private static bool Release(RootLock acquired)
    {
        acquired.Dispose();
        return false;
    }

    private static Process StartFlock(string lockFile, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("/usr/bin/flock") { UseShellExecute = false };
        startInfo.ArgumentList.Add(arguments[0]);
        foreach (var argument in arguments.Skip(1).TakeWhile(a => a != "--"))
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.ArgumentList.Add(lockFile);
        foreach (var argument in arguments.SkipWhile(a => a != "--").Skip(1))
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo)!;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), "The other process didn't take the lock.");
            await Task.Delay(50, cancellationToken);
        }
    }
}
