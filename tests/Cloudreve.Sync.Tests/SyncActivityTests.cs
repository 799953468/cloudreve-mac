using Cloudreve.Desktop;
using Xunit;

namespace Cloudreve.Sync.Tests;

public sealed class SyncActivityTests
{
    [Fact]
    public async Task PausedUploadWritesNothingUntilResumedAndCountsAbsoluteOffsets()
    {
        var temp = Directory.CreateTempSubdirectory();
        try
        {
            string path = Path.Combine(temp.FullName, "file");
            File.WriteAllBytes(path, [0, 1, 2, 3]);
            var activity = new SyncActivity();
            activity.Start("上传", "file");
            activity.BeginTransfer("上传", 4);
            long id = Assert.Single(activity.ChangesSince(-1)!.Active).Transfer!.Id;
            activity.SetPaused(id, true);
            using (var input = File.OpenRead(path))
            using (var slice = new FileSliceContent(input.SafeFileHandle, 1, 3, activity: activity))
            using (var output = new MemoryStream())
            {
                var copying = slice.CopyToAsync(output, TestContext.Current.CancellationToken);
                Assert.False(copying.IsCompleted);
                Assert.Equal(0, output.Length);
                activity.SetPaused(id, false);
                await copying.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                Assert.Equal(new byte[] { 1, 2, 3 }, output.ToArray());
                Assert.Equal(4, Assert.Single(activity.ChangesSince(-1)!.Active).Transfer!.Completed);
                activity.ReportBytes(2);
                Assert.Equal(4, Assert.Single(activity.ChangesSince(-1)!.Active).Transfer!.Completed);
            }

            activity.BeginTransfer("读取云端内容", 4);
            activity.SetPaused(id, true);
            Assert.False(Assert.Single(activity.ChangesSince(-1)!.Active).Transfer!.Paused);
        }
        finally
        {
            temp.Delete(true);
        }
    }

    [Fact]
    public async Task CancellingPausedTransferDoesNotHangShutdown()
    {
        var activity = new SyncActivity();
        activity.Start("下载", "doc");
        activity.BeginTransfer("下载", 10);
        activity.SetPaused(Assert.Single(activity.ChangesSince(-1)!.Active).Transfer!.Id, true);
        using (var cancel = new CancellationTokenSource())
        {
            var waiting = activity.WaitForResumeAsync(cancel.Token);
            await cancel.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await waiting; });
        }

        activity.Finish("已取消");
        Assert.Empty(activity.ChangesSince(-1)!.Active);
    }

    [Fact]
    public async Task PausingOneFileDoesNotPauseOrResetItsPeer()
    {
        var activity = new SyncActivity();
        var first = activity.CreateWorker();
        var second = activity.CreateWorker();
        first.Start("下载", "one");
        second.Start("上传", "two");
        first.BeginTransfer("下载", 50);
        second.BeginTransfer("上传", 100);
        var id = activity.ChangesSince(-1)!.Active.Single(file => file.Entry.Key == "one").Transfer!.Id;
        activity.SetPaused(id, true);
        var cancel = TestContext.Current.CancellationToken;
        var waiting = first.WaitForResumeAsync(cancel);
        Assert.False(waiting.IsCompleted);
        await second.WaitForResumeAsync(cancel);
        second.ReportBytes(42);
        Assert.Equal(42, activity.ChangesSince(-1)!.Active.Single(file => file.Entry.Key == "two").Transfer!.Completed);
        second.Finish("完成");
        Assert.False(waiting.IsCompleted);
        activity.SetPaused(id, false);
        await waiting.WaitAsync(TimeSpan.FromSeconds(5), cancel);
        first.Finish("完成");
        Assert.Empty(activity.ChangesSince(-1)!.Active);
    }

    [Fact]
    public void WaitingSnapshotKeepsManifestOrderAndCountsItemsBeyondTheVisibleLimit()
    {
        var activity = new SyncActivity();
        var jobs = Enumerable.Range(0, SyncActivity.Capacity + 3).Reverse().Select(i =>
            new QueuedFile(Guid.NewGuid(), new FileTransfer($"folder/{i}.txt", i % 2 == 0 ? SyncAction.Upload : SyncAction.Download, null, null, null))).ToArray();
        activity.SetPending(jobs);
        var worker = activity.CreateWorker();
        worker.Start("上传", jobs[1].File.Key);

        var snapshot = activity.ChangesSince(-1)!;
        var expected = jobs.Where(job => job != jobs[1]).Take(SyncActivity.Capacity).Select(job => new WaitingFile(job.File.Key, job.File.Action));
        Assert.Equal(expected, snapshot.Waiting);
        Assert.Equal(jobs.Length - 1, snapshot.WaitingCount);
        Assert.Equal(jobs[1].File.Key, Assert.Single(snapshot.Active).Entry.Key);
    }

    [Fact]
    public void SeparatesCurrentFromBoundedRecentHistory()
    {
        var activity = new SyncActivity();
        for (int i = 0; i <= SyncActivity.Capacity; i++)
        {
            activity.Start("上传", i.ToString());
            activity.Finish("完成");
        }

        activity.Start("下载", "current");
        var snapshot = Assert.IsType<ActivitySnapshot>(activity.ChangesSince(-1));
        Assert.Equal("current", Assert.Single(snapshot.Active).Entry.Key);
        Assert.Equal(SyncActivity.Capacity, snapshot.Recent.Length);
        Assert.Equal("1", snapshot.Recent[^1].Key);
        Assert.Null(activity.ChangesSince(snapshot.Revision));
        activity.Finish("失败");
        var updated = Assert.IsType<ActivitySnapshot>(activity.ChangesSince(snapshot.Revision));
        Assert.Empty(updated.Active);
        Assert.Equal("失败", updated.Recent[0].Status);
    }
}
