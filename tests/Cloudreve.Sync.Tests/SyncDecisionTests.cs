using Cloudreve.Desktop;
using Xunit;

namespace Cloudreve.Sync.Tests;

public class SyncDecisionTests
{
    [Theory]
    [InlineData(false, null, null, "Forget")]
    [InlineData(false, "a", null, "Upload")]
    [InlineData(false, null, "1", "Download")]
    [InlineData(false, "a", "1", "CompareContents")]
    [InlineData(true, "a", "1", "Unchanged")]
    [InlineData(true, "b", "1", "Upload")]
    [InlineData(true, "a", "2", "Download")]
    [InlineData(true, "b", "2", "CompareContents")]
    [InlineData(true, null, "1", "TrashRemote")]
    [InlineData(true, "a", null, "RecoverLocal")]
    [InlineData(true, null, "2", "Conflict")]
    [InlineData(true, "b", null, "Conflict")]
    [InlineData(true, null, null, "Forget")]
    public void ComparesBothSidesWithTheirLastCommonBaseline(bool known, string? local, string? remote, string expected)
    {
        FileBaseline? baseline = known ? new FileBaseline("a", "1") : null;
        Assert.Equal(expected, SyncDecision.ForFile(baseline, local, remote).ToString());
    }

    [Theory]
    [InlineData(0, -3600, "KeepLocal")]
    [InlineData(-3600, 0, "KeepRemote")]
    [InlineData(0, 2, null)]
    [InlineData(0, -2, null)]
    [InlineData(0, null, null)]
    [InlineData(null, 0, null)]
    public void NewerSideNeedsAClearlyNewerTime(int? localOffset, int? remoteOffset, string? expected)
    {
        var start = new DateTimeOffset(2026, 11, 28, 12, 0, 0, TimeSpan.Zero);
        var choice = Cloudreve.Desktop.FolderPairSync.NewerSide(localOffset is { } l ? start.AddSeconds(l) : null, remoteOffset is { } r ? start.AddSeconds(r) : null);
        Assert.Equal(expected, choice?.ToString());
    }
}
