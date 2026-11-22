using Cloudreve.Desktop;
using Xunit;

namespace Cloudreve.Sync.Tests;

public sealed class SyncTests : IDisposable
{
    private readonly string root;
    private readonly string local;
    private readonly string remote;
    private readonly string state;

    public SyncTests()
    {
        root = Directory.CreateTempSubdirectory("cloudreve-tests-").FullName;
        local = Directory.CreateDirectory(Path.Combine(root, "local")).FullName;
        remote = Directory.CreateDirectory(Path.Combine(root, "remote")).FullName;
        state = Path.Combine(root, "state", "pair.json");
    }

    private SyncResult Run()
    {
        return new SyncEngine(local, remote, state).Run();
    }

    [Fact]
    public void UpgradesLegacyStateAndReusesOnlyVerifiedContentVersions()
    {
        string left = Path.Combine(local, "formatVersion");
        string right = Path.Combine(remote, "formatVersion");
        File.WriteAllText(left, "abc");
        File.WriteAllText(right, "abc");
        Directory.CreateDirectory(Path.GetDirectoryName(state)!);
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData("abc"u8));
        File.WriteAllText(state, System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { ["formatVersion"] = hash }));
        Assert.Equal(1, Run().Unchanged);
        Assert.SkipWhen(MacFile.GetContentVersion(left) == null || MacFile.GetContentVersion(right) == null, "Volume lacks content generations.");
        using (var locked = new FileStream(left, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(1, Run().Unchanged);
        }
        var modified = File.GetLastWriteTimeUtc(left);
        File.WriteAllText(left, "xyz");
        File.SetLastWriteTimeUtc(left, modified);
        Assert.Equal(1, Run().Uploaded);
        Assert.Equal("xyz", File.ReadAllText(right));
    }

    [Fact]
    public void CopiesBothDirectionsAndRecognizesSameLengthEdits()
    {
        Directory.CreateDirectory(Path.Combine(local, "文稿"));
        string source = Path.Combine(local, "文稿", "A.txt");
        string copy = Path.Combine(remote, "文稿", "A.txt");
        File.WriteAllText(source, "one");
        File.WriteAllText(Path.Combine(remote, "B.txt"), "two");
        var first = Run();
        Assert.Equal(1, first.Uploaded);
        Assert.Equal(1, first.Downloaded);
        Assert.Equal(2, Run().Unchanged);
        File.WriteAllText(copy, "ABC");
        Assert.Equal(1, Run().Downloaded);
        Assert.Equal("ABC", File.ReadAllText(source));
        File.WriteAllText(source, "123");
        Assert.Equal(1, Run().Uploaded);
        Assert.Equal("123", File.ReadAllText(copy));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConflictingCopiesSurviveRepeatedRuns(bool withBaseline)
    {
        string l = Path.Combine(local, "a");
        string r = Path.Combine(remote, "a");
        if (withBaseline)
        {
            File.WriteAllText(l, "old");
            Run();
        }

        File.WriteAllText(l, "mine");
        File.WriteAllText(r, "theirs");
        Assert.Single(Run().Issues);
        Assert.Single(Run().Issues);
        Assert.Equal("mine", File.ReadAllText(l));
        Assert.Equal("theirs", File.ReadAllText(r));
    }

    [Fact]
    public void OneSidedDeletionRestoresTheSurvivingCopy()
    {
        string path = Path.Combine(local, "keep");
        File.WriteAllText(path, "keep");
        Run();
        File.Delete(path);
        Assert.Equal(1, Run().Downloaded);
        Assert.Equal("keep", File.ReadAllText(path));
    }

    [Fact]
    public void IgnoresDotFilesAndDoesNotFollowLinks()
    {
        File.WriteAllText(Path.Combine(local, ".private"), "secret");
        Directory.CreateSymbolicLink(Path.Combine(local, "loop"), root);
        Assert.Single(Run().Issues);
        Assert.Empty(Directory.GetFileSystemEntries(remote));
    }

    [Fact]
    public void DoesNotRecreateMissingRootOrSynchronizeNestedRoots()
    {
        Directory.Delete(remote);
        Assert.Throws<IOException>(() => Run());
        Assert.False(Directory.Exists(remote));
        Assert.Throws<IOException>(() => new SyncEngine(root, local, state).Run());
    }

    [Theory]
    [InlineData("https://user:secret@cloud.example/dav/")]
    [InlineData("http://cloud.example/dav/")]
    [InlineData("https://cloud.example/dav/?token=secret")]
    [InlineData("file:///Volumes/Cloud")]
    public void RejectsUnsafeEndpoint(string input)
    {
        using (var http = new HttpClient())
        {
            Assert.Throws<ArgumentException>(() => new DesktopLogin(http, input));
        }
    }

    public void Dispose()
    {
        Directory.Delete(root, true);
    }
}
