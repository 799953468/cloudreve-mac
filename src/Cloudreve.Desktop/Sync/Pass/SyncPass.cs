using Cloudreve.Sync;

namespace Cloudreve.Desktop;

// Work queues and caches that outlive a single pass of one pair.
internal sealed class PassResources
{
    internal AdaptiveWorkQueue LocalScan { get; } = new();
    internal AdaptiveWorkQueue RemoteScan { get; } = new();
    internal AdaptiveWorkQueue Transfers { get; } = new();
    internal TransferQueue? CachedQueue { get; set; }
}

// One reconciliation of a folder pair: inventory both sides, then settle folders, moves, files, transfers and folder
// deletions in that order. Reconcilers decide every step from the current view; this class executes them through
// PairOperations and feeds the results back into the view, so later steps see earlier changes.
internal sealed class SyncPass(FolderPairStore store, FolderPair pair, PairOperations ops, PassResources resources, SyncActivity? activity,
    Action? beginFullRemoteInventory, PairScanAttempt attempt, PairScanScope? scope)
{
    private BaselineDatabase database = null!;
    private TransferQueue queue = null!;
    private PairIgnoreRules rules = null!;
    private Dictionary<string, FileBaseline> baseline = null!;
    private Dictionary<string, FileBaseline> original = null!;
    private PairInventory local = null!;
    private PairInventory remote = null!;
    private ReconcileView view = null!;

    private readonly ConflictSet conflicts = new();
    private readonly List<FileConflict> choices = [];
    private readonly HashSet<string> blocked = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> deferred = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> deferredFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PairNode> directoryTrash = [];
    private readonly HashSet<string> trashedDirectories = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileTransfer> pending = [];
    private readonly HashSet<string> finished = [];
    private int uploaded, downloaded, removed;
    private bool dirty;

    private DriveClient Drive
    {
        get { return ops.Drive; }
    }

    internal async Task<PairSyncResult> RunAsync(CancellationToken cancel)
    {
        using (store.Lock(pair))
        using (database = store.OpenBaseline(pair))
        {
            await InventoryAsync(cancel);
            try
            {
                await ReconcileAsync(cancel);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                activity?.Finish(UiText.Get("activity.cancelled"));
                throw;
            }
            catch
            {
                activity?.Finish(UiText.Get("activity.failed"));
                throw;
            }
            finally
            {
                await CheckpointAsync();
            }

            return new PairSyncResult(uploaded, downloaded, removed, conflicts.Keys.ToArray(), choices, deferred.Count,
                deferredFiles.Count, deferred.Concat(deferredFiles).ToArray(), conflicts.Causes);
        }
    }

    private async Task InventoryAsync(CancellationToken cancel)
    {
        activity?.SetScan(ScanPhase.Local, isScoped: scope != null);
        try
        {
            FolderPairStore.Verify(pair);
            queue = TransferQueue.Open(store.TransferDirectory(pair), resources.CachedQueue);
            resources.CachedQueue = queue;
            rules = new PairIgnoreRules(store.ReadRules(pair));
            await RestoreVerifiedTransfersAsync(cancel);
            if (scope != null && database.HasLegacyIds())
            {
                scope = null;
                attempt.FullReason = "LegacyBaselineIds";
            }

            while (true)
            {
                attempt.Scope = scope;
                baseline = database.Read(scope);
                activity?.SetScan(ScanPhase.Local, isScoped: scope != null);
                attempt.Stage = "ScanLocal";
                local = await PairInventory.ReadLocalAsync(pair, rules, baseline, resources.LocalScan, cancel,
                    activity == null ? null : count => activity.SetScan(ScanPhase.Local, count, scope != null), scope);
                activity?.SetScan(ScanPhase.Remote, isScoped: scope != null);
                attempt.Stage = "VerifyRemoteRoot";
                await ops.VerifyRemoteRootAsync(cancel);
                attempt.Stage = "ScanRemote";
                if (scope == null)
                {
                    beginFullRemoteInventory?.Invoke();
                }

                remote = await PairInventory.ReadRemoteAsync(pair.Remote, "", Drive, rules, resources.RemoteScan, cancel,
                    activity == null ? null : count => activity.SetScan(ScanPhase.Remote, count, scope != null), scope);
                string? escalation = scope?.FullInventoryReason(baseline, local, remote, database);
                if (escalation == null)
                {
                    break;
                }

                attempt.FullReason = "Escalated:" + escalation;
                scope = null;
            }

            original = new Dictionary<string, FileBaseline>(baseline);
            attempt.Stage = "VerifyRoots";
            await ops.VerifyRemoteRootAsync(cancel);
            FolderPairStore.Verify(pair);
        }
        catch (Exception) when (cancel.IsCancellationRequested)
        {
            activity?.SetScan(ScanPhase.Idle);
            throw;
        }
        catch
        {
            activity?.SetScan(ScanPhase.Failed);
            throw;
        }

        activity?.SetScan(ScanPhase.Idle);
        view = new ReconcileView(baseline, local, remote, rules, conflicts, blocked);
    }

    // A worker can finish before the coordinator gets to commit the batch baseline.
    private async Task RestoreVerifiedTransfersAsync(CancellationToken cancel)
    {
        var verified = queue.VerifiedEntries();
        if (verified.Count == 0)
        {
            return;
        }

        var queued = database.ReadPaths(verified.Select(entry => entry.File.Key));
        var restored = new Dictionary<string, FileBaseline>(queued);
        if (queue.RestoreVerified(verified, restored, rules))
        {
            attempt.Stage = "RestoreVerifiedTransfers";
            await ops.VerifyRemoteRootAsync(cancel);
            database.SaveChanges(queued, restored);
        }
    }

    private async Task ReconcileAsync(CancellationToken cancel)
    {
        // A pass that migrates mount IDs cannot trust their volume, so it never adopts a recreated directory.
        bool migrating = LegacyVolumeIds.Present(baseline);
        var unverified = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        dirty = LegacyVolumeIds.Upgrade(baseline, local, unverified);
        conflicts.AddRange(unverified, ConflictCause.UnverifiedIdentity);
        blocked.UnionWith(unverified);
        attempt.Stage = "SyncDirectories";
        await SyncDirectoriesAsync(!migrating, cancel);
        attempt.Stage = "ApplyFileMoves";
        await ApplyFileMovesAsync(cancel);
        attempt.Stage = "ReconcileFiles";
        blocked.UnionWith(conflicts.Keys);
        await ReconcileFilesAsync(cancel);
        attempt.Stage = "TransferFiles";
        await TransferFilesAsync(cancel);
        // These trees were blocked during the file pass. Trash only after rechecking their contents.
        attempt.Stage = "TrashDirectories";
        await TrashDirectoriesAsync(cancel);
    }

    private void Conflict(string key, ConflictCause cause, bool block = true)
    {
        conflicts.Add(key, cause);
        if (block)
        {
            blocked.Add(key);
        }
    }

    private async Task SyncDirectoriesAsync(bool adoptRecreated, CancellationToken cancel)
    {
        foreach (string key in DirectoryReconciler.MovedOutOfMissingFolders(view))
        {
            Conflict(key, ConflictCause.UncertainMove);
        }

        var keys = DirectoryReconciler.Keys(view);
        var scannedIds = local.Nodes.Values.Where(node => node.LocalId != null).Select(node => node.LocalId!).ToHashSet();
        var trackedIds = baseline.Values.Where(entry => entry.LocalId != null).Select(entry => entry.LocalId!).ToHashSet();
        foreach (string key in keys)
        {
            cancel.ThrowIfCancellationRequested();
            var decision = DirectoryReconciler.Decide(key, view, adoptRecreated, scannedIds, trackedIds);
            string path = Path.Combine(pair.Local, key);
            switch (decision.Action)
            {
                case DirectoryAction.Forget:
                    dirty |= baseline.Remove(key);
                    continue;
                case DirectoryAction.Skip:
                    continue;
                case DirectoryAction.Conflict:
                    Conflict(key, decision.Cause);
                    continue;
                case DirectoryAction.TrashWhenVerified:
                    blocked.Add(key);
                    directoryTrash.Add(remote.Nodes[key]);
                    continue;
                case DirectoryAction.RemoveLocal:
                    if (!await RemoveLocalDirectoryAsync(key, path, cancel))
                    {
                        // A non-empty local directory is never removed.
                        Conflict(key, ConflictCause.DirectoryNotEmpty);
                    }

                    continue;
                case DirectoryAction.CreateRemote:
                    if (!await CreateRemoteDirectoryAsync(key, cancel))
                    {
                        continue;
                    }

                    break;
                case DirectoryAction.CreateLocal:
                    if (!await CreateLocalDirectoryAsync(key, path, cancel))
                    {
                        continue;
                    }

                    break;
            }

            var current = new FileBaseline("", "", local.Nodes[key].LocalId, remote.Nodes[key].Remote!.Id, true);
            if (current != baseline.GetValueOrDefault(key))
            {
                baseline[key] = current;
                dirty = true;
            }
        }
    }

    // Returns false when the folder still has contents; true when it was removed or deferred.
    private async Task<bool> RemoveLocalDirectoryAsync(string key, string path, CancellationToken cancel)
    {
        activity?.MarkSyncWork();
        await ops.VerifyRemoteRootAsync(cancel);
        await ops.ResolveRemoteAsync(key, null, false, cancel);
        if (DeferIfChanged(key))
        {
            return true;
        }

        var removal = LocalFile.ChangeDirectoryEntry(path, create: false);
        if (removal == DirectoryChangeResult.Applied)
        {
            local.Nodes.Remove(key);
            dirty |= baseline.Remove(key);
            return true;
        }

        if (removal == DirectoryChangeResult.PathChanged)
        {
            DeferIfChanged(key, operationRaced: true);
            return true;
        }

        return false;
    }

    // Returns whether the folder now exists on both sides and should be recorded.
    private async Task<bool> CreateRemoteDirectoryAsync(string key, CancellationToken cancel)
    {
        activity?.MarkSyncWork();
        FolderPairStore.Verify(pair);
        await ops.VerifyRemoteRootAsync(cancel);
        string name = Path.GetFileName(key);
        string relativeParent = PairKey.Parent(key) ?? "";
        string parent = relativeParent.Length == 0 ? pair.Remote : remote.Nodes[relativeParent].Remote!.Uri;
        await ops.ResolveRemoteAsync(key, null, false, cancel);
        if (DeferIfChanged(key))
        {
            return false;
        }

        await Drive.CreateFolderAsync(parent, name, cancel);
        var directory = await Drive.StatAsync(DriveClient.ChildUri(parent, name), cancel);
        PairInventory.ValidateRemote(parent, directory);
        if (!directory.IsFolder || directory.SkipSync || directory.Name.Normalize() != name)
        {
            throw new IOException(UiText.Get("sync.createdFolderUnverified"));
        }

        remote.Nodes.Add(key, new PairNode(key, NodeKind.Directory, Remote: directory));
        return !DeferIfChanged(key);
    }

    private async Task<bool> CreateLocalDirectoryAsync(string key, string path, CancellationToken cancel)
    {
        activity?.MarkSyncWork();
        FolderPairStore.Verify(pair);
        await ops.VerifyRemoteRootAsync(cancel);
        var there = remote.Nodes[key];
        if (string.IsNullOrEmpty(there.Remote!.Id) || await Drive.FolderIdAsync(there.Remote.Uri, cancel) != there.Remote.Id)
        {
            throw new IOException(UiText.Get("sync.remoteFolderChanged"));
        }

        if (DeferIfChanged(key))
        {
            return false;
        }

        if (LocalFile.ChangeDirectoryEntry(path, create: true) == DirectoryChangeResult.PathChanged)
        {
            DeferIfChanged(key, operationRaced: true);
            return false;
        }

        local.Nodes.Add(key, new PairNode(key, NodeKind.Directory, LocalId: LocalFile.Identity(path)));
        return !DeferIfChanged(key);
    }

    // A local folder that changed since the scan defers its subtree to the next pass instead of failing this one.
    private bool DeferIfChanged(string key, bool operationRaced = false)
    {
        string? changed = ops.ChangedLocalDirectory(local, key);
        if (changed == null && !operationRaced)
        {
            return false;
        }

        // Loss of the paired root is not a child race; it must abort the whole pass.
        FolderPairStore.Verify(pair);
        string subtree = changed ?? key;
        deferred.Add(subtree);
        blocked.Add(subtree);
        return true;
    }

    private async Task ApplyFileMovesAsync(CancellationToken cancel)
    {
        var candidates = MoveReconciler.Candidates(view, cancel);
        if (candidates.Count == 0)
        {
            return;
        }

        var index = MoveReconciler.Index(view, candidates);
        foreach (var (oldKey, previous) in candidates)
        {
            cancel.ThrowIfCancellationRequested();
            if (MoveReconciler.Decide(oldKey, previous, view, index) is { } move)
            {
                await ApplyMoveAsync(move, cancel);
                dirty = true;
            }
        }
    }

    private async Task ApplyMoveAsync(FileMove move, CancellationToken cancel)
    {
        var (oldKey, previous, here, there, localMoved, remoteMoved) = move;
        string target = move.Target;
        string parent = PairKey.Parent(target) ?? "";
        await ops.VerifyRemoteRootAsync(cancel);
        await ops.ResolveRemoteAsync(there.Key, there.Remote, false, cancel);
        await ops.RequireLocalAsync(here.Key, here.Hash, cancel);
        if (LocalFile.Identity(Path.Combine(pair.Local, here.Key)) != previous.LocalId)
        {
            throw new IOException(UiText.Get("sync.localReplacedDuringMove"));
        }

        if (localMoved)
        {
            await ops.RequireLocalAsync(oldKey, null, cancel);
        }

        if (remoteMoved)
        {
            await ops.ResolveRemoteAsync(oldKey, null, false, cancel);
        }

        activity?.Start(UiText.Get("activity.move"), target);
        if (!remoteMoved)
        {
            string destination = await ops.ResolveRemoteAsync(target, null, true, cancel);
            await ops.VerifyRemoteRootAsync(cancel);
            await ops.VerifyVersionAsync(there.Remote!, cancel);
            if (move.Rename)
            {
                await Drive.RenameAsync(there.Remote!, Path.GetFileName(target), cancel);
            }
            else
            {
                string directory = pair.Remote;
                foreach (string segment in parent.Split('/', StringSplitOptions.RemoveEmptyEntries))
                {
                    directory = DriveClient.ChildUri(directory, segment);
                }

                await Drive.MoveAsync(there.Remote!, directory, cancel);
            }

            var moved = there.Remote! with { Uri = destination, Name = Path.GetFileName(target) };
            await ops.VerifyVersionAsync(moved, cancel);
            remote.Nodes.Remove(oldKey);
            remote.Nodes.Add(target, there with { Key = target, Remote = moved });
        }
        else if (!localMoved)
        {
            ops.EnsureLocalDirectory(parent);
            await ops.RequireLocalAsync(target, null, cancel);
            await ops.RequireLocalAsync(oldKey, here.Hash, cancel);
            if (LocalFile.Identity(Path.Combine(pair.Local, oldKey)) != previous.LocalId)
            {
                throw new IOException(UiText.Get("sync.localReplacedDuringMove"));
            }

            File.Move(Path.Combine(pair.Local, oldKey), Path.Combine(pair.Local, target), false);
            local.Nodes.Remove(oldKey);
            local.Nodes.Add(target, here with { Key = target });
        }

        baseline.Remove(oldKey);
        baseline.Add(target, previous);
        activity?.Finish(UiText.Get("activity.done"));
    }

    private async Task ReconcileFilesAsync(CancellationToken cancel)
    {
        foreach (string key in FileReconciler.Keys(view))
        {
            cancel.ThrowIfCancellationRequested();
            var decision = FileReconciler.Decide(key, view);
            if (decision.Step == FileStep.CheckOwnedUpload)
            {
                if (await ops.CollectOwnedUploadAsync(key, decision.There!.Remote!, decision.Here, decision.Previous, queue, pending, cancel))
                {
                    continue;
                }

                decision = FileReconciler.Decide(key, view, ownedUploadChecked: true);
            }

            if (decision.Action != SyncAction.Unchanged)
            {
                FolderPairStore.Verify(pair);
            }

            await ApplyFileAsync(key, decision, cancel);
        }
    }

    private async Task ApplyFileAsync(string key, FileDecision decision, CancellationToken cancel)
    {
        var (previous, here, file) = (decision.Previous, decision.Here, decision.There?.Remote);
        switch (decision.Step)
        {
            case FileStep.Skip:
                return;
            case FileStep.Forget:
                dirty |= baseline.Remove(key);
                return;
            case FileStep.Conflict:
                Conflict(key, decision.Cause);
                return;
            case FileStep.ConflictWithChoice:
                Conflict(key, decision.Cause, block: false);
                choices.Add(new FileConflict(pair.Marker, key, here?.Hash, file));
                return;
            case FileStep.UpdateIds:
                baseline[key] = previous! with { LocalId = here!.LocalId, LocalGeneration = here.LocalGeneration, RemoteId = file!.Id };
                dirty = true;
                return;
            case FileStep.TrashRemote:
                activity?.Start(UiText.Get("activity.moveToCloudTrash"), key);
                await ops.VerifyRemoteRootAsync(cancel);
                await ops.ResolveRemoteAsync(key, file!, false, cancel);
                await ops.RequireLocalAsync(key, null, cancel);
                await Drive.TrashAsync(file!, cancel);
                activity?.Finish(UiText.Get("activity.done"));
                removed++;
                dirty |= baseline.Remove(key);
                return;
            case FileStep.RecoverLocal:
                activity?.Start(UiText.Get("activity.moveToRecovery"), key);
                await ops.VerifyRemoteRootAsync(cancel);
                await ops.ResolveRemoteAsync(key, null, false, cancel);
                await ops.RequireLocalAsync(key, here?.Hash, cancel);
                ops.MoveToRecovery(key);
                activity?.Finish(UiText.Get("activity.done"));
                removed++;
                dirty |= baseline.Remove(key);
                return;
            case FileStep.Transfer:
                pending.Add(new FileTransfer(key, decision.Action, here, file, previous));
                return;
            default:
                throw new InvalidOperationException(UiText.Get("sync.unknownAction"));
        }
    }

    private async Task TransferFilesAsync(CancellationToken cancel)
    {
        var work = queue.Reconcile(pending, scope == null && deferred.Count == 0 ? null :
            key => (scope != null && !scope.Covers(key)) || ReconcileView.Within(deferred, key), scope);
        activity?.SetPending(queue);
        await resources.Transfers.RunAsync(work, (entry, token) => ops.TransferFileAsync(entry.File, queue.Files(entry), token), completed =>
        {
            var item = completed.Item;
            if (completed.Deferred)
            {
                // Keep the queued entry and its checkpoint; the next scan of this path replaces it.
                deferredFiles.Add(item.Key);
                return;
            }

            finished.Add(item.Key);
            if (completed.Baseline == null)
            {
                Conflict(item.Key, ConflictCause.ContentsDiffer, block: false);
                choices.Add(new FileConflict(pair.Marker, item.Key, item.Local?.Hash, item.Remote));
                return;
            }

            baseline[item.Key] = completed.Baseline;
            dirty = true;
            if (item.Action == SyncAction.Upload)
            {
                uploaded++;
            }
            else if (item.Action == SyncAction.Download)
            {
                downloaded++;
            }
        }, cancel);
    }

    private async Task TrashDirectoriesAsync(CancellationToken cancel)
    {
        foreach (var directory in directoryTrash)
        {
            if (ReconcileView.Within(deferred, directory.Key))
            {
                continue;
            }

            if (await ops.TryTrashDirectoryAsync(directory, baseline, rules, cancel))
            {
                trashedDirectories.Add(directory.Key);
                removed++;
            }
            else
            {
                Conflict(directory.Key, ConflictCause.DirectoryNotEmpty, block: false);
            }
        }
    }

    // If checkpointing itself fails, it is the exception the caller will receive.
    private async Task CheckpointAsync()
    {
        string previousStage = attempt.Stage;
        attempt.Stage = "Checkpoint";
        if (trashedDirectories.Count > 0)
        {
            foreach (string key in baseline.Keys.Where(key => ReconcileView.Within(trashedDirectories, key)).ToArray())
            {
                baseline.Remove(key);
            }
        }

        if (dirty || trashedDirectories.Count > 0)
        {
            database.SaveChanges(original, baseline);
        }

        queue.Complete(finished);
        activity?.SetPending(queue);
        if (activity != null)
        {
            await activity.FlushHistoryAsync();
        }

        attempt.Stage = previousStage;
    }
}
