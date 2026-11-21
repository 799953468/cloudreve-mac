using Cloudreve.Desktop;
using Xunit;

namespace Cloudreve.Sync.Tests;

public sealed class PairWakeTests
{
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