using System.Net;
using Cloudreve.Desktop;
using Xunit;

namespace Cloudreve.Sync.Tests;

public sealed class AdaptiveTransfersTests
{
    [Fact]
    public async Task ADeepTreeWithOnlyOneChildPerDirectoryDoesNotIncreaseTheWindow()
    {
        var scheduler = new AdaptiveWorkQueue();
        var scanned = new List<int>();
        await scheduler.TraverseAsync([0], (depth, _) => Task.FromResult(depth), (depth, enqueue) =>
        {
            scanned.Add(depth);
            if (depth < 20)
            {
                enqueue(depth + 1);
            }
        }, TestContext.Current.CancellationToken);

        Assert.Equal(Enumerable.Range(0, 21), scanned);
        Assert.Equal(2, scheduler.Limit);
    }

    [Fact]
    public async Task GrowsWithBacklogWithoutExceedingSixActiveFiles()
    {
        var scheduler = new AdaptiveWorkQueue();
        int[] items = Enumerable.Range(0, 32).ToArray();
        var started = items.Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var release = items.Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var finished = new List<int>();
        int active = 0, peak = 0;
        var cancel = TestContext.Current.CancellationToken;
        var run = scheduler.RunAsync(items, async (item, token) =>
        {
            int count = Interlocked.Increment(ref active);
            peak = Math.Max(peak, count);
            started[item].SetResult();
            try
            {
                await release[item].Task.WaitAsync(TimeSpan.FromSeconds(10), token);
                return item;
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }, finished.Add, cancel);

        Assert.True(started[1].Task.IsCompleted);
        Assert.False(started[2].Task.IsCompleted);
        for (int i = 0; i < items.Length; i++)
        {
            // Wait for the current window to fill before completing its oldest file.
            int last = Math.Min(items.Length - 1, i + scheduler.Limit - 1);
            await started[last].Task.WaitAsync(TimeSpan.FromSeconds(10), cancel);
            release[i].SetResult();
            if (i + 1 < items.Length)
            {
                await started[Math.Min(items.Length - 1, last + 1)].Task.WaitAsync(TimeSpan.FromSeconds(10), cancel);
            }
        }

        await run;
        Assert.Equal(6, peak);
        Assert.Equal(6, scheduler.Limit);
        Assert.Equal(items, finished.Order());
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task ThrottlingCancelsAndDrainsPeersBeforeTheNextScan(HttpStatusCode status)
    {
        var scheduler = new AdaptiveWorkQueue();
        var cancel = TestContext.Current.CancellationToken;
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool drained = false;
        var started = new List<int>();
        async Task<int> TransferAsync(int item, CancellationToken token)
        {
            started.Add(item);
            if (item == 0)
            {
                await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
                throw new HttpRequestException("busy", null, status);
            }

            bothStarted.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return item;
            }
            finally
            {
                drained = true;
            }
        }

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => scheduler.RunAsync([0, 1, 2], TransferAsync,
            _ => Assert.Fail("Failed files must not be checkpointed."), cancel));
        Assert.Equal(status, error.StatusCode);
        Assert.Equal([0, 1], started);
        Assert.True(drained);
        Assert.Equal(1, scheduler.Limit);

        var finished = new List<int>();
        await scheduler.RunAsync([2, 3, 4], (item, _) => Task.FromResult(item), finished.Add, cancel);
        Assert.Equal([2, 3, 4], finished);
        Assert.True(scheduler.Limit > 1);
    }
}
