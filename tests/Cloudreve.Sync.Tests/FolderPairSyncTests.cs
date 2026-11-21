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
        Assert.Empty(store.ReadBaseline(pair));
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
        internal readonly HashSet<string> Folders = [Root];
        internal int Requests, Transfers;
        internal bool Incomplete, Truncate;
        internal string RootId = "original-folder";
        private int sequence;
        private string? uploading;

        internal void Put(string uri, string text)
        {
            Files[uri] = (text, (++sequence).ToString());
        }

        private object Entry(string uri)
        {
            bool folder = Folders.Contains(uri);
            return new { id = uri == Root ? RootId : uri, name = Uri.UnescapeDataString(uri[(uri.LastIndexOf('/') + 1)..]), path = uri, type = folder ? 1 : 0,
                size = folder ? 0 : Encoding.UTF8.GetByteCount(Files[uri].Text), primary_entity = folder ? null : Files[uri].Version };
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
