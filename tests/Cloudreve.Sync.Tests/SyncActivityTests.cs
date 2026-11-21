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
            long id = activity.ChangesSince(-1)!.Transfer!.Id;
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
                Assert.Equal(4, activity.ChangesSince(-1)!.Transfer!.Completed);
                activity.ReportBytes(2);
                Assert.Equal(4, activity.ChangesSince(-1)!.Transfer!.Completed);
            }

            activity.BeginTransfer("读取云端内容", 4);
            activity.SetPaused(id, true);
            Assert.False(activity.ChangesSince(-1)!.Transfer!.Paused);
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
        activity.SetPaused(activity.ChangesSince(-1)!.Transfer!.Id, true);
        using (var cancel = new CancellationTokenSource())
        {
            var waiting = activity.WaitForResumeAsync(cancel.Token);
            await cancel.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await waiting; });
        }

        activity.Finish("已取消");
        Assert.Null(activity.ChangesSince(-1)!.Transfer);
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
        Assert.Equal("current", snapshot.Current?.Key);
        Assert.Equal(SyncActivity.Capacity, snapshot.Recent.Length);
        Assert.Equal("1", snapshot.Recent[^1].Key);
        Assert.Null(activity.ChangesSince(snapshot.Revision));
        activity.Finish("失败");
        var updated = Assert.IsType<ActivitySnapshot>(activity.ChangesSince(snapshot.Revision));
        Assert.Null(updated.Current);
        Assert.Equal("失败", updated.Recent[0].Status);
    }
}