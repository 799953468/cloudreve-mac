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

                var scope = await next;
                if (changed)
                {
                    Assert.NotNull(scope);
                    Assert.True(scope.Covers("changed"));
                    Assert.False(scope.Covers("unrelated"));
                }
                else
                {
                    Assert.Null(scope);
                }
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
    public async Task CoalescesLocalAndRemoteRenamePathsAcrossBatches()
    {
        var wake = new PairWake();
        wake.SignalRemoteChanges("""[{"type":"rename","from":"/old/a","to":"/new/a"},{"type":"modify","from":"/café"}]""");
        wake.SignalPaths("local/edit", ".DS_Store");
        var first = await wake.WaitForNextScanAsync(false, TestContext.Current.CancellationToken);
        Assert.NotNull(first);
        Assert.True(first.Covers("old/a") && first.Covers("new/a") && first.Covers("local/edit") && first.Covers("café"));
        Assert.False(first.Covers("old/b") || first.Covers(".DS_Store"));
        wake.SignalPaths("later");
        var next = await wake.WaitForNextScanAsync(false, TestContext.Current.CancellationToken);
        Assert.NotNull(next);
        Assert.True(next.Covers("later"));
        Assert.False(next.Covers("local/edit"));
    }

    [Theory]
    [InlineData("[{}]")]
    [InlineData("bad json")]
    [InlineData("""[{"type":"rename","from":"/old"}]""")]
    [InlineData("""[{"type":"modify","from":"/../outside"}]""")]
    [InlineData("""[{"type":"modify","from":"/"}]""")]
    public async Task InvalidRemoteBatchRequestsFullScan(string data)
    {
        var wake = new PairWake();
        wake.SignalPaths("first");
        wake.SignalRemoteChanges(data);
        wake.SignalPaths("last");
        Assert.Null(await wake.WaitForNextScanAsync(false, TestContext.Current.CancellationToken));
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
    public async Task FullRemoteListingCoversEarlierReconnectAndPreservesLocalEdits()
    {
        var wake = new PairWake();
        wake.RequestRemoteCatchUp();
        wake.BeginFullRemoteInventory();
        Assert.False(await wake.WaitAsync(TimeSpan.FromMilliseconds(25), TestContext.Current.CancellationToken));

        wake.RequestRemoteCatchUp();
        wake.SignalPaths("edited");
        wake.BeginFullRemoteInventory();
        var next = await wake.WaitForNextScanAsync(false, TestContext.Current.CancellationToken);
        Assert.NotNull(next);
        Assert.True(next.Covers("edited"));
        Assert.False(next.Covers("other"));

        wake.RequestRemoteCatchUp();
        Assert.Null(await wake.WaitForNextScanAsync(false, TestContext.Current.CancellationToken));
        wake.Signal();
        wake.BeginFullRemoteInventory();
        Assert.Null(await wake.WaitForNextScanAsync(false, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReportsWhyAScanCoversTheWholePair()
    {
        var cancel = TestContext.Current.CancellationToken;
        var wake = new PairWake();
        wake.SignalPaths("edited");
        Assert.NotNull(await wake.WaitForNextScanAsync(false, cancel));
        Assert.Null(wake.LastFullReason);

        wake.RequestRemoteCatchUp();
        Assert.Null(await wake.WaitForNextScanAsync(false, cancel));
        Assert.Equal("RemoteCatchUp", wake.LastFullReason);

        wake.SignalPaths(Enumerable.Range(0, 1025).Select(i => "file" + i));
        wake.Signal("IgnoreRulesChanged");
        Assert.Null(await wake.WaitForNextScanAsync(false, cancel));
        Assert.Equal("TooManyChangedPaths", wake.LastFullReason);

        wake.SignalRemoteChanges("""[{"type":"restore","from":"/a"}]""");
        Assert.Null(await wake.WaitForNextScanAsync(false, cancel));
        Assert.Equal("UnrecognizedRemoteEvent", wake.LastFullReason);
    }

    [Fact]
    public async Task FailedPathScanRetriesOriginalAndNewEdits()
    {
        var temp = Directory.CreateTempSubdirectory();
        try
        {
            var wake = new PairWake(TimeSpan.FromMilliseconds(50));
            using (var watcher = wake.WatchLocal(temp.FullName))
            {
                Assert.NotNull(watcher);
                wake.SetRemoteAvailable(true);
                wake.SignalPaths("another");
                var next = await wake.WaitForNextScanAsync(true, TestContext.Current.CancellationToken, new PairScanScope(["edited"]));
                Assert.NotNull(next);
                Assert.True(next.Covers("edited"));
                Assert.True(next.Covers("another"));
                Assert.False(next.Covers("other"));
            }
        }
        finally
        {
            temp.Delete(true);
        }
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
