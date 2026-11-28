using Cloudreve.Sync;

namespace Cloudreve.Desktop;

internal sealed record CompletedTransfer(FileTransfer Item, FileBaseline? Baseline, bool Deferred = false);

// Every change the engine makes goes through these operations. Each one rechecks the copies it is about to touch right
// before acting, because the scans that produced the decision may already be stale: a mismatch throws instead of
// overwriting or deleting something the decision did not see.
internal sealed class PairOperations(FolderPair pair, DriveClient drive, SyncActivity? activity, AdaptiveWorkQueue remoteScan)
{
    internal FolderPair Pair
    {
        get { return pair; }
    }

    internal DriveClient Drive
    {
        get { return drive; }
    }

    internal async Task VerifyRemoteRootAsync(CancellationToken cancel)
    {
        if (await drive.FolderIdAsync(pair.Remote, cancel) != pair.RemoteId)
        {
            throw new IOException(UiText.Get("pair.remoteRootReplaced"));
        }
    }

    internal async Task VerifyVersionAsync(DriveEntry expected, CancellationToken cancel)
    {
        var current = await PairInventory.ReadVersionAsync(drive, expected.Uri, cancel);
        if (current.Version != expected.Version || expected.Id != null && current.Id != expected.Id)
        {
            throw new IOException(UiText.Get("sync.remoteFileChangedRescan"));
        }
    }

    internal static bool MatchesPreviousVersion(DriveEntry placeholder, DriveEntry? previous)
    {
        return previous == null || !previous.IsFolder && !previous.SkipSync && !string.IsNullOrEmpty(previous.Version) &&
               PairInventory.SameUri(previous.Uri, placeholder.Uri) && (previous.Id == null || previous.Id == placeholder.Id) && previous.Version == placeholder.Version;
    }

    // Resolve each segment from its parent. Missing intermediate directories can be created for an upload only.
    internal async Task<string> ResolveRemoteAsync(string key, DriveEntry? expected, bool createParents, CancellationToken cancel, bool requireParents = false,
        string? uploadSession = null)
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
                throw new IOException(UiText.Get("sync.remoteNameCollision"));
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
                        throw new IOException(UiText.Get("sync.remoteFileChangedRescan"));
                    }

                    return child;
                }

                if ((item == null) != (expected == null) || item?.IsFolder == true)
                {
                    throw new IOException(UiText.Get("sync.remoteFileChanged"));
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
                        throw new IOException(UiText.Get("sync.remoteParentChanged"));
                    }

                    return child;
                }

                FolderPairStore.Verify(pair);
                await drive.CreateFolderAsync(parent, name, cancel);
            }
            else if (!item.IsFolder)
            {
                throw new IOException(UiText.Get("sync.remoteParentReplacedByFile"));
            }

            parent = child;
        }

        throw new InvalidDataException(UiText.Get("sync.emptyPath"));
    }

    // Null expectedHash means the path must be absent. A scanned node with a content generation proves the file is
    // unchanged without reading it again.
    internal async Task RequireLocalAsync(string key, string? expectedHash, CancellationToken cancel, PairNode? scanned = null)
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

        throw new LocalFileChangedException(UiText.Get("sync.localFileChangedRescan"));
    }

    internal void EnsureLocalDirectory(string relative)
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

    internal string RecoveryPath(string key)
    {
        string backup = PairKey.Combine(PairKey.Combine(PairInventory.Recovery, Guid.NewGuid().ToString("N")), key);
        EnsureLocalDirectory(PairKey.Parent(backup)!);
        FolderPairStore.CheckDirectory(Path.GetDirectoryName(Path.Combine(pair.Local, key))!);
        return Path.Combine(pair.Local, backup);
    }

    internal void MoveToRecovery(string key)
    {
        File.Move(Path.Combine(pair.Local, key), RecoveryPath(key));
    }

    internal async Task<string> CompareRemoteHashAsync(string key, DriveEntry file, string? expectedHash, SyncActivity? progress, CancellationToken cancel,
        PairNode? scanned = null)
    {
        await RequireLocalAsync(key, expectedHash, cancel, scanned);
        string hash = await drive.HashDownloadAsync(file, cancel, progress);
        await VerifyVersionAsync(file, cancel);
        await RequireLocalAsync(key, expectedHash, cancel, scanned);
        return hash;
    }

    // Downloads next to the target, verifies both copies again, then installs atomically. A replaced local file goes to recovery.
    internal async Task<PairNode> InstallDownloadAsync(string key, DriveEntry file, string? expectedHash, SyncActivity? progress, CancellationToken cancel,
        TransferFiles? files = null)
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
                throw new InvalidDataException(UiText.Get("sync.fileChanged"));
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

    // Returns the first folder on the way to key that no longer matches the scan, or null when all of them still do.
    internal string? ChangedLocalDirectory(PairInventory local, string key)
    {
        FolderPairStore.Verify(pair);
        string relative = "";
        foreach (string name in key.Split('/'))
        {
            relative = PairKey.Combine(relative, name);
            string path = Path.Combine(pair.Local, relative);
            var kind = LocalFile.Inspect(path);
            if (local.Nodes.TryGetValue(relative, out var scanned))
            {
                if (kind != NodeKind.Directory || LocalFile.Identity(path) != scanned.LocalId)
                {
                    return relative;
                }
            }
            else if (kind != null || relative != key)
            {
                // Only the leaf may be absent: its parents were reconciled first.
                return relative;
            }
        }

        return null;
    }

    // Trashes a cloud folder deleted locally, but only while every item in it is one the baseline already knew.
    internal async Task<bool> TryTrashDirectoryAsync(PairNode directory, Dictionary<string, FileBaseline> baseline, PairIgnoreRules rules, CancellationToken cancel)
    {
        var target = directory.Remote!;
        var tree = await PairInventory.ReadRemoteAsync(target.Uri, directory.Key, drive, rules, remoteScan, cancel);
        if (tree.Excluded.Count != 0)
        {
            return false;
        }

        foreach (var node in tree.Nodes.Values)
        {
            if (node.Kind == NodeKind.Unsupported || !baseline.TryGetValue(node.Key, out var known) ||
                string.IsNullOrEmpty(known.RemoteId) || node.Remote!.Id != known.RemoteId ||
                known.IsDirectory != (node.Kind == NodeKind.Directory) || !known.IsDirectory && node.Remote.Version != known.RemoteVersion)
            {
                return false;
            }
        }

        await VerifyRemoteRootAsync(cancel);
        if (await drive.FolderIdAsync(target.Uri, cancel) != target.Id)
        {
            return false;
        }

        FolderPairStore.Verify(pair);
        string path = Path.Combine(pair.Local, directory.Key);
        FolderPairStore.CheckDirectory(Path.GetDirectoryName(path)!);
        string? parent = PairKey.Parent(directory.Key);
        if (!string.IsNullOrEmpty(parent) && (!baseline.TryGetValue(parent, out var knownParent) || knownParent.LocalId == null ||
                                              LocalFile.Identity(Path.Combine(pair.Local, parent)) != knownParent.LocalId))
        {
            return false;
        }

        if (LocalFile.Inspect(path) != null)
        {
            return false;
        }

        cancel.ThrowIfCancellationRequested();
        activity?.Start(UiText.Get("activity.moveToCloudTrash"), directory.Key);
        await drive.TrashAsync(target, cancel);
        activity?.Finish(UiText.Get("activity.done"));
        return true;
    }

    // A new upload and a version upload can both appear as an excluded placeholder in a fresh listing. Returns whether
    // the placeholder was this app's own: resumed as a pending upload, or cancelled when the copies moved on.
    internal async Task<bool> CollectOwnedUploadAsync(string key, DriveEntry placeholder, PairNode? here, FileBaseline? baseline, TransferQueue queue,
        List<FileTransfer> pending, CancellationToken cancel)
    {
        var entry = queue.Find(key);
        if (entry?.File.Action != SyncAction.Upload)
        {
            return false;
        }

        var files = queue.Files(entry);
        var saved = files.ReadUpload();
        var previous = entry.File.Remote;
        if (saved == null || placeholder.UploadSessionId != saved.SessionId || !MatchesPreviousVersion(placeholder, previous))
        {
            return false;
        }

        if (baseline == entry.File.Previous && here == entry.File.Local && here?.Hash != null &&
            SyncDecision.ForFile(baseline, here.Hash, previous?.Version) == SyncAction.Upload)
        {
            pending.Add(new FileTransfer(key, SyncAction.Upload, here, previous, baseline));
        }
        else
        {
            // Discard only our own stale session. A later scan will decide what the current copies need.
            await VerifyRemoteRootAsync(cancel);
            string target = await ResolveRemoteAsync(key, previous, false, cancel, requireParents: true, uploadSession: saved.SessionId);
            await drive.CancelUploadAsync(target, saved.SessionId, cancel);
        }

        return true;
    }

    // An upload placeholder untouched this long is abandoned, whichever client started it: upload sessions expire long before.
    internal static readonly TimeSpan StalePlaceholderAge = TimeSpan.FromHours(24);

    // Cancels another client's abandoned upload so the path can sync again. Cancelling removes a placeholder for a new
    // file and drops the pending version of an existing one. Returns false, changing nothing, while the upload may still be live.
    internal async Task<bool> TryClearStalePlaceholderAsync(string key, DriveEntry placeholder, DateTimeOffset now, CancellationToken cancel)
    {
        if (placeholder.UploadSessionId is not { } session || await drive.ModifiedAsync(placeholder.Uri, cancel) is not { } modified ||
            now - modified < StalePlaceholderAge)
        {
            return false;
        }

        await VerifyRemoteRootAsync(cancel);
        string target = await ResolveRemoteAsync(key, null, false, cancel, requireParents: true, uploadSession: session);
        await drive.CancelUploadAsync(target, session, cancel);
        Serilog.Log.Logger.ForContext("PairId", pair.Marker)
            .Information("Cancelled an abandoned upload at {ConflictKey}, last updated {CloudModified}", key, modified);
        return true;
    }

    // Runs one queued transfer. A verified result is written next to its checkpoint first, so a crash before the
    // baseline commit cannot lose it.
    internal async Task<CompletedTransfer> TransferFileAsync(FileTransfer item, TransferFiles files, CancellationToken cancel)
    {
        string key = item.Key;
        var here = item.Local;
        var file = item.Remote;
        var progress = activity?.CreateWorker();
        try
        {
            cancel.ThrowIfCancellationRequested();
            FolderPairStore.Verify(pair);
            switch (item.Action)
            {
                case SyncAction.Upload:
                    progress?.Start(UiText.Get("activity.upload"), key);
                    await VerifyRemoteRootAsync(cancel);
                    // Parent creation belongs to the serial directory pass, not competing sibling uploads.
                    var saved = files.ReadUpload();
                    string target = await ResolveRemoteAsync(key, file, false, cancel, requireParents: true, uploadSession: saved?.SessionId);
                    if (saved?.Expired == true)
                    {
                        await drive.CancelUploadAsync(target, saved.SessionId, cancel);
                        File.Delete(files.UploadPath);
                        saved = null;
                        target = await ResolveRemoteAsync(key, file, false, cancel, requireParents: true);
                    }

                    string path = Path.Combine(pair.Local, key);
                    await RequireLocalAsync(key, here!.Hash, cancel, here);
                    await drive.UploadAsync(path, target, file, cancel, progress, files, saved);
                    var uploaded = await drive.StatAsync(target, cancel);
                    // A resumed local/relay upload has no separate finalize call. Only replace our own stranded placeholder.
                    if (saved?.StorageKind == "local" && uploaded.UploadSessionId == saved.SessionId &&
                        PairInventory.SameUri(target, uploaded.Uri) && MatchesPreviousVersion(uploaded, file))
                    {
                        await VerifyRemoteRootAsync(cancel);
                        await drive.CancelUploadAsync(target, saved.SessionId, cancel);
                        File.Delete(files.UploadPath);
                        target = await ResolveRemoteAsync(key, file, false, cancel, requireParents: true);
                        await RequireLocalAsync(key, here.Hash, cancel, here);
                        await drive.UploadAsync(path, target, file, cancel, progress, files);
                        uploaded = await drive.StatAsync(target, cancel);
                    }

                    file = PairInventory.RequireVersion(uploaded, target);
                    if (await CompareRemoteHashAsync(key, file, here.Hash, progress, cancel, here) != here.Hash)
                    {
                        throw new IOException(UiText.Get("sync.uploadReadbackMismatch"));
                    }

                    break;
                case SyncAction.Download:
                    progress?.Start(UiText.Get("activity.download"), key);
                    here = await InstallDownloadAsync(key, file!, here?.Hash, progress, cancel, files);
                    break;
                case SyncAction.CompareContents:
                    progress?.Start(UiText.Get("activity.compare"), key);
                    if (await CompareRemoteHashAsync(key, file!, here!.Hash, progress, cancel, here) != here.Hash)
                    {
                        progress?.Finish(UiText.Get("activity.conflict"));
                        return new CompletedTransfer(item, null);
                    }

                    break;
                default:
                    throw new InvalidOperationException(UiText.Get("sync.unknownAction"));
            }

            var baseline = new FileBaseline(here.Hash!, file!.Version!, here.LocalId, file.Id, LocalGeneration: here.LocalGeneration);
            FolderPairStore.WriteJson(files.VerifiedPath, baseline, DesktopJson.Default.FileBaseline);
            progress?.Finish(UiText.Get("activity.done"));
            return new CompletedTransfer(item, baseline);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            progress?.Finish(UiText.Get("activity.cancelled"));
            throw;
        }
        catch (LocalFileChangedException)
        {
            // Failing the pass would restart its whole scan; a file that is still being written could repeat that indefinitely.
            progress?.Finish(UiText.Get("activity.changedWillRetry"));
            return new CompletedTransfer(item, null, Deferred: true);
        }
        catch
        {
            progress?.Finish(UiText.Get("activity.failed"));
            throw;
        }
    }
}
