using System.Net.Http.Json;
using System.Text.Json;
using Cloudreve.Desktop;
using Xunit;

namespace Cloudreve.Sync.Tests;

// Runs the sync engine against a real Cloudreve server, as two computers paired with one cloud folder.
// Skipped unless CLOUDREVE_IT_SITE, CLOUDREVE_IT_EMAIL and CLOUDREVE_IT_PASSWORD are set. Each test works in its own
// new folder under the account's root and moves it to the trash afterwards. Never put the credentials in the repository.
[Collection("Real Cloudreve server")]
public sealed class RealServerTests : IAsyncLifetime
{
    private static readonly string? SiteUrl = Environment.GetEnvironmentVariable("CLOUDREVE_IT_SITE");
    private static readonly string? Email = Environment.GetEnvironmentVariable("CLOUDREVE_IT_EMAIL");
    private static readonly string? Password = Environment.GetEnvironmentVariable("CLOUDREVE_IT_PASSWORD");
    private static bool Configured
    {
        get { return !string.IsNullOrEmpty(SiteUrl) && !string.IsNullOrEmpty(Email) && !string.IsNullOrEmpty(Password); }
    }

    private readonly DirectoryInfo temp = Directory.CreateTempSubdirectory("cloudreve-it-");
    private readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
    private DriveSession session = null!;
    private DriveClient client = null!;
    private string? root;
    private string rootId = "";

    private static CancellationToken Cancel
    {
        get { return TestContext.Current.CancellationToken; }
    }

    public async ValueTask InitializeAsync()
    {
        if (!Configured)
        {
            return;
        }

        session = await LoginAsync();
        client = new DriveClient(http, http, session, rotated => { session = rotated; });
        string name = "cr-it-" + Guid.NewGuid().ToString("N")[..12];
        await client.CreateFolderAsync("cloudreve://my", name, Cancel);
        root = "cloudreve://my/" + name;
        rootId = await client.FolderIdAsync(root, Cancel);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (root != null)
            {
                await client.TrashAsync(await client.StatAsync(root, CancellationToken.None), CancellationToken.None);
            }
        }
        finally
        {
            http.Dispose();
            temp.Delete(true);
        }
    }

    private async Task<DriveSession> LoginAsync()
    {
        var site = new Uri(SiteUrl!);
        using var response = await http.PostAsJsonAsync(new Uri(site, "/api/v4/session/token"), new { email = Email, password = Password }, Cancel);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(Cancel);
        Assert.Equal(0, json.GetProperty("code").GetInt32());
        var data = json.GetProperty("data");
        var token = data.GetProperty("token");
        return new DriveSession(site.AbsoluteUri, Guid.NewGuid().ToString(), "Integration", "cloudreve://my", data.GetProperty("user").GetProperty("id").GetString()!,
            token.GetProperty("access_token").GetString()!, token.GetProperty("refresh_token").GetString()!,
            token.GetProperty("access_expires").GetDateTimeOffset(), token.GetProperty("refresh_expires").GetDateTimeOffset());
    }

    // One computer: its own settings, local folder and pair, all paired with this test's cloud folder.
    private sealed class Device
    {
        private readonly RealServerTests owner;
        private readonly FolderPairStore store;

        internal Device(RealServerTests owner, string name)
        {
            this.owner = owner;
            var deviceSession = owner.session with { Root = owner.root! };
            store = new FolderPairStore(System.IO.Path.Combine(owner.temp.FullName, name, "settings"), deviceSession);
            Pair = store.Create(owner.temp.CreateSubdirectory(name + "-local").FullName, owner.root!, owner.rootId);
        }

        internal FolderPair Pair { get; }

        internal string At(string key)
        {
            return System.IO.Path.Combine(Pair.Local, key.Replace('/', System.IO.Path.DirectorySeparatorChar));
        }

        internal FolderPairSync Engine()
        {
            return new FolderPairSync(store, Pair, owner.client);
        }

        internal async Task<PairSyncResult> FullAsync()
        {
            var attempt = new PairScanAttempt { ServerTime = await owner.client.ServerTimeAsync(Cancel) };
            var result = await Engine().RunAsync(Cancel, attempt);
            Assert.Null(attempt.Scope);
            return result;
        }

        internal async Task<(PairSyncResult Result, PairScanAttempt Attempt, CatchUpPlan Plan)> CatchUpAsync()
        {
            var now = await owner.client.ServerTimeAsync(Cancel);
            var engine = Engine();
            var plan = await engine.PlanCatchUpAsync(true, true, [], now, Cancel);
            Assert.Null(plan.FullReason);
            var attempt = new PairScanAttempt { ServerTime = now, CatchUpReason = "Startup", CoversCloudChanges = true };
            return (await engine.RunAsync(Cancel, attempt, plan.Scope), attempt, plan);
        }

        internal void Write(string key, string text)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(At(key))!);
            File.WriteAllText(At(key), text);
        }

        internal string[] Recovered()
        {
            string recovery = System.IO.Path.Combine(Pair.Local, PairInventory.Recovery);
            return Directory.Exists(recovery) ? Directory.GetFiles(recovery, "*", SearchOption.AllDirectories).Select(File.ReadAllText).ToArray() : [];
        }
    }

    [Fact]
    public async Task TwoComputersShareCreatesEditsRenamesAndDeletes()
    {
        Assert.SkipUnless(Configured, "Set CLOUDREVE_IT_SITE, CLOUDREVE_IT_EMAIL and CLOUDREVE_IT_PASSWORD to run against a real server.");
        var a = new Device(this, "a");
        var b = new Device(this, "b");
        a.Write("docs/note.txt", "v1");
        Directory.CreateDirectory(a.At("empty"));
        Assert.Equal(1, (await a.FullAsync()).Uploaded);
        Assert.Equal(1, (await b.FullAsync()).Downloaded);
        Assert.Equal("v1", File.ReadAllText(b.At("docs/note.txt")));
        Assert.True(Directory.Exists(b.At("empty")));

        a.Write("docs/note.txt", "v2");
        Assert.Equal(1, (await a.FullAsync()).Uploaded);
        Assert.Equal(1, (await b.FullAsync()).Downloaded);
        Assert.Equal("v2", File.ReadAllText(b.At("docs/note.txt")));

        File.Move(a.At("docs/note.txt"), a.At("docs/renamed.txt"));
        var renamed = await a.FullAsync();
        Assert.Equal((0, 0), (renamed.Uploaded, renamed.Downloaded));
        var followed = await b.FullAsync();
        Assert.Equal((0, 0), (followed.Uploaded, followed.Downloaded));
        Assert.Equal("v2", File.ReadAllText(b.At("docs/renamed.txt")));
        Assert.False(File.Exists(b.At("docs/note.txt")));

        File.Delete(a.At("docs/renamed.txt"));
        Assert.Equal(1, (await a.FullAsync()).Removed);
        Assert.Equal(1, (await b.FullAsync()).Removed);
        Assert.False(File.Exists(b.At("docs/renamed.txt")));
        Assert.Contains("v2", b.Recovered());
    }

    [Fact]
    public async Task CatchUpAppliesAnotherComputersChangesWithoutAFullScan()
    {
        Assert.SkipUnless(Configured, "Set CLOUDREVE_IT_SITE, CLOUDREVE_IT_EMAIL and CLOUDREVE_IT_PASSWORD to run against a real server.");
        var a = new Device(this, "a");
        var b = new Device(this, "b");
        a.Write("edit.txt", "before");
        a.Write("move/old.txt", "moved");
        a.Write("gone.txt", "gone");
        a.Write("quiet/untouched.txt", "same");
        await a.FullAsync();
        await b.FullAsync();

        a.Write("edit.txt", "after");
        File.Move(a.At("move/old.txt"), a.At("move/new.txt"));
        File.Delete(a.At("gone.txt"));
        await a.FullAsync();

        var (result, attempt, plan) = await b.CatchUpAsync();
        Assert.Contains("gone.txt", plan.Scope!.ConfirmedRemoteDeletions);
        // Folders created within the cursor margin may be covered whole instead of path by path.
        Assert.True(plan.Scope.Covers("move/new.txt"));
        Assert.True(plan.Scope.Covers("move/old.txt"));
        Assert.NotNull(attempt.Scope);
        Assert.Null(attempt.FullReason);
        Assert.Empty(result.Conflicts);
        Assert.Equal("after", File.ReadAllText(b.At("edit.txt")));
        Assert.Equal("moved", File.ReadAllText(b.At("move/new.txt")));
        Assert.False(File.Exists(b.At("move/old.txt")));
        Assert.False(File.Exists(b.At("gone.txt")));
        Assert.Contains("gone", b.Recovered());
        Assert.Equal("same", File.ReadAllText(b.At("quiet/untouched.txt")));
    }

    [Fact]
    public async Task ConflictBetweenComputersKeepsTheNewerCopy()
    {
        Assert.SkipUnless(Configured, "Set CLOUDREVE_IT_SITE, CLOUDREVE_IT_EMAIL and CLOUDREVE_IT_PASSWORD to run against a real server.");
        var a = new Device(this, "a");
        var b = new Device(this, "b");
        a.Write("shared.txt", "base");
        await a.FullAsync();
        await b.FullAsync();

        a.Write("shared.txt", "from a");
        await a.FullAsync();
        b.Write("shared.txt", "from b");
        File.SetLastWriteTimeUtc(b.At("shared.txt"), DateTime.UtcNow.AddHours(-1));
        var result = await b.FullAsync();
        Assert.Equal(ConflictCause.ContentsDiffer, result.Causes!["shared.txt"]);

        var engine = b.Engine();
        var choice = Assert.Single(result.Choices);
        var (newer, _, cloudTime) = await engine.NewerSideAsync(choice, Cancel);
        Assert.NotNull(cloudTime);
        Assert.Equal(ConflictChoice.KeepRemote, newer);
        await engine.ResolveConflictAsync(choice, ConflictChoice.KeepRemote, Cancel);
        Assert.Equal("from a", File.ReadAllText(b.At("shared.txt")));
        Assert.Contains("from b", b.Recovered());
        Assert.Empty((await b.FullAsync()).Conflicts);
    }

    [Fact]
    public async Task RecreatedBuildFolderKeepsSyncing()
    {
        Assert.SkipUnless(Configured, "Set CLOUDREVE_IT_SITE, CLOUDREVE_IT_EMAIL and CLOUDREVE_IT_PASSWORD to run against a real server.");
        var a = new Device(this, "a");
        a.Write("build/out/same.txt", "same");
        a.Write("build/out/stale.txt", "stale");
        await a.FullAsync();

        Directory.Delete(a.At("build"), true);
        a.Write("build/out/same.txt", "same");
        a.Write("build/out/fresh.txt", "fresh");
        var result = await a.FullAsync();
        Assert.Empty(result.Conflicts);
        Assert.Equal((1, 1), (result.Uploaded, result.Removed));
        var listing = await client.ListAsync(root + "/build/out", Cancel);
        Assert.Equal(["fresh.txt", "same.txt"], listing.Select(entry => entry.Name).Order());
    }

    [Fact]
    public async Task ServerSearchAndTrashMatchWhatCatchUpRelyOn()
    {
        Assert.SkipUnless(Configured, "Set CLOUDREVE_IT_SITE, CLOUDREVE_IT_EMAIL and CLOUDREVE_IT_PASSWORD to run against a real server.");
        var a = new Device(this, "a");
        var since = (await client.ServerTimeAsync(Cancel)).AddMinutes(-2).ToUnixTimeSeconds();
        a.Write("deep/sub/file.txt", "deep");
        await a.FullAsync();

        // The search reaches nested items.
        var changed = await client.ChangedSinceAsync(root!, since, Cancel);
        Assert.Contains(changed, entry => CatchUpPlanner.RelativeKey(root!, entry.Uri) == "deep/sub/file.txt");
        var id = changed.Single(entry => entry.Name == "file.txt").Id;

        // A trashed item keeps its identity and remembers where it lived.
        File.Delete(a.At("deep/sub/file.txt"));
        await a.FullAsync();
        var trashed = await client.TrashedSinceAsync(since, Cancel);
        var item = Assert.Single(trashed, entry => CatchUpPlanner.RelativeKey(root!, entry.RestoreUri) == "deep/sub/file.txt");
        Assert.Equal(id, item.Id);
    }
}
