using Cloudreve.Desktop;
using Xunit;

namespace Cloudreve.Sync.Tests;

public sealed class PairWakeTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HealthyWatchersSkipPollTicksUntilChangeOrDisconnect(bool changed)
    {
        var root = Directory.CreateTempSubdirectory("cloudreve-idle-");
        var wake = new PairWake(TimeSpan.FromMilliseconds(30));
        try
        {
            using (var watcher = wake.WatchLocal(root.FullName))
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                Assert.NotNull(watcher);
                wake.SetRemoteAvailable(true);
                var next = wake.WaitForNextScanAsync(false, timeout.Token);
                await Task.Delay(TimeSpan.FromMilliseconds(180), timeout.Token);
                Assert.False(next.IsCompleted);

                if (changed)
                {
                    File.WriteAllText(Path.Combine(root.FullName, "changed"), "new");
                }
                else
                {
                    wake.SetRemoteAvailable(false);
                }

                await next;
            }
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public async Task HealthyWatchersCanStayIdleUntilShutdown()
    {
        var root = Directory.CreateTempSubdirectory("cloudreve-idle-");
        var wake = new PairWake(TimeSpan.FromMilliseconds(25));
        try
        {
            using (var watcher = wake.WatchLocal(root.FullName))
            using (var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(300)))
            {
                Assert.NotNull(watcher);
                wake.SetRemoteAvailable(true);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wake.WaitForNextScanAsync(false, timeout.Token));
            }
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public async Task NotificationsDuringFailureDoNotBypassRetryOrQueueAnotherPass()
    {
        var wake = new PairWake(TimeSpan.FromMilliseconds(500));
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            wake.Signal();
            var retry = wake.WaitForNextScanAsync(true, timeout.Token);
            await Task.Delay(TimeSpan.FromMilliseconds(250), timeout.Token);
            Assert.False(retry.IsCompleted);
            wake.Signal();
            await retry;
            Assert.False(await wake.WaitAsync(TimeSpan.FromMilliseconds(25), timeout.Token));
        }
    }

    [Theory]
    [InlineData("notes/work.txt", true)]
    [InlineData("notes/.DS_Store", false)]
    [InlineData(".cloudreve-recovery/old/work.txt", false)]
    [InlineData("notes/.cloudreve-upload.part", false)]
    public void FiltersInternalPaths(string path, bool relevant)
    {
        Assert.Equal(relevant, PairWake.IsRelevant(path));
    }

    [Fact]
    public async Task ConcurrentHintsCoalesceAndWaitCanBeCancelled()
    {
        var wake = new PairWake();
        Parallel.For(0, 100, _ => { wake.Signal(); });
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            await wake.WaitAsync(TimeSpan.FromMinutes(1), timeout.Token);
        }

        using (var cancel = new CancellationTokenSource())
        {
            var next = wake.WaitAsync(TimeSpan.FromMinutes(1), cancel.Token);
            Assert.False(next.IsCompleted);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next);
        }
    }

    [Fact]
    public async Task ALocalCreateWakesTheScanner()
    {
        var temp = Directory.CreateTempSubdirectory("cloudreve-watch-");
        try
        {
            var wake = new PairWake();
            using (var watcher = wake.WatchLocal(temp.FullName))
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                Assert.NotNull(watcher);
                await File.WriteAllTextAsync(Path.Combine(temp.FullName, "changed"), "content", timeout.Token);
                await wake.WaitAsync(TimeSpan.FromMinutes(1), timeout.Token);
            }
        }
        finally
        {
            temp.Delete(true);
        }
    }
}
