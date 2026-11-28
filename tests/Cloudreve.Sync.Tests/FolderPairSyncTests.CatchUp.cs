using Cloudreve.Desktop;
using Xunit;

namespace Cloudreve.Sync.Tests;

public sealed partial class FolderPairSyncTests
{
    private FolderPairSync Engine(SyncActivity? activity = null)
    {
        var restored = new FolderPairStore(Path.Combine(temp.FullName, "settings"), session);
        return new FolderPairSync(restored, restored.Load()!, new DriveClient(http, http, session, _ => { }), activity);
    }

    // A full pass that records the change cursor. The clock moves on first, so the setup lies clearly before the
    // cursor and its margin, as it would after any real pause.
    private Task<PairSyncResult> FullPassAsync()
    {
        site.Now = site.Now.AddMinutes(10);
        return Engine().RunAsync(TestContext.Current.CancellationToken, new PairScanAttempt { ServerTime = site.Now });
    }

    private async Task<(PairSyncResult Result, PairScanAttempt Attempt, CatchUpPlan Plan)> CatchUpAsync(bool local = true, bool cloud = true)
    {
        var engine = Engine();
        var plan = await engine.PlanCatchUpAsync(local, cloud, [], site.Now, TestContext.Current.CancellationToken);
        Assert.Null(plan.FullReason);
        var attempt = new PairScanAttempt { ServerTime = site.Now, CatchUpReason = "Startup", CoversCloudChanges = cloud };
        var result = await engine.RunAsync(TestContext.Current.CancellationToken, attempt, plan.Scope);
        return (result, attempt, plan);
    }

    [Fact]
    public async Task CatchUpVisitsOnlyChangedPathsOnBothSides()
    {
        site.Folders.UnionWith([Root + "/a", Root + "/b", Root + "/unrelated"]);
        site.Put(Root + "/a/x", "x");
        site.Put(Root + "/unrelated/keep", "keep");
        await FullPassAsync();
        site.Now = site.Now.AddMinutes(10);
        File.WriteAllText(Path.Combine(pair.Local, "a", "x"), "local edit");
        site.Put(Root + "/b/y", "cloud new");
        site.Now = site.Now.AddMinutes(10);

        site.BeforeList = uri => Assert.NotEqual(Root + "/unrelated", uri);
        var (result, attempt, plan) = await CatchUpAsync();
        Assert.Equal(["a/x", "b/y"], plan.Scope!.Targets.Order());
        Assert.Equal((1, 1), (plan.LocalChanges, plan.CloudChanges));
        Assert.NotNull(attempt.Scope);
        Assert.Null(attempt.FullReason);
        Assert.Equal((1, 1), (result.Uploaded, result.Downloaded));
        Assert.Equal("local edit", site.Files[Root + "/a/x"].Text);
        Assert.Equal("cloud new", File.ReadAllText(Path.Combine(pair.Local, "b", "y")));

        // The cursor moved past the cloud change; only this pass's own upload is newer than it.
        site.Now = site.Now.AddMinutes(10);
        var next = await Engine().PlanCatchUpAsync(true, true, [], site.Now, TestContext.Current.CancellationToken);
        Assert.Equal(["a/x"], next.Scope!.Targets);
    }

    [Fact]
    public async Task CatchUpConfirmsCloudDeletionsFromTheTrashWithoutAFullScan()
    {
        site.Put(Root + "/doc", "doc");
        site.Put(Root + "/keep", "keep");
        site.Folders.Add(Root + "/empty");
        site.Ids[Root + "/empty"] = "empty-id";
        await FullPassAsync();
        site.Now = site.Now.AddMinutes(10);
        site.TrashFile(Root + "/doc");
        site.TrashFile(Root + "/empty");

        var (result, attempt, plan) = await CatchUpAsync();
        Assert.Equal(2, plan.CloudDeletions);
        Assert.Equal(["doc", "empty"], plan.Scope!.ConfirmedRemoteDeletions.Order());
        Assert.NotNull(attempt.Scope);
        Assert.False(File.Exists(Path.Combine(pair.Local, "doc")));
        Assert.False(Directory.Exists(Path.Combine(pair.Local, "empty")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(pair.Local, "keep")));
        // A cloud deletion never destroys the local copy: it goes to the recovery folder.
        Assert.Single(Directory.GetFiles(Path.Combine(pair.Local, PairInventory.Recovery), "doc", SearchOption.AllDirectories));
        Assert.Empty(result.Conflicts);
    }

    [Fact]
    public async Task CatchUpConfirmsLocalDeletionsAndAppliesLocalMoves()
    {
        site.Put(Root + "/gone", "gone");
        site.Put(Root + "/old", "old");
        await FullPassAsync();
        site.Now = site.Now.AddMinutes(10);
        File.Delete(Path.Combine(pair.Local, "gone"));
        File.Move(Path.Combine(pair.Local, "old"), Path.Combine(pair.Local, "new"));
        int transfers = site.Transfers;

        var (result, attempt, plan) = await CatchUpAsync();
        Assert.Equal(["gone", "new", "old"], plan.Scope!.Targets.Order());
        Assert.Equal(["gone"], plan.Scope.ConfirmedLocalDeletions);
        Assert.NotNull(attempt.Scope);
        Assert.Equal([Root + "/gone"], site.TrashRequests);
        Assert.Equal("old", site.Files[Root + "/new"].Text);
        Assert.False(site.Files.ContainsKey(Root + "/old"));
        Assert.Equal(transfers, site.Transfers);
        Assert.Empty(result.Conflicts);
    }

    [Fact]
    public async Task CatchUpFollowsCloudRenamesByIdentity()
    {
        site.Put(Root + "/before", "text");
        await FullPassAsync();
        site.Now = site.Now.AddMinutes(10);
        site.Move(Root + "/before", Root + "/after");
        int transfers = site.Transfers;

        var (result, attempt, plan) = await CatchUpAsync();
        Assert.Equal(["after", "before"], plan.Scope!.Targets.Order());
        Assert.NotNull(attempt.Scope);
        Assert.Equal("text", File.ReadAllText(Path.Combine(pair.Local, "after")));
        Assert.False(File.Exists(Path.Combine(pair.Local, "before")));
        Assert.Equal(transfers, site.Transfers);
        Assert.Empty(result.Conflicts);
    }

    [Fact]
    public async Task CatchUpNeedsAFreshCursorForCloudChangesOnly()
    {
        var engine = Engine();
        var cancel = TestContext.Current.CancellationToken;
        Assert.Equal("NoChangeCursor", (await engine.PlanCatchUpAsync(true, true, [], site.Now, cancel)).FullReason);
        // Comparing the local side with the baseline needs no cursor.
        Assert.Null((await engine.PlanCatchUpAsync(true, false, [], site.Now, cancel)).FullReason);
        await FullPassAsync();
        Assert.Null((await engine.PlanCatchUpAsync(true, true, [], site.Now, cancel)).FullReason);
        Assert.Equal("ChangeCursorExpired", (await engine.PlanCatchUpAsync(true, true, [], site.Now.AddDays(8), cancel)).FullReason);
    }

    [Fact]
    public async Task CatchUpResumesInterruptedTransfersAndSignaledPaths()
    {
        site.Put(Root + "/queued", "cloud");
        await FullPassAsync();
        var remote = new DriveEntry("queued", Root + "/queued", false, 5, site.Files[Root + "/queued"].Version);
        TransferQueue.Open(store.TransferDirectory(pair)).Reconcile([new FileTransfer("queued", SyncAction.Download, null, remote, null)]);
        var plan = await Engine().PlanCatchUpAsync(true, true, ["signaled"], site.Now, TestContext.Current.CancellationToken);
        Assert.Equal(["queued", "signaled"], plan.Scope!.Targets.Order());
    }

    [Fact]
    public async Task ConflictsComeBackAfterARestartWithTheirCause()
    {
        site.Put(Root + "/doc", "cloud");
        File.WriteAllText(Path.Combine(pair.Local, "doc"), "local");
        var first = await FullPassAsync();
        Assert.Equal(ConflictCause.ContentsDiffer, first.Causes!["doc"]);

        // Nothing changed since, so only the saved conflict list brings the path into a cloud catch-up after a restart.
        site.Now = site.Now.AddMinutes(10);
        var (result, _, plan) = await CatchUpAsync(local: false);
        Assert.Equal(["doc"], plan.Scope!.Targets);
        Assert.Equal(ConflictCause.ContentsDiffer, result.Causes!["doc"]);
        var choice = Assert.Single(result.Choices);

        await Engine().ResolveConflictAsync(choice, ConflictChoice.KeepLocal, TestContext.Current.CancellationToken);
        Assert.Empty((await RunAsync(scope: new PairScanScope(["doc"]))).Conflicts);
        site.Now = site.Now.AddMinutes(10);
        var settled = await Engine().PlanCatchUpAsync(true, false, [], site.Now, TestContext.Current.CancellationToken);
        Assert.Empty(settled.Scope!.Targets);
    }

    [Fact]
    public async Task CloudDeletionFromAPushEventSyncsInAPathScan()
    {
        site.Put(Root + "/doc", "doc");
        site.Put(Root + "/keep", "keep");
        await FullPassAsync();
        site.TrashFile(Root + "/doc");

        var attempt = new PairScanAttempt { ServerTime = site.Now };
        site.BeforeList = uri => Assert.Equal(Root, uri);
        var result = await RunAsync(attempt: attempt, scope: new PairScanScope(["doc"]));
        Assert.NotNull(attempt.Scope);
        Assert.Null(attempt.FullReason);
        Assert.Equal(1, result.Removed);
        Assert.False(File.Exists(Path.Combine(pair.Local, "doc")));
        Assert.Single(Directory.GetFiles(Path.Combine(pair.Local, PairInventory.Recovery), "doc", SearchOption.AllDirectories));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(pair.Local, "keep")));
    }

    [Fact]
    public async Task CloudFolderDeletionRemovesItsUnchangedLocalCopy()
    {
        site.Folders.UnionWith([Root + "/dir", Root + "/dir/sub"]);
        site.Ids[Root + "/dir"] = "dir-id";
        site.Put(Root + "/dir/a", "a");
        site.Put(Root + "/dir/sub/b", "b");
        await FullPassAsync();
        site.TrashFile(Root + "/dir");

        var attempt = new PairScanAttempt { ServerTime = site.Now };
        var result = await RunAsync(attempt: attempt, scope: new PairScanScope(["dir"]));
        Assert.NotNull(attempt.Scope);
        Assert.Empty(result.Conflicts);
        Assert.False(Directory.Exists(Path.Combine(pair.Local, "dir")));
        Assert.Equal(["a", "b"], Directory.GetFiles(Path.Combine(pair.Local, PairInventory.Recovery), "*", SearchOption.AllDirectories)
            .Select(File.ReadAllText).Order());
        Assert.DoesNotContain(store.ReadBaseline(pair).Keys, key => key.StartsWith("dir", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PermanentCloudDeletionStillNeedsAFullScan()
    {
        site.Put(Root + "/doc", "doc");
        await FullPassAsync();
        // Removed without going through the trash, so nothing can tell it apart from a move out of the scope.
        site.Files.Remove(Root + "/doc");

        var attempt = new PairScanAttempt { ServerTime = site.Now };
        await RunAsync(attempt: attempt, scope: new PairScanScope(["doc"]));
        Assert.Null(attempt.Scope);
        Assert.Equal("Escalated:RemoteItemDisappeared", attempt.FullReason);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AbandonedUploadPlaceholderIsCancelledAfterADay(bool stale)
    {
        string path = Path.Combine(pair.Local, "upload");
        File.WriteAllText(path, "abcdef");
        site.UploadChunkSize = 3;
        site.InterruptPart = 1;
        await Assert.ThrowsAsync<HttpRequestException>(() => RunAsync());
        site.InterruptPart = null;
        // The client lost track of its upload, as after a crash: the placeholder now belongs to nobody it knows.
        TransferQueue.Open(store.TransferDirectory(pair)).Reconcile([]);
        site.Modified[Root + "/upload"] = site.Now.AddHours(stale ? -25 : -1);

        var result = await RunAsync(attempt: new PairScanAttempt { ServerTime = site.Now });
        if (!stale)
        {
            Assert.Equal(ConflictCause.UploadInProgress, result.Causes!["upload"]);
            Assert.Equal(0, site.SessionsCancelled);
            return;
        }

        Assert.Empty(result.Conflicts);
        Assert.Equal(1, site.SessionsCancelled);
        Assert.Equal(["upload"], result.DeferredPaths);
        // The retry finds an ordinary missing file and uploads it.
        Assert.Equal(1, (await RunAsync(scope: new PairScanScope(["upload"]))).Uploaded);
        Assert.Equal("abcdef", site.Files[Root + "/upload"].Text);
    }

    [Fact]
    public async Task FolderRecreatedInTheCloudKeepsSyncingFileByFile()
    {
        site.Folders.Add(Root + "/deps");
        site.Ids[Root + "/deps"] = "old-folder";
        site.Put(Root + "/deps/same", "same");
        site.Put(Root + "/deps/changed", "before");
        site.Put(Root + "/deps/gone", "gone");
        await FullPassAsync();
        // Another computer deleted and recreated the folder: new folder ID, files uploaded again as new versions.
        site.Ids[Root + "/deps"] = "new-folder";
        site.Files.Remove(Root + "/deps/gone");
        site.Put(Root + "/deps/same", "same");
        site.Put(Root + "/deps/changed", "after");
        var sameVersion = site.Files[Root + "/deps/same"].Version;

        var result = await FullPassAsync();
        var baseline = store.ReadBaseline(pair);
        Assert.Equal("new-folder", baseline["deps"].RemoteId);
        // Same contents under a new version: recorded without touching the local copy or keeping a recovery copy.
        Assert.Equal(sameVersion, baseline["deps/same"].RemoteVersion);
        Assert.Equal(0, result.Downloaded);
        // Different contents are not overwritten silently; they become an ordinary content conflict.
        Assert.Equal(ConflictCause.ContentsDiffer, Assert.Single(result.Causes!).Value);
        Assert.Equal("before", File.ReadAllText(Path.Combine(pair.Local, "deps", "changed")));
        Assert.False(File.Exists(Path.Combine(pair.Local, "deps", "gone")));
        var recovered = Directory.GetFiles(Path.Combine(pair.Local, PairInventory.Recovery), "*", SearchOption.AllDirectories).Select(File.ReadAllText).ToArray();
        Assert.Equal(["gone"], recovered);
    }

    [Fact]
    public async Task LocalOnlyLinksAreSkippedInsteadOfReportedAsConflicts()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Creating symbolic links needs no privileges on macOS.");
        File.WriteAllText(Path.Combine(pair.Local, "target"), "target");
        File.CreateSymbolicLink(Path.Combine(pair.Local, "link"), "target");
        var result = await RunAsync();
        Assert.Empty(result.Conflicts);
        Assert.Equal(["link"], result.Skipped);
        Assert.Equal(1, result.Uploaded);
        Assert.False(site.Files.ContainsKey(Root + "/link"));
    }

    [Fact]
    public async Task CoordinatorCatchesUpAtStartupAndVerifiesWhenDue()
    {
        site.Put(Root + "/doc", "doc");
        await FullPassAsync();
        site.Now = site.Now.AddMinutes(10);
        site.Put(Root + "/new", "new");

        var attempts = new List<PairScanAttempt>();
        var two = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new PairSyncCoordinator(Engine(), new PairWake(), new DriveClient(http, http, session, _ => { }), Serilog.Log.Logger,
            firstVerificationDelay: TimeSpan.Zero, verificationInterval: TimeSpan.FromMilliseconds(1), verificationCheck: TimeSpan.FromMilliseconds(50))
        {
            Completed = (_, attempt) =>
            {
                lock (attempts)
                {
                    attempts.Add(attempt);
                    if (attempts.Count == 2)
                    {
                        two.TrySetResult();
                    }
                }

                return Task.CompletedTask;
            }
        };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var run = coordinator.RunAsync(stop.Token);
        await two.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        stop.Cancel();
        await run;

        Assert.Equal("Startup", attempts[0].CatchUpReason);
        Assert.NotNull(attempts[0].Scope);
        Assert.Null(attempts[1].Scope);
        Assert.Equal(PairSyncCoordinator.VerificationReason, attempts[1].FullReason);
        Assert.Equal("new", File.ReadAllText(Path.Combine(pair.Local, "new")));
    }
}
