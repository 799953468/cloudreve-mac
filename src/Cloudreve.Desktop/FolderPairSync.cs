using System.Diagnostics;
using Cloudreve.Sync;

namespace Cloudreve.Desktop;

internal sealed record FileConflict(Guid PairId, string Key, string? LocalHash, DriveEntry? Remote);

internal enum ConflictChoice
{
    KeepLocal,
    KeepRemote,
    KeepBoth
}

internal sealed record PairSyncResult(int Uploaded, int Downloaded, int Removed, IReadOnlyList<string> Conflicts, IReadOnlyList<FileConflict> Choices, int DeferredDirectories);

internal sealed class PairScanAttempt
{
    private readonly Stopwatch timer = Stopwatch.StartNew();
    internal string Stage { get; set; } = "Prepare";
    internal PairScanScope? Scope { get; set; }
    internal long ElapsedMs { get { return timer.ElapsedMilliseconds; } }
}

internal sealed partial class FolderPairSync(FolderPairStore store, FolderPair pair, DriveClient drive, SyncActivity? activity = null, Action? beginFullRemoteInventory = null)
{
    private readonly AdaptiveWorkQueue localScan = new();
    private readonly AdaptiveWorkQueue remoteScan = new();
    private readonly AdaptiveWorkQueue transfers = new();
    private TransferQueue? cachedQueue;

    internal async Task<PairSyncResult> RunAsync(CancellationToken cancel, PairScanAttempt? attempt = null, PairScanScope? scope = null)
    {
        attempt ??= new PairScanAttempt();
        using (store.Lock(pair))
        using (var database = store.OpenBaseline(pair))
        {
            TransferQueue queue;
            Dictionary<string, FileBaseline> baseline;
            Dictionary<string, FileBaseline> original;
            PairIgnoreRules rules;
            PairInventory local, remote;
            activity?.SetScan(ScanPhase.Local, isScoped: scope != null);
            try
            {
                FolderPairStore.Verify(pair);
                queue = TransferQueue.Open(store.TransferDirectory(pair), cachedQueue);
                cachedQueue = queue;
                rules = new PairIgnoreRules(store.ReadRules(pair));
                // A worker can finish before the coordinator gets to commit the batch baseline.
                var verified = queue.VerifiedEntries();
                if (verified.Count > 0)
                {
                    var queued = database.ReadPaths(verified.Select(entry => entry.File.Key));
                    var restored = new Dictionary<string, FileBaseline>(queued);
                    if (queue.RestoreVerified(verified, restored, rules))
                    {
                        attempt.Stage = "RestoreVerifiedTransfers";
                        await VerifyRemoteRootAsync(cancel);
                        database.SaveChanges(queued, restored);
                    }
                }

                if (scope != null && database.HasLegacyIds())
                {
                    scope = null;
                }

                while (true)
                {
                    attempt.Scope = scope;
                    baseline = database.Read(scope);
                    activity?.SetScan(ScanPhase.Local, isScoped: scope != null);
                    attempt.Stage = "ScanLocal";
                    local = await PairInventory.ReadLocalAsync(pair, rules, baseline, localScan, cancel,
                        activity == null ? null : count => activity.SetScan(ScanPhase.Local, count, scope != null), scope);
                    activity?.SetScan(ScanPhase.Remote, isScoped: scope != null);
                    attempt.Stage = "VerifyRemoteRoot";
                    await VerifyRemoteRootAsync(cancel);
                    attempt.Stage = "ScanRemote";
                    if (scope == null)
                    {
                        beginFullRemoteInventory?.Invoke();
                    }
                    remote = await PairInventory.ReadRemoteAsync(pair.Remote, "", drive, rules, remoteScan, cancel,
                        activity == null ? null : count => activity.SetScan(ScanPhase.Remote, count, scope != null), scope);
                    if (scope == null || !scope.NeedsFullInventory(baseline, local, remote, database))
                    {
                        break;
                    }

                    scope = null;
                }

                original = new Dictionary<string, FileBaseline>(baseline);

                attempt.Stage = "VerifyRoots";
                await VerifyRemoteRootAsync(cancel);
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

            // Visit parents first. A collision or an unsupported node also protects its descendants.
            var conflicts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var choices = new List<FileConflict>();
            var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var deferred = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var directoryTrash = new List<PairNode>();
            var trashedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new List<FileTransfer>();
            var finished = new HashSet<string>();
            int uploaded = 0, downloaded = 0, removed = 0;
            bool dirty = false;
            try
            {
                dirty = LegacyVolumeIds.Upgrade(baseline, local, conflicts);
                blocked.UnionWith(conflicts);
                attempt.Stage = "SyncDirectories";
                dirty |= await SyncDirectoriesAsync(local, remote, baseline, rules, conflicts, blocked, deferred, directoryTrash, cancel);
                attempt.Stage = "ApplyFileMoves";
                dirty |= await ApplyFileMovesAsync(local, remote, baseline, rules, conflicts, blocked, cancel);
                attempt.Stage = "ReconcileFiles";
                blocked.UnionWith(conflicts);
                var keys = baseline.Keys.Concat(local.Nodes.Keys).Concat(remote.Nodes.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase);
                foreach (string key in keys.ToArray())
                {
                    cancel.ThrowIfCancellationRequested();
                    if (BlockedPath(blocked, key))
                    {
                        continue;
                    }

                    if (rules.Excludes(key) || local.Excluded.Contains(key) || remote.Excluded.Contains(key))
                    {
                        dirty |= baseline.Remove(key);
                        continue;
                    }

                    baseline.TryGetValue(key, out var previous);
                    local.Nodes.TryGetValue(key, out var here);
                    remote.Nodes.TryGetValue(key, out var there);
                    if (previous?.IsDirectory == true)
                    {
                        continue;
                    }

                    if (there?.Key == key && there.Remote?.UploadSessionId != null &&
                        await CollectOwnedUploadAsync(key, there.Remote, here, previous, queue, pending, cancel))
                    {
                        continue;
                    }

                    if (here?.Kind == NodeKind.Unsupported || there?.Kind == NodeKind.Unsupported ||
                        here != null && (here.Key != key || there != null && here.Kind != there.Kind) || there != null && there.Key != key ||
                        previous != null && (here?.Kind == NodeKind.Directory || there?.Kind == NodeKind.Directory))
                    {
                        conflicts.Add(key);
                        blocked.Add(key);
                        continue;
                    }

                    if (here?.Kind == NodeKind.Directory || there?.Kind == NodeKind.Directory)
                    {
                        continue;
                    }

                    var file = there?.Remote;
                    string? hash = here?.Hash;
                    var action = SyncDecision.ForFile(previous, hash, file?.Version);
                    if (action == SyncAction.Unchanged)
                    {
                        if (previous!.LocalId != here!.LocalId || previous.LocalGeneration != here.LocalGeneration || previous.RemoteId != file!.Id)
                        {
                            baseline[key] = previous with { LocalId = here.LocalId, LocalGeneration = here.LocalGeneration, RemoteId = file!.Id };
                            dirty = true;
                        }

                        continue;
                    }

                    FolderPairStore.Verify(pair);
                    switch (action)
                    {
                        case SyncAction.Forget:
                            dirty |= baseline.Remove(key);
                            continue;
                        case SyncAction.Conflict:
                            conflicts.Add(key);
                            choices.Add(new FileConflict(pair.Marker, key, hash, file));
                            continue;
                        case SyncAction.TrashRemote:
                            activity?.Start(UiText.Get("移到云端回收站"), key);
                            await VerifyRemoteRootAsync(cancel);
                            await ResolveRemoteAsync(key, file!, false, cancel);
                            await RequireLocalAsync(key, null, cancel);
                            await drive.TrashAsync(file!, cancel);
                            activity?.Finish(UiText.Get("完成"));
                            removed++;
                            dirty |= baseline.Remove(key);
                            continue;
                        case SyncAction.RecoverLocal:
                            activity?.Start(UiText.Get("移到本地恢复目录"), key);
                            await VerifyRemoteRootAsync(cancel);
                            await ResolveRemoteAsync(key, null, false, cancel);
                            await RequireLocalAsync(key, hash, cancel);
                            MoveToRecovery(key);
                            activity?.Finish(UiText.Get("完成"));
                            removed++;
                            dirty |= baseline.Remove(key);
                            continue;
                        case SyncAction.Upload:
                        case SyncAction.Download:
                        case SyncAction.CompareContents:
                            pending.Add(new FileTransfer(key, action, here, file, previous));
                            break;
                        default:
                            throw new InvalidOperationException(UiText.Get("未知的同步操作。"));
                    }
                }

                attempt.Stage = "TransferFiles";
                var work = queue.Reconcile(pending, scope == null && deferred.Count == 0 ? null :
                    key => (scope != null && !scope.Covers(key)) || BlockedPath(deferred, key), scope);
                activity?.SetPending(queue);
                await transfers.RunAsync(work, (entry, token) => TransferFileAsync(entry.File, queue.Files(entry), token), completed =>
                {
                    var item = completed.Item;
                    finished.Add(item.Key);
                    if (completed.Baseline == null)
                    {
                        conflicts.Add(item.Key);
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

                // These trees were blocked during the file pass. Trash only after rechecking their contents.
                attempt.Stage = "TrashDirectories";
                foreach (var directory in directoryTrash)
                {
                    if (BlockedPath(deferred, directory.Key))
                    {
                        continue;
                    }

                    if (await TryTrashDirectoryAsync(directory, baseline, rules, cancel))
                    {
                        trashedDirectories.Add(directory.Key);
                        removed++;
                    }
                    else
                    {
                        conflicts.Add(directory.Key);
                    }
                }
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                activity?.Finish(UiText.Get("已取消"));
                throw;
            }
            catch
            {
                activity?.Finish(UiText.Get("失败"));
                throw;
            }
            finally
            {
                // If checkpointing itself fails, it is the exception the caller will receive.
                string previousStage = attempt.Stage;
                attempt.Stage = "Checkpoint";
                if (trashedDirectories.Count > 0)
                {
                    foreach (string key in baseline.Keys.Where(key => BlockedPath(trashedDirectories, key)).ToArray())
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

            return new PairSyncResult(uploaded, downloaded, removed, conflicts.ToArray(), choices, deferred.Count);
        }
    }

    internal async Task ResolveConflictAsync(FileConflict conflict, ConflictChoice choice, CancellationToken cancel)
    {
        PairInventory.ValidateKey(conflict.Key);
        if (conflict.PairId != pair.Marker || !Enum.IsDefined(choice))
        {
            throw new InvalidOperationException(UiText.Get("冲突记录不属于当前配对。"));
        }

        using (store.Lock(pair))
        {
            FolderPairStore.Verify(pair);
            var rules = new PairIgnoreRules(store.ReadRules(pair));
            if (rules.Excludes(conflict.Key))
            {
                throw new IOException(UiText.Get("此路径已被忽略，未处理。"));
            }

            await VerifyRemoteRootAsync(cancel);
            string key = conflict.Key;
            string path = Path.Combine(pair.Local, key);
            string? hash = conflict.LocalHash;
            var file = conflict.Remote;
            await ResolveRemoteAsync(key, file, false, cancel);
            await RequireLocalAsync(key, hash, cancel);
            var baseline = store.ReadBaseline(pair);
            if (choice == ConflictChoice.KeepBoth)
            {
                if (hash == null || file == null)
                {
                    throw new IOException(UiText.Get("只有两端都存在的普通文件才能保留两份。"));
                }

                string extension = Path.GetExtension(key);
                string copyKey = PairKey.Combine(PairKey.Parent(key) ?? "", "local-copy-" + Guid.NewGuid().ToString("N") + extension);
                if (rules.Excludes(copyKey))
                {
                    throw new IOException(UiText.Get("本地副本将被忽略，未处理。"));
                }

                await ResolveRemoteAsync(copyKey, null, false, cancel);
                string temp = Path.Combine(Path.GetDirectoryName(path)!, $".cloudreve-{Guid.NewGuid():N}.part");
                try
                {
                    File.Copy(path, temp, false);
                    if (await PairInventory.HashAsync(temp, cancel) != hash)
                    {
                        throw new IOException(UiText.Get("本地文件已变化，未覆盖。"));
                    }

                    File.Move(temp, Path.Combine(pair.Local, copyKey), false);
                }
                finally
                {
                    FolderPairStore.CheckDirectory(Path.GetDirectoryName(temp)!);
                    File.Delete(temp);
                }
            }

            if (choice == ConflictChoice.KeepLocal && hash != null)
            {
                string target = await ResolveRemoteAsync(key, file, true, cancel);
                await RequireLocalAsync(key, hash, cancel);
                await drive.UploadAsync(path, target, file, cancel);
                file = await PairInventory.ReadVersionAsync(drive, target, cancel);
                if (await CompareRemoteHashAsync(key, file, hash, activity, cancel) != hash)
                {
                    throw new IOException(UiText.Get("上传回读与本地内容不一致，未记录为已解决。"));
                }

                baseline[key] = new FileBaseline(hash, file.Version!, LocalFile.Identity(path), file.Id);
            }
            else if (choice == ConflictChoice.KeepLocal && file != null)
            {
                await drive.TrashAsync(file, cancel);
                baseline.Remove(key);
            }
            else if (file != null)
            {
                var received = await InstallDownloadAsync(key, file, hash, activity, cancel);
                baseline[key] = new FileBaseline(received.Hash!, file.Version!, received.LocalId, file.Id, LocalGeneration: received.LocalGeneration);
            }
            else if (hash != null)
            {
                MoveToRecovery(key);
                baseline.Remove(key);
            }

            store.SaveBaseline(pair, baseline);
        }
    }

    private async Task VerifyRemoteRootAsync(CancellationToken cancel)
    {
        if (await drive.FolderIdAsync(pair.Remote, cancel) != pair.RemoteId)
        {
            throw new IOException(UiText.Get("配对的云端目录已被替换；请核对后重新配对。"));
        }
    }

    private async Task VerifyVersionAsync(DriveEntry expected, CancellationToken cancel)
    {
        var current = await PairInventory.ReadVersionAsync(drive, expected.Uri, cancel);
        if (current.Version != expected.Version || expected.Id != null && current.Id != expected.Id)
        {
            throw new IOException(UiText.Get("云端文件在同步期间已变化，将重新扫描。"));
        }
    }

    // Resolve each segment from its parent. Missing intermediate directories can be created for an upload only.
    private async Task<string> ResolveRemoteAsync(string key, DriveEntry? expected, bool createParents, CancellationToken cancel, bool requireParents = false, string? uploadSession = null)
    {
        string parent = pair.Remote;
        string[] parts = key.Split('/');
        for (int i = 0; i < parts.Length; i++)
        {
            string name = parts[i];
            string child = DriveClient.ChildUri(parent, name);
            var siblings = requireParents ? await drive.ListForUploadAsync(parent, cancel) : await drive.ListAsync(parent, cancel);
            var matches = siblings.Where(item => string.Equals(item.Name.Normalize(), name, StringComparison.OrdinalIgnoreCase)).ToArray();
            var item = matches.FirstOrDefault();
            bool ownUpload = i == parts.Length - 1 && uploadSession != null && item?.UploadSessionId == uploadSession;
            if (matches.Length > 1 || item != null && (item.SkipSync && !ownUpload || item.Name.Normalize() != name))
            {
                throw new IOException(UiText.Get("云端名称冲突或条目类型已变化。"));
            }

            if (item != null)
            {
                PairInventory.ValidateRemote(parent, item);
                child = item.Uri;
            }

            if (i == parts.Length - 1)
            {
                if (ownUpload)
                {
                    var placeholder = await drive.StatAsync(child, cancel);
                    if (placeholder.UploadSessionId != uploadSession || !PairInventory.SameUri(placeholder.Uri, child) ||
                        !MatchesPreviousVersion(placeholder, expected))
                    {
                        throw new IOException(UiText.Get("云端文件在同步期间已变化，将重新扫描。"));
                    }

                    return child;
                }

                if ((item == null) != (expected == null) || item?.IsFolder == true)
                {
                    throw new IOException(UiText.Get("云端文件在同步期间已变化。"));
                }

                if (expected != null)
                {
                    await VerifyVersionAsync(expected, cancel);
                    return expected.Uri;
                }

                return child;
            }

            if (item == null)
            {
                if (!createParents)
                {
                    if (expected != null || requireParents)
                    {
                        throw new IOException(UiText.Get("云端父目录在同步期间已变化。"));
                    }

                    return child;
                }

                FolderPairStore.Verify(pair);
                await drive.CreateFolderAsync(parent, name, cancel);
            }
            else if (!item.IsFolder)
            {
                throw new IOException(UiText.Get("云端父目录已被文件替换。"));
            }

            parent = child;
        }

        throw new InvalidDataException(UiText.Get("同步路径为空。"));
    }

    private async Task RequireLocalAsync(string key, string? expectedHash, CancellationToken cancel, PairNode? scanned = null)
    {
        FolderPairStore.Verify(pair);
        cancel.ThrowIfCancellationRequested();
        string path = pair.Local;
        string[] parts = key.Split('/');
        for (int i = 0; i < parts.Length; i++)
        {
            path = Path.Combine(path, parts[i]);
            var kind = LocalFile.Inspect(path);
            if (kind == null && expectedHash == null)
            {
                return;
            }

            if (i == parts.Length - 1 && kind == NodeKind.File && expectedHash != null)
            {
                if (scanned?.Hash == expectedHash && scanned.LocalGeneration != 0 &&
                    LocalFile.GetContentVersion(path) is { } current && current.LocalId == scanned.LocalId && current.Generation == scanned.LocalGeneration)
                {
                    return;
                }
                if (await PairInventory.HashAsync(path, cancel) == expectedHash)
                {
                    return;
                }
            }

            if (kind != NodeKind.Directory || i == parts.Length - 1)
            {
                break;
            }
        }

        throw new IOException(UiText.Get("本地文件在同步期间已变化，将重新扫描。"));
    }

    private void EnsureLocalDirectory(string relative)
    {
        FolderPairStore.Verify(pair);
        string parent = pair.Local;
        foreach (string name in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            parent = Path.Combine(parent, name);
            if (LocalFile.Inspect(parent) == null)
            {
                Directory.CreateDirectory(parent);
                if (parent == Path.Combine(pair.Local, PairInventory.Recovery))
                {
                    LocalFile.Hide(parent);
                }
            }

            FolderPairStore.CheckDirectory(parent);
        }
    }

    private string RecoveryPath(string key)
    {
        string backup = PairKey.Combine(PairKey.Combine(PairInventory.Recovery, Guid.NewGuid().ToString("N")), key);
        EnsureLocalDirectory(PairKey.Parent(backup)!);
        FolderPairStore.CheckDirectory(Path.GetDirectoryName(Path.Combine(pair.Local, key))!);
        return Path.Combine(pair.Local, backup);
    }

    private void MoveToRecovery(string key)
    {
        File.Move(Path.Combine(pair.Local, key), RecoveryPath(key));
    }

    private async Task<string> CompareRemoteHashAsync(string key, DriveEntry file, string? expectedHash, SyncActivity? progress, CancellationToken cancel,
        PairNode? scanned = null)
    {
        await RequireLocalAsync(key, expectedHash, cancel, scanned);
        string hash = await drive.HashDownloadAsync(file, cancel, progress);
        await VerifyVersionAsync(file, cancel);
        await RequireLocalAsync(key, expectedHash, cancel, scanned);
        return hash;
    }

    private async Task<PairNode> InstallDownloadAsync(string key, DriveEntry file, string? expectedHash, SyncActivity? progress, CancellationToken cancel, TransferFiles? files = null)
    {
        string parent = PairKey.Parent(key) ?? "";
        await RequireLocalAsync(key, expectedHash, cancel);
        EnsureLocalDirectory(parent);
        string path = Path.Combine(pair.Local, key);
        string temp = Path.Combine(Path.GetDirectoryName(path)!, $".cloudreve-{Guid.NewGuid():N}.part");
        try
        {
            string? downloadedHash = null;
            if (files == null)
            {
                await drive.DownloadAsync(file, temp, cancel, progress);
            }
            else
            {
                downloadedHash = await drive.StageDownloadAsync(file, files, temp, cancel, progress);
            }

            var received = await PairInventory.ReadLocalFileAsync(key, temp, null, cancel);
            if (downloadedHash != null && received.Hash != downloadedHash)
            {
                throw new InvalidDataException(UiText.Get("文件在同步期间已变化。"));
            }

            await VerifyRemoteRootAsync(cancel);
            await VerifyVersionAsync(file, cancel);
            await RequireLocalAsync(key, expectedHash, cancel);
            if (expectedHash != null)
            {
                File.Replace(temp, path, RecoveryPath(key));
            }
            else
            {
                File.Move(temp, path, false);
            }

            string? installedId = LocalFile.Identity(path);
            return installedId == received.LocalId ? received : received with { LocalId = installedId, LocalGeneration = 0 };
        }
        finally
        {
            FolderPairStore.CheckDirectory(Path.GetDirectoryName(temp)!);
            File.Delete(temp);
        }
    }
}
