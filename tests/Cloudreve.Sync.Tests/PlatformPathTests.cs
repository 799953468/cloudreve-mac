using Cloudreve.Desktop;
using Xunit;

namespace Cloudreve.Sync.Tests;

public sealed class PlatformPathTests
{
    [Theory]
    [InlineData("", null)]
    [InlineData("a", "")]
    [InlineData("a/b", "a")]
    [InlineData("a/b/c.txt", "a/b")]
    public void KeyParentMatchesDirectoryNameWithForwardSlashes(string key, string? parent)
    {
        Assert.Equal(parent, PairKey.Parent(key));
    }

    [Fact]
    public void KeysAlwaysUseForwardSlashes()
    {
        Assert.Equal("a", PairKey.Combine("", "a"));
        Assert.Equal("a/b/c", PairKey.Combine("a/b", "c"));
        string relative = Path.Combine("docs", "café.txt");
        Assert.Equal("docs/café.txt", PairKey.FromLocal(relative));
    }

    [Fact]
    public void LocalFolderOverlapRespectsPathBoundaries()
    {
        string root = Path.Combine(Path.GetTempPath(), "cloudreve");
        Assert.Equal(root + Path.DirectorySeparatorChar, DriveProfiles.AsDirectory(root));
        string fileSystemRoot = Path.GetPathRoot(Path.GetTempPath())!;
        Assert.Equal(fileSystemRoot, DriveProfiles.AsDirectory(fileSystemRoot));
    }

    [Theory]
    [InlineData("report.docx", true)]
    [InlineData("名称 with spaces", true)]
    [InlineData("a:b", false)]
    [InlineData("what?", false)]
    [InlineData("trailing.", false)]
    [InlineData("trailing ", false)]
    [InlineData("CON", false)]
    [InlineData("nul.txt", false)]
    [InlineData("COM1 .log", false)]
    [InlineData("console", true)]
    [InlineData("COM10", true)]
    public void WindowsRejectsNamesItCannotStore(string name, bool storable)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows naming rules.");
        Assert.Equal(storable, LocalFile.IsLocalName(name));
    }

    [Fact]
    public void WindowsIdentitiesFollowTheFileAndNotItsName()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows file IDs.");
        var temp = Directory.CreateTempSubdirectory("cloudreve-id-");
        try
        {
            string first = Path.Combine(temp.FullName, "first.txt");
            string second = Path.Combine(temp.FullName, "second.txt");
            File.WriteAllText(first, "one");
            string? id = LocalFile.Identity(first);
            var before = LocalFile.GetContentVersion(first);
            Assert.NotNull(id);
            Assert.DoesNotContain(':', id[..32]);
            Assert.Equal(':', id[32]);
            File.Move(first, second);
            Assert.Equal(id, LocalFile.Identity(second));
            File.AppendAllText(second, "two");
            var after = LocalFile.GetContentVersion(second);
            if (before != null && after != null)
            {
                Assert.Equal(before.Value.LocalId, after.Value.LocalId);
                Assert.NotEqual(before.Value.Generation, after.Value.Generation);
            }

            Assert.Equal(NodeKind.File, LocalFile.Inspect(second));
            Assert.Equal(NodeKind.Directory, LocalFile.Inspect(temp.FullName));
            Assert.Null(LocalFile.Inspect(first));
            Assert.Equal(DirectoryChangeResult.Applied, LocalFile.ChangeDirectoryEntry(Path.Combine(temp.FullName, "dir"), create: true));
            Assert.Equal(DirectoryChangeResult.PathChanged, LocalFile.ChangeDirectoryEntry(Path.Combine(temp.FullName, "dir"), create: true));
            File.WriteAllText(Path.Combine(temp.FullName, "dir", "child"), "");
            Assert.Equal(DirectoryChangeResult.NotEmpty, LocalFile.ChangeDirectoryEntry(Path.Combine(temp.FullName, "dir"), create: false));
            Assert.Equal(DirectoryChangeResult.PathChanged, LocalFile.ChangeDirectoryEntry(Path.Combine(temp.FullName, "missing", "new"), create: true));
        }
        finally
        {
            temp.Delete(true);
        }
    }
}
