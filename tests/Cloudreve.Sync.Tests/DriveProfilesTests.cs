using Cloudreve.Desktop;
using Xunit;

namespace Cloudreve.Sync.Tests;

public sealed class DriveProfilesTests : IDisposable
{
    private readonly DirectoryInfo temp = Directory.CreateTempSubdirectory("cloudreve-profiles-");

    [Fact]
    public void IndependentSessionsAreDiscoveredRotatedAndForgottenSeparately()
    {
        var primary = new SessionFileStore(Path.Combine(temp.FullName, "app"));
        var catalog = new DriveProfiles(primary);
        var second = catalog.Create();
        var saved = new DriveSession("https://site.example/", "first", "Main", "cloudreve://my/", "one", "access", "refresh",
            DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow.AddDays(1));
        primary.Save(saved);
        second.Save(saved with { DriveId = "second", UserId = "two", RefreshToken = "second-refresh" });
        var restored = Assert.Single(new DriveProfiles(primary).OtherStores());
        var session = Assert.IsType<DriveSession>(restored.Load());
        restored.Save(session with { RefreshToken = "rotated" });
        Assert.Equal("rotated", second.Load()?.RefreshToken);
        Assert.Equal(saved, primary.Load());
        restored.Forget();
        Assert.Empty(catalog.OtherStores());
        Assert.Equal(saved, primary.Load());
    }

    [Fact]
    public void ProfilesCannotPairOverlappingTreesOrApplicationSettings()
    {
        var primary = new SessionFileStore(Path.Combine(temp.FullName, "settings"));
        var catalog = new DriveProfiles(primary);
        var second = catalog.Create();
        string root = temp.CreateSubdirectory("files").FullName;
        catalog.Reserve(primary, root);
        Assert.Throws<IOException>(() => catalog.Reserve(second, root));
        Assert.Throws<IOException>(() => catalog.Reserve(second, Path.Combine(root, "docs")));
        Assert.Throws<IOException>(() => catalog.Reserve(second, primary.DirectoryPath));
        Assert.Throws<IOException>(() => catalog.Reserve(second, temp.FullName));
        catalog.Reserve(second, root + "-separate");
        catalog.Release(primary);
        catalog.Reserve(second, root);
    }

    public void Dispose()
    {
        temp.Delete(true);
    }
}