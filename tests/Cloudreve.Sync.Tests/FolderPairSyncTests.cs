using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
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

    private Task<PairSyncResult> RunAsync(SyncActivity? activity = null)
    {
        var restored = new FolderPairStore(Path.Combine(temp.FullName, "settings"), session);
        return new FolderPairSync(restored, restored.Load()!, new DriveClient(http, http, session, _ => { }), activity).RunAsync(TestContext.Current.CancellationToken);
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
    public async Task OverlapsIndependentFilesAndCheckpointsEachVerifiedCopy()
    {
        site.Put(Root + "/a", "cloud");
        foreach (string key in new[] { "b", "c", "d" })
        {
            File.WriteAllText(Path.Combine(pair.Local, key), key);
        }

        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int started = 0;
        site.BeforeStorageAsync = async (_, token) =>
        {
            if (Interlocked.Increment(ref started) == 2)
            {
                both.TrySetResult();
            }

            await release.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
        };
        var activity = new SyncActivity();
        var sync = RunAsync(activity);
        try
        {
            await both.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(["a", "b"], activity.ChangesSince(-1)!.Active.Select(file => file.Entry.Key).Order());
        }
        finally
        {
            release.TrySetResult();
            await sync;
        }

        var result = await sync;
        Assert.Equal(3, result.Uploaded);
        Assert.Equal(1, result.Downloaded);
        Assert.Empty(result.Conflicts);
        Assert.Equal("cloud", File.ReadAllText(Path.Combine(pair.Local, "a")));
        Assert.Equal(["b", "c", "d"], new[] { "b", "c", "d" }.Select(key => site.Files[Root + "/" + key].Text));
        Assert.Equal(4, store.ReadBaseline(pair).Count);
        Assert.Empty(activity.ChangesSince(-1)!.Active);
        Assert.Equal(4, activity.ChangesSince(-1)!.Recent.Length);
    }

    [Fact]
    public async Task FailedBatchPersistsSuccessfulFilesAndRetriesOnlyUnfinishedOnes()
    {
        foreach (string key in new[] { "a", "b", "c" })
        {
            site.Put(Root + "/" + key, key);
        }

        var thirdStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        site.BeforeStorageAsync = async (uri, token) =>
        {
            if (uri == Root + "/b")
            {
                await thirdStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
                throw new HttpRequestException("busy", null, HttpStatusCode.ServiceUnavailable);
            }

            if (uri == Root + "/c")
            {
                thirdStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
        };

        await Assert.ThrowsAsync<HttpRequestException>(() => RunAsync());
        Assert.Equal("a", Assert.Single(store.ReadBaseline(pair)).Key);
        Assert.Empty(Directory.GetFiles(pair.Local, "*.part"));
        site.BeforeStorageAsync = null;
        Assert.Equal(2, (await RunAsync()).Downloaded);
        Assert.Equal(3, store.ReadBaseline(pair).Count);
    }

    [Fact]
    public async Task RestartScansIndependentLocalFilesConcurrently()
    {
        site.Put(Root + "/first", "one");
        site.Put(Root + "/second", "two");
        await RunAsync();
        var cancel = TestContext.Current.CancellationToken;
        using (var readers = new Barrier(2))
        {
            var restored = new GatedBaseline(store.ReadBaseline(pair), readers, cancel);
            var inventory = await PairInventory.ReadLocalAsync(pair, new PairIgnoreRules(""), restored, new AdaptiveWorkQueue(), cancel);
            Assert.Equal(1, readers.CurrentPhaseNumber);
            Assert.Equal(restored["first"].LocalHash, inventory.Nodes["first"].Hash);
            Assert.Equal(restored["second"].LocalHash, inventory.Nodes["second"].Hash);
        }
    }

    private sealed class GatedBaseline(Dictionary<string, FileBaseline> files, Barrier readers, CancellationToken cancel)
        : ReadOnlyDictionary<string, FileBaseline>(files), IReadOnlyDictionary<string, FileBaseline>
    {
        bool IReadOnlyDictionary<string, FileBaseline>.TryGetValue(string key, [MaybeNullWhen(false)] out FileBaseline value)
        {
            if (!readers.SignalAndWait(TimeSpan.FromSeconds(10), cancel))
            {
                throw new TimeoutException("No second local scan worker entered the baseline lookup.");
            }

            return TryGetValue(key, out value);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialRestartScanDrainsSiblingListingsWithoutApplyingDeletions(bool cancelScan)
    {
        const string slow = Root + "/slow";
        const string broken = Root + "/broken";
        site.Folders.UnionWith([slow, broken]);
        site.Put(slow + "/keep", "in the slow subtree");
        site.Put(Root + "/deleted-locally", "still on cloud");
        site.Put(Root + "/deleted-remotely", "still here");
        await RunAsync();
        File.Delete(Path.Combine(pair.Local, "deleted-locally"));
        site.Files.Remove(Root + "/deleted-remotely");
        var before = store.ReadBaseline(pair);
        var waiting = TransferQueue.Open(store.TransferDirectory(pair)).Reconcile([new FileTransfer("pending", SyncAction.Upload, null, null, null)]);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool drained = false;
        using (var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken))
        {
            site.BeforeMetadataAsync = async (uri, token) =>
            {
                if (uri == slow)
                {
                    entered.SetResult();
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(10), token);
                        throw new TimeoutException("The sibling did not cancel this listing.");
                    }
                    finally
                    {
                        drained = true;
                    }
                }
                else if (uri == broken)
                {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
                    if (cancelScan)
                    {
                        await stop.CancelAsync();
                        token.ThrowIfCancellationRequested();
                    }

                    site.Incomplete = true;
                }
            };

            var restored = new FolderPairSync(store, store.Load()!, new DriveClient(http, http, session, _ => { }));
            var scan = restored.RunAsync(stop.Token);
            if (cancelScan)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan);
            }
            else
            {
                await Assert.ThrowsAsync<InvalidDataException>(() => scan);
            }
        }

        Assert.True(drained);
        Assert.Empty(site.TrashRequests);
        Assert.Equal("still on cloud", site.Files[Root + "/deleted-locally"].Text);
        Assert.Equal("still here", File.ReadAllText(Path.Combine(pair.Local, "deleted-remotely")));
        Assert.Equal("in the slow subtree", File.ReadAllText(Path.Combine(pair.Local, "slow/keep")));
        Assert.Equal(before, store.ReadBaseline(pair));
        Assert.Equal(waiting, TransferQueue.Open(store.TransferDirectory(pair)).Entries);

        site.BeforeMetadataAsync = null;
        site.Incomplete = false;
        Assert.Equal(2, (await RunAsync()).Removed);
    }

    [Fact]
    public async Task UnreadableLocalFileAbortsTheScanBeforePropagatingSiblingDeletion()
    {
        site.Put(Root + "/locked", "original");
        site.Put(Root + "/missing", "keep");
        await RunAsync();
        var previous = store.ReadBaseline(pair);
        string changed = Path.Combine(pair.Local, "locked");
        File.WriteAllText(changed, "changed");
        File.Delete(Path.Combine(pair.Local, "missing"));
        using (var locked = new FileStream(changed, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAsync<IOException>(() => RunAsync());
        }

        Assert.Equal(previous, store.ReadBaseline(pair));
        Assert.Empty(site.TrashRequests);
        Assert.Equal("keep", site.Files[Root + "/missing"].Text);
    }

    [Fact]
    public async Task ResolvesMissingVersionsConcurrentlyInAFlatDirectory()
    {
        site.Put(Root + "/one", "1");
        site.Put(Root + "/two", "2");
        site.OmitListedVersions = true;
        int requests = 0;
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        site.BeforeMetadataAsync = async (uri, token) =>
        {
            if (uri != Root)
            {
                if (Interlocked.Increment(ref requests) == 2)
                {
                    both.SetResult();
                }

                await both.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            }
        };

        var inventory = await PairInventory.ReadRemoteAsync(Root, "", new DriveClient(http, http, session, _ => { }),
            new PairIgnoreRules(""), new AdaptiveWorkQueue(), TestContext.Current.CancellationToken);
        Assert.Equal(2, requests);
        Assert.All(inventory.Nodes.Values, node => Assert.Equal(site.Files[node.Remote!.Uri].Version, node.Remote.Version));
    }

    [Fact]
    public async Task ReusesVerifiedDigestAfterRestartWithoutOpeningTheFile()
    {
        site.Put(Root + "/doc", "original");
        await RunAsync();
        string path = Path.Combine(pair.Local, "doc");
        var baseline = store.ReadBaseline(pair)["doc"];
        Assert.SkipWhen(MacFile.GetContentVersion(path) == null, "Volume does not provide a content generation.");
        Assert.NotEqual(0u, baseline.LocalGeneration);
        Assert.Equal(Convert.ToHexString(SHA256.HashData("original"u8)), baseline.LocalHash);
        int transfers = site.Transfers;

        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            // Prove an ordinary content read would fail while the inventory scan can still complete.
            await Assert.ThrowsAsync<IOException>(() => PairInventory.HashAsync(path, TestContext.Current.CancellationToken));
            Assert.Empty((await RunAsync()).Conflicts);
        }

        Assert.Equal(transfers, site.Transfers);
        Assert.Equal(baseline, store.ReadBaseline(pair)["doc"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DetectsSameSizeChangesEvenWithRestoredModificationTime(bool replaceFile)
    {
        site.Put(Root + "/doc", "first");
        await RunAsync();
        string path = Path.Combine(pair.Local, "doc");
        var modified = File.GetLastWriteTimeUtc(path);
        if (replaceFile)
        {
            string replacement = Path.Combine(temp.FullName, "replacement");
            File.WriteAllText(replacement, "other");
            File.Move(replacement, path, true);
        }
        else
        {
            File.WriteAllText(path, "other");
        }

        File.SetLastWriteTimeUtc(path, modified);
        Assert.Equal(1, (await RunAsync()).Uploaded);
        Assert.Equal("other", site.Files[Root + "/doc"].Text);
    }

    [Fact]
    public async Task RechecksCachedLocalContentBeforeInstallingCloudChange()
    {
        site.Put(Root + "/doc", "first");
        await RunAsync();
        string path = Path.Combine(pair.Local, "doc");
        var baseline = store.ReadBaseline(pair)["doc"];
        site.Put(Root + "/doc", "cloud");
        site.BeforeList = _ => File.WriteAllText(path, "local");

        await Assert.ThrowsAsync<IOException>(() => RunAsync());
        Assert.Equal("local", File.ReadAllText(path));
        Assert.Equal("cloud", site.Files[Root + "/doc"].Text);
        Assert.Equal(baseline, store.ReadBaseline(pair)["doc"]);
    }

    [Fact]
    public async Task RejectsIncorrectUploadReadbackWithoutCheckpointing()
    {
        string path = Path.Combine(pair.Local, "doc");
        File.WriteAllText(path, "local");
        site.ReadbackOverride = "wrong";

        await Assert.ThrowsAsync<IOException>(() => RunAsync());
        Assert.Empty(store.ReadBaseline(pair));
        Assert.Equal("local", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(pair.Local, "*.part"));
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
    public async Task SyncsNestedEmptyDirectoriesAndTheirDeletion()
    {
        Directory.CreateDirectory(Path.Combine(pair.Local, "one/two"));
        site.Folders.Add(Root + "/remote");
        Assert.Empty((await RunAsync()).Conflicts);
        Assert.Contains(Root + "/one/two", site.Folders);
        Assert.True(Directory.Exists(Path.Combine(pair.Local, "remote")));
        site.Folders.Remove(Root + "/remote");
        Directory.Delete(Path.Combine(pair.Local, "one/two"));
        Assert.Empty((await RunAsync()).Conflicts);
        Assert.False(Directory.Exists(Path.Combine(pair.Local, "remote")));
        Assert.DoesNotContain(Root + "/one/two", site.Folders);
        Assert.Equal(Root + "/one/two", Assert.Single(site.TrashRequests));
    }

    [Fact]
    public async Task TrashesOnlyTheMissingTreeAndForgetsItsDescendantsAcrossRestart()
    {
        site.Folders.UnionWith([Root + "/a", Root + "/a/nested", Root + "/ab"]);
        site.Put(Root + "/a/nested/note", "nested");
        site.Put(Root + "/a/other", "other");
        site.Put(Root + "/ab/note", "keep");
        await RunAsync();
        Directory.Delete(Path.Combine(pair.Local, "a"), true);

        var result = await RunAsync();
        Assert.Empty(result.Conflicts);
        Assert.Equal(1, result.Removed);
        Assert.Equal(0, result.Downloaded);
        Assert.DoesNotContain(Root + "/a/nested", site.Folders);
        Assert.False(Directory.Exists(Path.Combine(pair.Local, "a")));
        Assert.Equal("keep", site.Files[Assert.Single(site.Files.Keys)].Text);
        Assert.Equal(["ab", "ab/note"], store.ReadBaseline(pair).Keys.Order());
        Assert.Equal(0, (await RunAsync()).Removed);
        Assert.Equal(Root + "/a", Assert.Single(site.TrashRequests));
    }

    [Theory]
    [InlineData("new")]
    [InlineData("modified")]
    [InlineData("replaced-folder")]
    [InlineData("ignored")]
    [InlineData("reserved")]
    public async Task ProtectsTheEntireTreeWhenACloudDescendantIsNotSafeToTrash(string change)
    {
        site.Folders.UnionWith([Root + "/tree", Root + "/tree/sub"]);
        site.Put(Root + "/tree/keep", "keep");
        site.Put(Root + "/tree/sub/doc", "old");
        store.SaveRules(pair, "*.tmp");
        await RunAsync();
        Directory.Delete(Path.Combine(pair.Local, "tree"), true);
        switch (change)
        {
            case "new": site.Put(Root + "/tree/sub/new", "new"); break;
            case "modified": site.Put(Root + "/tree/sub/doc", "new"); break;
            case "replaced-folder": site.Ids[Root + "/tree/sub"] = "replacement"; break;
            case "ignored": site.Put(Root + "/tree/sub/save.tmp", "ignored"); break;
            case "reserved": site.Put(Root + "/tree/sub/.DS_Store", "reserved"); break;
        }

        Assert.Equal("tree", Assert.Single((await RunAsync()).Conflicts));
        Assert.Empty(site.TrashRequests);
        Assert.Equal("keep", site.Files[Root + "/tree/keep"].Text);
        Assert.False(Directory.Exists(Path.Combine(pair.Local, "tree")));
        Assert.Contains("tree/sub/doc", store.ReadBaseline(pair).Keys);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DoesNotTrashRenamedDirectoriesOrFilesMovedOutOfAMissingDirectory(bool moveWholeTree)
    {
        site.Folders.Add(Root + "/tree");
        site.Put(Root + "/tree/doc", "keep");
        await RunAsync();
        string tree = Path.Combine(pair.Local, "tree");
        if (moveWholeTree)
        {
            Directory.Move(tree, Path.Combine(pair.Local, "renamed"));
        }
        else
        {
            File.Move(Path.Combine(tree, "doc"), Path.Combine(pair.Local, "renamed"));
            Directory.Delete(tree);
        }

        Assert.Contains("tree", (await RunAsync()).Conflicts);
        Assert.Empty(site.TrashRequests);
        Assert.DoesNotContain(Root + "/renamed", site.Folders);
        Assert.Equal("keep", site.Files[Root + "/tree/doc"].Text);
    }

    [Fact]
    public async Task RetriesRejectedDirectoryTrashWithoutForgettingTheTree()
    {
        site.Folders.UnionWith([Root + "/first", Root + "/tree"]);
        site.Put(Root + "/tree/doc", "keep");
        await RunAsync();
        Directory.Delete(Path.Combine(pair.Local, "first"));
        Directory.Delete(Path.Combine(pair.Local, "tree"), true);
        site.RejectTrash = Root + "/tree";
        await Assert.ThrowsAsync<HttpRequestException>(() => RunAsync());
        Assert.DoesNotContain("first", store.ReadBaseline(pair).Keys);
        Assert.Contains("tree/doc", store.ReadBaseline(pair).Keys);
        site.RejectTrash = null;
        Assert.Equal(1, (await RunAsync()).Removed);
        Assert.Empty(store.ReadBaseline(pair));
        Assert.Equal([Root + "/first", Root + "/tree", Root + "/tree"], site.TrashRequests);
    }

    [Theory]
    [InlineData("local-reappeared")]
    [InlineData("local-root-unavailable")]
    [InlineData("cloud-root-replaced")]
    [InlineData("target-replaced")]
    [InlineData("late-cloud-edit")]
    [InlineData("incomplete-list")]
    public async Task RechecksBothSidesBetweenTheScanAndRecursiveTrash(string change)
    {
        string target = Root + "/tree";
        site.Folders.Add(target);
        site.Put(target + "/doc", "original");
        await RunAsync();
        string local = Path.Combine(pair.Local, "tree");
        Directory.Delete(local, true);
        int targetListings = 0;
        site.BeforeList = parent =>
        {
            if (parent != target || ++targetListings != 2)
            {
                return;
            }

            switch (change)
            {
                case "local-reappeared": Directory.CreateDirectory(local); break;
                case "local-root-unavailable": Directory.Move(pair.Local, pair.Local + "-offline"); break;
                case "cloud-root-replaced": site.RootId = "replacement"; break;
                case "target-replaced": site.Ids[target] = "replacement"; break;
                case "late-cloud-edit": site.Put(target + "/doc", "newer"); break;
                case "incomplete-list": site.Incomplete = true; break;
            }
        };

        if (change == "incomplete-list")
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => RunAsync());
        }
        else if (change is "local-root-unavailable" or "cloud-root-replaced")
        {
            await Assert.ThrowsAsync<IOException>(() => RunAsync());
        }
        else
        {
            Assert.Equal("tree", Assert.Single((await RunAsync()).Conflicts));
        }

        Assert.Empty(site.TrashRequests);
        Assert.Contains(target, site.Folders);
        Assert.Contains("tree/doc", store.ReadBaseline(pair).Keys);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoresOnlyUnacknowledgedUploadParts(bool updateExisting)
    {
        const string key = "upload";
        const string content = "abcdefghi";
        if (updateExisting)
        {
            site.Put(Root + "/" + key, "old");
            await RunAsync();
        }

        File.WriteAllText(Path.Combine(pair.Local, key), content);
        site.UploadChunkSize = 3;
        site.InterruptPart = 1;
        string cache = store.TransferDirectory(pair);
        await Assert.ThrowsAsync<HttpRequestException>(() => RunAsync(new SyncActivity(cache)));
        var restarted = new SyncActivity(cache);
        Assert.Equal([new WaitingFile(key, SyncAction.Upload)], restarted.ChangesSince(-1)!.Waiting);
        Assert.Single(restarted.ChangesSince(-1)!.Recent);
        var checkpoint = Assert.Single(Directory.GetFiles(cache, "*.upload"));
        if (OperatingSystem.IsMacOS())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(checkpoint));
        }

        // Simulate a process dying halfway through its next receipt append.
        File.AppendAllText(Assert.Single(Directory.GetFiles(cache, "*.receipts")), "{\"NextPart\":");
        site.InterruptPart = null;
        var result = await RunAsync(restarted);
        Assert.Equal(1, result.Uploaded);
        Assert.Equal(1, site.SessionsCreated);
        Assert.Equal(0, site.SessionsCancelled);
        Assert.Equal([0, 1, 1, 2], site.Parts.Select(part => part.Part));
        Assert.Single(site.Parts.Select(part => part.Id).Distinct());
        Assert.Equal(content, site.Files[Root + "/" + key].Text);
        Assert.Empty(Directory.GetFiles(cache, "*.upload"));
        Assert.Empty(new SyncActivity(cache).ChangesSince(-1)!.Waiting);
        Assert.Equal(2, new SyncActivity(cache).ChangesSince(-1)!.Recent.Length);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task RecoversAnAcknowledgedLocalUploadOnlyWhileItOwnsThePlaceholder(bool updateExisting, bool foreign)
    {
        const string key = "upload";
        const string uri = Root + "/" + key;
        if (updateExisting)
        {
            site.Put(uri, "old");
            await RunAsync();
        }

        var previous = store.ReadBaseline(pair).GetValueOrDefault(key);
        File.WriteAllText(Path.Combine(pair.Local, key), "new content");
        site.KeepUploadPlaceholder = true;
        var failure = await Assert.ThrowsAsync<InvalidDataException>(() => RunAsync());
        Assert.Equal(RemoteVersionFailure.UploadStillPending, failure.Data[nameof(RemoteVersionFailure)]);
        Assert.Equal(previous, store.ReadBaseline(pair).GetValueOrDefault(key));
        Assert.Single(site.Parts);

        site.KeepUploadPlaceholder = false;
        site.ForeignPlaceholder = foreign;
        var result = await RunAsync();
        if (foreign)
        {
            Assert.Contains(key, result.Conflicts);
            Assert.Single(site.Parts);
            Assert.Equal(0, site.SessionsCancelled);
            Assert.Equal(previous, store.ReadBaseline(pair).GetValueOrDefault(key));
            return;
        }

        Assert.Equal(1, result.Uploaded);
        Assert.Equal(1, site.SessionsCancelled);
        Assert.Equal(2, site.SessionsCreated);
        Assert.Equal("new content", site.Files[uri].Text);
        Assert.Equal(site.Files[uri].Version, store.ReadBaseline(pair)[key].RemoteVersion);
        Assert.Empty(TransferQueue.Open(store.TransferDirectory(pair)).Entries);
    }

    [Fact]
    public async Task RecoversVerifiedFileBeforeReplanningEditsMadeWhileStopped()
    {
        site.Put(Root + "/a", "first");
        site.Put(Root + "/z", "waiting");
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        site.BeforeStorageAsync = async (uri, cancel) =>
        {
            if (uri.EndsWith("/z", StringComparison.Ordinal))
            {
                await hold.Task.WaitAsync(cancel);
                throw new HttpRequestException("offline");
            }
        };

        string cache = store.TransferDirectory(pair);
        var run = RunAsync();
        var snapshot = temp.CreateSubdirectory("at-interruption");
        try
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                while (!Directory.EnumerateFiles(cache, "*.verified").Any())
                {
                    await Task.Delay(10, timeout.Token);
                }
            }

            Assert.Empty(store.ReadBaseline(pair));
            string completed = Assert.Single(Directory.GetFiles(cache, "*.verified"));
            File.Copy(completed, Path.Combine(snapshot.FullName, Path.GetFileName(completed)));
            File.Copy(Path.Combine(cache, "pending.json"), Path.Combine(snapshot.FullName, "pending.json"));
        }
        finally
        {
            hold.TrySetResult();
            await Assert.ThrowsAsync<HttpRequestException>(() => run);
        }

        // Restore the on-disk snapshot from before shutdown had a chance to checkpoint the batch.
        store.SaveBaseline(pair, []);
        foreach (var file in snapshot.GetFiles())
        {
            file.CopyTo(Path.Combine(cache, file.Name), true);
        }

        site.BeforeStorageAsync = null;
        File.WriteAllText(Path.Combine(pair.Local, "a"), "edited while stopped");
        var result = await RunAsync();
        Assert.Empty(result.Conflicts);
        Assert.Equal(1, result.Uploaded);
        Assert.Equal(1, result.Downloaded);
        Assert.Equal("edited while stopped", site.Files[Root + "/a"].Text);
        Assert.Empty(Directory.GetFiles(cache, "*.verified"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReplansExpiredSessionOrEditedSource(bool expired)
    {
        string path = Path.Combine(pair.Local, "upload");
        File.WriteAllText(path, "abcdef");
        site.UploadChunkSize = 3;
        site.InterruptPart = 1;
        site.TicketExpiry = expired ? 1 : null;
        await Assert.ThrowsAsync<HttpRequestException>(() => RunAsync());
        site.InterruptPart = null;
        site.TicketExpiry = null;
        if (!expired)
        {
            File.WriteAllText(path, "123456");
            Assert.Equal(0, (await RunAsync()).Uploaded);
        }

        Assert.Equal(1, (await RunAsync()).Uploaded);
        Assert.Equal(2, site.SessionsCreated);
        Assert.Equal(1, site.SessionsCancelled);
        Assert.Equal([0, 1, 0, 1], site.Parts.Select(part => part.Part));
        Assert.Equal(File.ReadAllText(path), site.Files[Root + "/upload"].Text);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public async Task RestoresOnlyVerifiedDownloadPrefixes(bool ranges, bool corruptPrefix, bool newVersion)
    {
        const string uri = Root + "/remote";
        string path = Path.Combine(pair.Local, "remote");
        site.Put(uri, "old");
        await RunAsync();
        site.Put(uri, "abcdefgh");
        site.DownloadLimit = 3;
        await Assert.ThrowsAsync<InvalidDataException>(() => RunAsync());
        Assert.Equal("old", File.ReadAllText(path));
        string cache = store.TransferDirectory(pair);
        string part = Assert.Single(Directory.GetFiles(cache, "*.part"));
        Assert.Equal("abc", File.ReadAllText(part));
        if (corruptPrefix)
        {
            File.WriteAllText(part, "bad");
        }
        else
        {
            File.AppendAllText(part, "uncommitted tail");
        }

        if (newVersion)
        {
            site.Put(uri, "ABCDEFGH");
        }

        site.AcceptRanges = ranges;
        site.DownloadLimit = null;
        Assert.Equal(1, (await RunAsync()).Downloaded);
        Assert.Equal(corruptPrefix || newVersion ? (long?)null : 3L, site.Ranges[^1]);
        Assert.Equal(site.Files[uri].Text, File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(cache, "*.part"));
        Assert.Empty(TransferQueue.Open(cache).Entries);
    }

    [Fact]
    public async Task NeverResumesOrCancelsAnotherClientsPlaceholder()
    {
        string path = Path.Combine(pair.Local, "upload");
        File.WriteAllText(path, "abcdef");
        site.UploadChunkSize = 3;
        site.InterruptPart = 1;
        await Assert.ThrowsAsync<HttpRequestException>(() => RunAsync());
        site.ForeignPlaceholder = true;
        site.InterruptPart = null;
        var result = await RunAsync();
        Assert.Contains("upload", result.Conflicts);
        Assert.Equal([0, 1], site.Parts.Select(part => part.Part));
        Assert.Equal(0, site.SessionsCancelled);
        Assert.Equal("abcdef", File.ReadAllText(path));
    }

    [Fact]
    public async Task RejectsMismatchedRangeBeforeInstallingAnyBytes()
    {
        site.Put(Root + "/remote", "abcdefgh");
        site.DownloadLimit = 3;
        await Assert.ThrowsAsync<InvalidDataException>(() => RunAsync());
        site.AcceptRanges = true;
        site.BadRange = true;
        site.DownloadLimit = null;
        await Assert.ThrowsAsync<InvalidDataException>(() => RunAsync());
        Assert.False(File.Exists(Path.Combine(pair.Local, "remote")));
        Assert.Empty(store.ReadBaseline(pair));
        await RunAsync();
        Assert.Null(site.Ranges[^1]);
        Assert.Equal("abcdefgh", File.ReadAllText(Path.Combine(pair.Local, "remote")));
    }

    [Fact]
    public async Task UnsatisfiableRangeRetriesWithAFullAnonymousRequest()
    {
        site.Put(Root + "/remote", "abcdefgh");
        site.DownloadLimit = 3;
        await Assert.ThrowsAsync<InvalidDataException>(() => RunAsync());
        site.RejectRange = true;
        site.DownloadLimit = null;
        Assert.Equal(1, (await RunAsync()).Downloaded);
        Assert.Equal(new long?[] { null, 3, null }, site.Ranges);
        Assert.Equal("abcdefgh", File.ReadAllText(Path.Combine(pair.Local, "remote")));
    }

    [Fact]
    public async Task IgnoresOrDisconnectsWithoutReplayingStaleDownloads()
    {
        site.Put(Root + "/remote", "abcdefgh");
        site.DownloadLimit = 3;
        string cache = store.TransferDirectory(pair);
        await Assert.ThrowsAsync<InvalidDataException>(() => RunAsync(new SyncActivity(cache)));
        store.SaveRules(pair, "remote");
        Assert.Equal(0, (await RunAsync()).Downloaded);
        Assert.Empty(TransferQueue.Open(cache).Entries);
        Assert.Empty(Directory.GetFiles(cache, "*.part"));
        Assert.False(File.Exists(Path.Combine(pair.Local, "remote")));
        Assert.Single(new SyncActivity(cache).ChangesSince(-1)!.Recent);
        store.Disconnect(pair);
        Assert.False(Directory.Exists(cache));
        Assert.Equal("abcdefgh", site.Files[Root + "/remote"].Text);
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
        internal readonly List<string> TrashRequests = [];
        internal int Requests, Transfers;
        internal bool Incomplete, Truncate, OmitListedVersions;
        internal string? ReadbackOverride;
        internal int? DownloadLimit;
        internal bool AcceptRanges, BadRange, RejectRange, ForeignPlaceholder, KeepUploadPlaceholder;
        internal readonly List<long?> Ranges = [];
        internal int UploadChunkSize, SessionsCreated, SessionsCancelled;
        internal int? InterruptPart;
        internal long? TicketExpiry;
        internal readonly List<(string Id, int Part)> Parts = [];
        private readonly Dictionary<string, string> placeholders = [];
        private readonly Dictionary<string, List<string>> uploadedParts = [];
        private readonly Dictionary<string, long> uploadLengths = [];
        internal Func<string, CancellationToken, Task>? BeforeStorageAsync;
        internal Func<string, CancellationToken, Task>? BeforeMetadataAsync;
        internal string? RejectTrash;
        internal Action<string>? BeforeList;
        internal string RootId = "original-folder";
        private int sequence;
        private readonly Dictionary<string, string> uploads = [];
        private readonly SemaphoreSlim requestGate = new(1);

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

        private object Entry(string uri, bool listing = false)
        {
            bool folder = Folders.Contains(uri);
            if (placeholders.TryGetValue(uri, out var sessionId))
            {
                Files.TryGetValue(uri, out var old);
                return new { id = Ids.GetValueOrDefault(uri, sessionId), name = uri[(uri.LastIndexOf('/') + 1)..], path = uri, type = 0,
                    size = uploadLengths[sessionId], primary_entity = old.Version,
                    metadata = new Dictionary<string, string> { ["sys:upload_session_id"] = ForeignPlaceholder ? "another-client" : sessionId } };
            }

            return new
            {
                id = uri == Root ? RootId : folder ? Ids.GetValueOrDefault(uri, uri) : Ids[uri], name = Uri.UnescapeDataString(uri[(uri.LastIndexOf('/') + 1)..]), path = uri, type = folder ? 1 : 0,
                size = folder ? 0 : Encoding.UTF8.GetByteCount(Files[uri].Text), primary_entity = folder || listing && OmitListedVersions ? null : Files[uri].Version
            };
        }

        private static HttpResponseMessage Ok(object? data)
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { code = 0, data }) };
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (BeforeMetadataAsync != null && request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath is "/api/v4/file" or "/api/v4/file/info")
            {
                string target = HttpUtility.ParseQueryString(request.RequestUri.Query)["uri"]!;
                await BeforeMetadataAsync(target, cancellationToken);
            }

            if (request.RequestUri!.Host == "storage.example" && BeforeStorageAsync != null)
            {
                string target = HttpUtility.ParseQueryString(request.RequestUri.Query)["uri"]!;
                await BeforeStorageAsync(target, cancellationToken);
            }

            await requestGate.WaitAsync(cancellationToken);
            try
            {
                return await HandleAsync(request, cancellationToken);
            }
            finally
            {
                requestGate.Release();
            }
        }

        private async Task<HttpResponseMessage> HandleAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var uri = request.RequestUri!;
            var query = HttpUtility.ParseQueryString(uri.Query);
            if (uri.Host == "storage.example")
            {
                Assert.Null(request.Headers.Authorization);
                Transfers++;
                string text = Truncate ? "" : ReadbackOverride ?? Files[query["uri"]!].Text;
                long? from = request.Headers.Range?.Ranges.Single().From;
                Ranges.Add(from);
                if (from != null && RejectRange)
                {
                    return new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable);
                }

                if (from != null && AcceptRanges)
                {
                    int offset = checked((int)from.Value);
                    var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new StringContent(text[offset..]) };
                    response.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(BadRange ? 0 : offset, text.Length - 1, text.Length);
                    return response;
                }

                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(DownloadLimit is int limit ? text[..limit] : text) };
            }

            Assert.NotNull(request.Headers.Authorization);
            if (request.Method == HttpMethod.Get && uri.AbsolutePath == "/api/v4/file")
            {
                string parent = query["uri"]!;
                BeforeList?.Invoke(parent);
                Assert.Contains(parent, Folders);
                var children = Files.Keys.Concat(Folders).Concat(placeholders.Keys).Distinct().Where(key => key[..key.LastIndexOf('/')] == parent)
                    .Select(key => Entry(key, listing: true)).ToArray();
                object pagination = Incomplete ? new { total_items = children.Length + 1, page = 0, page_size = 100 } : new { };
                return Ok(new { files = children, pagination });
            }

            if (uri.AbsolutePath == "/api/v4/file/info")
            {
                return Ok(Entry(query["uri"]!));
            }

            if (uri.AbsolutePath.StartsWith("/api/v4/file/upload/", StringComparison.Ordinal))
            {
                string id = uri.Segments[^2].TrimEnd('/');
                int part = int.Parse(uri.Segments[^1]);
                Parts.Add((id, part));
                if (part == InterruptPart)
                {
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                }

                var parts = uploadedParts[id];
                Assert.Equal(parts.Count, part);
                parts.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
                string target = uploads[id];
                string text = string.Concat(parts);
                if (!KeepUploadPlaceholder && Encoding.UTF8.GetByteCount(text) == uploadLengths[id])
                {
                    Put(target, text);
                    placeholders.Remove(target);
                }

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
                case "/api/v4/file/upload" when request.Method == HttpMethod.Delete:
                    string cancelled = body.GetProperty("id").GetString()!;
                    string cancelledTarget = body.GetProperty("uri").GetString()!;
                    Assert.Equal(cancelled, placeholders[cancelledTarget]);
                    placeholders.Remove(cancelledTarget);
                    SessionsCancelled++;
                    return Ok(null);
                case "/api/v4/file/upload":
                    string uploading = body.GetProperty("uri").GetString()!;
                    if (Files.TryGetValue(uploading, out var previous))
                    {
                        Assert.Equal(previous.Version, body.GetProperty("previous").GetString());
                        Assert.Equal("version", body.GetProperty("entity_type").GetString());
                    }

                    string id = Guid.NewGuid().ToString("N");
                    uploads.Add(id, uploading);
                    uploadedParts.Add(id, []);
                    uploadLengths.Add(id, body.GetProperty("size").GetInt64());
                    placeholders.Add(uploading, id);
                    SessionsCreated++;
                    return Ok(new { session_id = id, chunk_size = UploadChunkSize, expires = TicketExpiry ?? DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() });
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
                    string target = Assert.Single(body.GetProperty("uris").EnumerateArray()).GetString()!;
                    Assert.NotEqual(Root, target);
                    TrashRequests.Add(target);
                    if (target == RejectTrash)
                    {
                        return new HttpResponseMessage(HttpStatusCode.Conflict);
                    }

                    if (Folders.Remove(target))
                    {
                        Folders.RemoveWhere(path => path.StartsWith(target + "/", StringComparison.Ordinal));
                        foreach (string path in Files.Keys.Where(path => path.StartsWith(target + "/", StringComparison.Ordinal)).ToArray())
                        {
                            Files.Remove(path);
                        }
                    }
                    else
                    {
                        Assert.True(Files.Remove(target));
                    }

                    return Ok(null);
                default:
                    throw new InvalidOperationException(uri.AbsolutePath);
            }
        }
    }
}
