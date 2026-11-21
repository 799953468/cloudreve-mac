using System.Runtime.Versioning;
using Cloudreve.Desktop;
using Xunit;

namespace Cloudreve.Sync.Tests;

public sealed class SessionFileStoreTests : IDisposable
{
    private readonly DirectoryInfo temp = Directory.CreateTempSubdirectory("cloudreve-sessions-");
    private readonly SessionFileStore store;
    private readonly string file;

    private readonly DriveSession session = new("https://site.example/", "drive", "Work", "cloudreve://my/", "user", "access", "refresh",
        DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow.AddDays(1));

    public SessionFileStoreTests()
    {
        store = new SessionFileStore(Path.Combine(temp.FullName, "Cloudreve"));
        file = Path.Combine(store.DirectoryPath, "drives.json");
    }

    [Fact]
    public void MissingSessionDoesNotCreateSettingsOnStartupOrDisconnect()
    {
        Assert.Null(store.Load());
        store.Forget();
        Assert.False(Directory.Exists(store.DirectoryPath));
    }

    [Fact]
    [SupportedOSPlatform("macos")]
    public void SavedSessionSurvivesReopenWithPrivatePermissions()
    {
        const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        Directory.CreateDirectory(store.DirectoryPath);
        File.SetUnixFileMode(store.DirectoryPath, ownerOnly | UnixFileMode.UserExecute | UnixFileMode.OtherExecute);
        store.Save(session);

        var reopened = new SessionFileStore(store.DirectoryPath);
        Assert.Equal(session, reopened.Load());
        Assert.Equal(ownerOnly | UnixFileMode.UserExecute, File.GetUnixFileMode(store.DirectoryPath));
        Assert.Equal(ownerOnly, File.GetUnixFileMode(file));
        File.SetUnixFileMode(file, ownerOnly | UnixFileMode.OtherRead);
        Assert.Equal(session, reopened.Load());
        Assert.Equal(ownerOnly, File.GetUnixFileMode(file));

        var rotated = session with { AccessToken = "rotated-access", RefreshToken = "rotated-refresh" };
        reopened.Save(rotated);
        Assert.Equal(rotated, store.Load());
        Assert.Equal(ownerOnly, File.GetUnixFileMode(file));
        Assert.Equal(file, Assert.Single(Directory.GetFiles(store.DirectoryPath)));
    }

    [Fact]
    public void ForgetDeletesOnlyTheLoginFile()
    {
        store.Save(session);
        string other = Path.Combine(store.DirectoryPath, "other.json");
        File.WriteAllText(other, "keep");
        store.Forget();
        store.Forget();
        Assert.False(File.Exists(file));
        Assert.Null(new SessionFileStore(store.DirectoryPath).Load());
        Assert.Equal("keep", File.ReadAllText(other));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("null")]
    public void DamagedSessionCanBeReplacedByANewLogin(string content)
    {
        Directory.CreateDirectory(store.DirectoryPath);
        File.WriteAllText(file, content);
        Assert.Throws<InvalidDataException>(() => store.Load());
        Assert.Equal(content, File.ReadAllText(file));
        store.Save(session);
        Assert.Equal(session, new SessionFileStore(store.DirectoryPath).Load());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LoginFileCannotBeALiveOrDanglingLink(bool targetExists)
    {
        Directory.CreateDirectory(store.DirectoryPath);
        string target = Path.Combine(temp.FullName, "target");
        if (targetExists)
        {
            File.WriteAllText(target, "untouched");
        }

        File.CreateSymbolicLink(file, target);
        Assert.Throws<IOException>(() => store.Load());
        Assert.Throws<IOException>(() => store.Save(session));
        Assert.Throws<IOException>(() => store.Forget());
        Assert.NotNull(new FileInfo(file).LinkTarget);
        Assert.Equal(targetExists, File.Exists(target));
        if (targetExists)
        {
            Assert.Equal("untouched", File.ReadAllText(target));
        }
    }

    [Fact]
    public void LinkedSettingsDirectoryIsNotFollowed()
    {
        string target = temp.CreateSubdirectory("other").FullName;
        Directory.CreateSymbolicLink(store.DirectoryPath, target);
        Assert.Throws<IOException>(() => store.Load());
        Assert.Throws<IOException>(() => store.Save(session));
        Assert.Throws<IOException>(() => store.Forget());
        Assert.Empty(Directory.GetFileSystemEntries(target));
    }

    public void Dispose()
    {
        temp.Delete(true);
    }
}