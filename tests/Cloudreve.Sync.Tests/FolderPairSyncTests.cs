using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Web;
using Cloudreve.Desktop;
using Xunit;

namespace Cloudreve.Sync.Tests;

public sealed class FolderPairSyncTests : IDisposable
{
    private const string Root = "cloudreve://my/work";
    private readonly DirectoryInfo temp = Directory.CreateTempSubdirectory("cloudreve-pair-");
    private readonly Site site = new();
    private readonly HttpClient http;

    private readonly DriveSession session = new("https://site.example", "drive", "Work", Root, "account", "access", "refresh",
        DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow.AddHours(2));

    private readonly FolderPairStore store;
    private readonly FolderPair pair;

    public FolderPairSyncTests()
    {
        http = new HttpClient(site);
        store = new FolderPairStore(Path.Combine(temp.FullName, "settings"), session);
        pair = store.Create(temp.CreateSubdirectory("local").FullName, Root, site.RootId);
    }

    private Task<PairSyncResult> RunAsync()
    {
        var restored = new FolderPairStore(Path.Combine(temp.FullName, "settings"), session);
        return new FolderPairSync(restored, restored.Load()!, new DriveClient(http, http, session, _ => { })).RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TransfersNestedFilesBothWaysAndResumesFromDisk()
    {
        Directory.CreateDirectory(Path.Combine(pair.Local, "notes"));
        string local = Path.Combine(pair.Local, "notes", "a #.txt");
        string uri = Root + "/notes/a%20%23.txt";
        File.WriteAllText(local, "one");
        site.Folders.Add(Root + "/remote");
        site.Put(Root + "/remote/b", "two");
        var first = await RunAsync();
        Assert.Equal(1, first.Uploaded);
        Assert.Equal(1, first.Downloaded);
        Assert.Equal("one", site.Files[uri].Text);
        Assert.Equal("two", File.ReadAllText(Path.Combine(pair.Local, "remote/b")));
        int transfers = site.Transfers;
        var second = await RunAsync();
        Assert.Empty(second.Conflicts);
        Assert.Equal(transfers, site.Transfers);

        site.Put(Root + "/remote/b", "xyz");
        File.WriteAllText(local, "abc");
        var changes = await RunAsync();
        Assert.Equal(1, changes.Uploaded);
        Assert.Equal(1, changes.Downloaded);
        Assert.Equal("abc", site.Files[uri].Text);
        Assert.Equal("xyz", File.ReadAllText(Path.Combine(pair.Local, "remote/b")));

        File.Delete(local);
        site.Files.Remove(Root + "/remote/b");
        Assert.Equal(2, (await RunAsync()).Removed);
        Assert.False(site.Files.ContainsKey(uri));
        Assert.False(File.Exists(Path.Combine(pair.Local, "remote/b")));
        Assert.Contains("xyz", Directory.GetFiles(Path.Combine(pair.Local, ".cloudreve-recovery"), "b", SearchOption.AllDirectories).Select(File.ReadAllText));
        Assert.All(store.ReadBaseline(pair).Values, entry => Assert.True(entry.IsDirectory));
    }

    [Theory]
    [InlineData(true, "new")]
    [InlineData(false, "new")]
    [InlineData(true, "sub/old")]
    [InlineData(false, "sub/old")]
    public async Task MovesOrdinaryFileWithoutTransferringOrChangingItsVersion(bool fromLocal, string target)
    {
        site.Folders.Add(Root + "/sub");
        site.Put(Root + "/old", "first");
        site.Put(Root + "/old", "second");
        await RunAsync();
        var original = store.ReadBaseline(pair)["old"];
        Assert.NotNull(original.LocalId);
        Assert.NotNull(original.RemoteId);
        int transfers = site.Transfers;
        if (fromLocal)
        {
            File.Move(Path.Combine(pair.Local, "old"), Path.Combine(pair.Local, target));
        }
        else
        {
            site.Move(Root + "/old", Root + "/" + target);
        }

        Assert.Empty((await RunAsync()).Conflicts);
        Assert.Empty((await RunAsync()).Conflicts);
        Assert.Equal(original, store.ReadBaseline(pair)[target]);
        Assert.False(site.Files.ContainsKey(Root + "/old"));
        Assert.False(File.Exists(Path.Combine(pair.Local, "old")));
        Assert.Equal("second", File.ReadAllText(Path.Combine(pair.Local, target)));
        Assert.Equal(transfers, site.Transfers);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OccupiedMoveTargetPreservesAllCopies(bool ambiguousIdentity)
    {
        site.Put(Root + "/old", "original");
        await RunAsync();
        File.Move(Path.Combine(pair.Local, "old"), Path.Combine(pair.Local, "new"));
        site.Put(Root + "/new", "other");
        if (ambiguousIdentity)
        {
            site.Ids[Root + "/new"] = site.Ids[Root + "/old"];
        }

        int transfers = site.Transfers;
        Assert.Contains("old", (await RunAsync()).Conflicts);
        Assert.Contains("new", (await RunAsync()).Conflicts);
        Assert.Equal("original", site.Files[Root + "/old"].Text);
        Assert.Equal("other", site.Files[Root + "/new"].Text);
        Assert.Equal(transfers, site.Transfers);
    }

    [Fact]
    public async Task LegacyBaselineLearnsIdsWithoutDownloading()
    {
        site.Put(Root + "/doc", "original");
        await RunAsync();
        var entries = store.ReadBaseline(pair);
        entries["doc"] = entries["doc"] with { LocalId = null, RemoteId = null };
        store.SaveBaseline(pair, entries);
        int transfers = site.Transfers;
        await RunAsync();
        Assert.NotNull(store.ReadBaseline(pair)["doc"].LocalId);
        Assert.Equal(transfers, site.Transfers);
    }

    [Fact]
    public async Task SyncsNestedEmptyDirectoriesButNeverTrashesRemoteDirectoryRecursively()
    {
        Directory.CreateDirectory(Path.Combine(pair.Local, "one/two"));
        site.Folders.Add(Root + "/remote");
        Assert.Empty((await RunAsync()).Conflicts);
        Assert.Contains(Root + "/one/two", site.Folders);
        Assert.True(Directory.Exists(Path.Combine(pair.Local, "remote")));
        site.Folders.Remove(Root + "/remote");
        Directory.Delete(Path.Combine(pair.Local, "one/two"));
        Assert.Equal("one/two", Assert.Single((await RunAsync()).Conflicts));
        Assert.False(Directory.Exists(Path.Combine(pair.Local, "remote")));
        Assert.Contains(Root + "/one/two", site.Folders);
    }

    [Fact]
    public async Task RemoteDirectoryDeletionPreservesNewAndIgnoredChildren()
    {
        site.Folders.Add(Root + "/remote");
        await RunAsync();
        store.SaveRules(pair, "*.tmp");
        site.Folders.Remove(Root + "/remote");
        File.WriteAllText(Path.Combine(pair.Local, "remote/ignored.tmp"), "one");
        Assert.Contains("remote", (await RunAsync()).Conflicts);
        File.WriteAllText(Path.Combine(pair.Local, "remote/new"), "two");
        Assert.Contains("remote", (await RunAsync()).Conflicts);
        Assert.DoesNotContain(Root + "/remote", site.Folders);
        Assert.Equal("one", File.ReadAllText(Path.Combine(pair.Local, "remote/ignored.tmp")));
        Assert.Equal("two", File.ReadAllText(Path.Combine(pair.Local, "remote/new")));
    }

    [Fact]
    public async Task IgnoredPathsNeverPropagateDeletionAndMergeAgainWhenIncluded()
    {
        string uri = Root + "/saved.tmp";
        string local = Path.Combine(pair.Local, "saved.tmp");
        site.Put(uri, "server");
        await RunAsync();
        store.SaveRules(pair, "*.tmp");
        File.Delete(local);
        site.Put(Root + "/other.tmp", "other");
        int transfers = site.Transfers;
        Assert.Equal(0, (await RunAsync()).Removed);
        Assert.Equal(transfers, site.Transfers);
        Assert.Equal("server", site.Files[uri].Text);
        Assert.False(File.Exists(local));
        store.SaveRules(pair, "");
        Assert.Equal(2, (await RunAsync()).Downloaded);
        Assert.Equal("server", File.ReadAllText(local));
    }

    [Theory]
    [InlineData("*.log\n!important.log", "deep/important.log", false)]
    [InlineData("/build/", "other/build/a", false)]
    [InlineData("/build/", "build/sub/a", true)]
    [InlineData("src/**/temp?.bin", "src/tmp/x/temp1.bin", true)]
    [InlineData("src/**/temp?.bin", "src/temp1.bin", true)]
    [InlineData("build/\n!build/keep", "build/keep", true)]
    public void MatchesDocumentedIgnorePatterns(string text, string path, bool expected)
    {
        Assert.Equal(expected, new PairIgnoreRules(text).Excludes(path));
    }

    [Fact]
    public async Task ReportsConflictsAcrossRestartsWithoutChangingEitherCopy()
    {
        string path = Path.Combine(pair.Local, "doc");
        site.Put(Root + "/doc", "one");
        await RunAsync();
        var baseline = store.ReadBaseline(pair)["doc"];
        File.WriteAllText(path, "two");
        site.Put(Root + "/doc", "new");
        Assert.Equal("doc", Assert.Single((await RunAsync()).Conflicts));
        Assert.Equal("doc", Assert.Single((await RunAsync()).Conflicts));
        Assert.Equal("two", File.ReadAllText(path));
        Assert.Equal("new", site.Files[Root + "/doc"].Text);
        Assert.Equal(baseline, store.ReadBaseline(pair)["doc"]);
        File.Delete(path);
        Assert.Equal("doc", Assert.Single((await RunAsync()).Conflicts));
        Assert.Equal("new", site.Files[Root + "/doc"].Text);
    }

    [Theory]
    [InlineData("KeepLocal", "local")]
    [InlineData("KeepRemote", "cloud")]
    [InlineData("KeepBoth", "cloud")]
    public async Task ResolvesOnlyTheSelectedFileAndPreservesTheChosenVersion(string choice, string expected)
    {
        string path = Path.Combine(pair.Local, "doc");
        site.Put(Root + "/doc", "first");
        await RunAsync();
        File.WriteAllText(path, "local");
        site.Put(Root + "/doc", "cloud");
        var conflict = Assert.Single((await RunAsync()).Choices);
        var engine = new FolderPairSync(store, pair, new DriveClient(http, http, session, _ => { }));
        await engine.ResolveConflictAsync(conflict, Enum.Parse<ConflictChoice>(choice), TestContext.Current.CancellationToken);
        Assert.Empty((await RunAsync()).Conflicts);
        Assert.Equal(expected, File.ReadAllText(path));
        Assert.Equal(expected, site.Files[Root + "/doc"].Text);
        if (choice == "KeepBoth")
        {
            Assert.Equal("local", File.ReadAllText(Assert.Single(Directory.GetFiles(pair.Local, "local-copy-*"))));
            Assert.Contains(site.Files.Values, entry => entry.Text == "local");
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RefusesStaleConflictDecisions(bool changeCloud)
    {
        string path = Path.Combine(pair.Local, "doc");
        site.Put(Root + "/doc", "cloud");
        File.WriteAllText(path, "local");
        var conflict = Assert.Single((await RunAsync()).Choices);
        if (changeCloud)
        {
            site.Put(Root + "/doc", "newer");
        }
        else
        {
            File.WriteAllText(path, "newer");
        }

        var engine = new FolderPairSync(store, pair, new DriveClient(http, http, session, _ => { }));
        await Assert.ThrowsAsync<IOException>(() => engine.ResolveConflictAsync(conflict, ConflictChoice.KeepRemote, TestContext.Current.CancellationToken));
        Assert.Equal(changeCloud ? "local" : "newer", File.ReadAllText(path));
        Assert.Equal(changeCloud ? "newer" : "cloud", site.Files[Root + "/doc"].Text);
    }

    [Fact]
    public async Task IncompleteListingAndMissingRootNeverPropagateDeletion()
    {
        site.Put(Root + "/saved", "saved");
        await RunAsync();
        File.Delete(Path.Combine(pair.Local, "saved"));
        site.Incomplete = true;
        await Assert.ThrowsAsync<InvalidDataException>(() => RunAsync());
        Assert.Single(site.Files);
        site.Incomplete = false;
        Directory.Move(pair.Local, pair.Local + "-unmounted");
        Directory.CreateDirectory(pair.Local);
        int requests = site.Requests;
        await Assert.ThrowsAsync<IOException>(() => RunAsync());
        Assert.Equal(requests, site.Requests);
        Assert.Single(site.Files);
        Assert.Single(store.ReadBaseline(pair));
    }

    [Fact]
    public async Task ReplacingTheRemoteFolderWithAnEmptyNamesakeDoesNotRemoveLocalFiles()
    {
        site.Put(Root + "/saved", "keep");
        await RunAsync();
        site.Files.Clear();
        site.RootId = "another-folder";
        await Assert.ThrowsAsync<IOException>(() => RunAsync());
        Assert.Equal("keep", File.ReadAllText(Path.Combine(pair.Local, "saved")));
        Assert.Single(store.ReadBaseline(pair));
    }

    [Fact]
    public async Task RejectedDownloadPreservesTheLocalCopyAndOldBaseline()
    {
        site.Put(Root + "/saved", "first");
        await RunAsync();
        var baseline = store.ReadBaseline(pair)["saved"];
        site.Put(Root + "/saved", "second");
        site.Truncate = true;
        await Assert.ThrowsAsync<InvalidDataException>(() => RunAsync());
        Assert.Equal("first", File.ReadAllText(Path.Combine(pair.Local, "saved")));
        Assert.Equal(baseline, store.ReadBaseline(pair)["saved"]);
        Assert.Empty(Directory.GetFiles(pair.Local, "*.part"));
    }

    [Fact]
    public async Task DoesNotFollowLocalLinksOrGuessBetweenCaseVariants()
    {
        string outside = temp.CreateSubdirectory("outside").FullName;
        File.WriteAllText(Path.Combine(outside, "secret"), "unpaired");
        Directory.CreateSymbolicLink(Path.Combine(pair.Local, "linked"), outside);
        site.Folders.Add(Root + "/linked");
        site.Put(Root + "/linked/cloud", "remote");
        site.Put(Root + "/Doc", "first");
        site.Put(Root + "/doc", "second");
        var result = await RunAsync();
        Assert.Equal(2, result.Conflicts.Count);
        Assert.False(File.Exists(Path.Combine(outside, "cloud")));
        Assert.False(File.Exists(Path.Combine(pair.Local, "Doc")));
        Assert.Equal(0, site.Transfers);
    }

    public void Dispose()
    {
        http.Dispose();
        temp.Delete(true);
    }

    private sealed class Site : HttpMessageHandler
    {
        internal readonly Dictionary<string, (string Text, string Version)> Files = [];
        internal readonly Dictionary<string, string> Ids = [];
        internal readonly HashSet<string> Folders = [Root];
        internal int Requests, Transfers;
        internal bool Incomplete, Truncate;
        internal string RootId = "original-folder";
        private int sequence;
        private string? uploading;

        internal void Put(string uri, string text)
        {
            if (!Files.ContainsKey(uri))
            {
                Ids[uri] = Guid.NewGuid().ToString("N");
            }

            Files[uri] = (text, (++sequence).ToString());
        }

        internal void Move(string source, string target)
        {
            Files.Add(target, Files[source]);
            Files.Remove(source);
            Ids[target] = Ids[source];
            Ids.Remove(source);
        }

        private object Entry(string uri)
        {
            bool folder = Folders.Contains(uri);
            return new
            {
                id = uri == Root ? RootId : folder ? uri : Ids[uri], name = Uri.UnescapeDataString(uri[(uri.LastIndexOf('/') + 1)..]), path = uri, type = folder ? 1 : 0,
                size = folder ? 0 : Encoding.UTF8.GetByteCount(Files[uri].Text), primary_entity = folder ? null : Files[uri].Version
            };
        }

        private static HttpResponseMessage Ok(object? data)
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { code = 0, data }) };
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var uri = request.RequestUri!;
            var query = HttpUtility.ParseQueryString(uri.Query);
            if (uri.Host == "storage.example")
            {
                Assert.Null(request.Headers.Authorization);
                Transfers++;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Truncate ? "" : Files[query["uri"]!].Text) };
            }

            Assert.NotNull(request.Headers.Authorization);
            if (request.Method == HttpMethod.Get && uri.AbsolutePath == "/api/v4/file")
            {
                string parent = query["uri"]!;
                Assert.Contains(parent, Folders);
                var children = Files.Keys.Concat(Folders).Where(key => key[..key.LastIndexOf('/')] == parent).Select(Entry).ToArray();
                object pagination = Incomplete ? new { total_items = children.Length + 1, page = 0, page_size = 100 } : new { };
                return Ok(new { files = children, pagination });
            }

            if (uri.AbsolutePath == "/api/v4/file/info")
            {
                return Ok(Entry(query["uri"]!));
            }

            if (uri.AbsolutePath == "/api/v4/file/upload/id/0")
            {
                Put(uploading!, await request.Content!.ReadAsStringAsync(cancellationToken));
                Transfers++;
                return Ok(null);
            }

            var body = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken);
            switch (uri.AbsolutePath)
            {
                case "/api/v4/file/rename":
                    string oldName = body.GetProperty("uri").GetString()!;
                    Move(oldName, DriveClient.ChildUri(oldName[..oldName.LastIndexOf('/')], body.GetProperty("new_name").GetString()!));
                    return Ok(null);
                case "/api/v4/file/move":
                    string oldPath = body.GetProperty("uris")[0].GetString()!;
                    Assert.False(body.GetProperty("copy").GetBoolean());
                    Move(oldPath, body.GetProperty("dst").GetString() + oldPath[oldPath.LastIndexOf('/')..]);
                    return Ok(null);
                case "/api/v4/file/upload":
                    uploading = body.GetProperty("uri").GetString()!;
                    if (Files.TryGetValue(uploading, out var previous))
                    {
                        Assert.Equal(previous.Version, body.GetProperty("previous").GetString());
                        Assert.Equal("version", body.GetProperty("entity_type").GetString());
                    }

                    return Ok(new { session_id = "id", chunk_size = 0 });
                case "/api/v4/file/url":
                    string source = body.GetProperty("uris")[0].GetString()!;
                    Assert.Equal(Files[source].Version, body.GetProperty("entity").GetString());
                    return Ok(new { urls = new[] { new { url = "https://storage.example/?uri=" + Uri.EscapeDataString(source) } } });
                case "/api/v4/file/create":
                    Assert.Equal("folder", body.GetProperty("type").GetString());
                    Assert.True(Folders.Add(body.GetProperty("uri").GetString()!));
                    return Ok(null);
                case "/api/v4/file":
                    Assert.Equal(HttpMethod.Delete, request.Method);
                    Assert.False(body.GetProperty("skip_soft_delete").GetBoolean());
                    Assert.True(Files.Remove(body.GetProperty("uris")[0].GetString()!));
                    return Ok(null);
                default:
                    throw new InvalidOperationException(uri.AbsolutePath);
            }
        }
    }
}