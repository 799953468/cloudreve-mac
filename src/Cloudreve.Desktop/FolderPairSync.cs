using Cloudreve.Sync;

namespace Cloudreve.Desktop;

internal sealed record PairSyncResult(int Uploaded, int Downloaded, int Removed, IReadOnlyList<string> Conflicts);

internal sealed class FolderPairSync(FolderPairStore store, FolderPair pair, DriveClient drive)
{
    internal async Task<PairSyncResult> RunAsync(CancellationToken cancel)
    {
        using (store.Lock(pair))
        {
            FolderPairStore.Verify(pair);
            var baseline = store.ReadBaseline(pair);
            foreach (string key in baseline.Keys)
            {
                PairInventory.ValidateKey(key);
            }

            var local = await PairInventory.ReadLocalAsync(pair, cancel);
            await VerifyRemoteRootAsync(cancel);
            var remote = await PairInventory.ReadRemoteAsync(pair, drive, cancel);
            await VerifyRemoteRootAsync(cancel);
            FolderPairStore.Verify(pair);

            // Visit parents first. A collision or an unsupported node also protects its descendants.
            var conflicts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var keys = baseline.Keys.Concat(local.Nodes.Keys).Concat(remote.Nodes.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase);
            int uploaded = 0, downloaded = 0, removed = 0;
            bool dirty = false;
            try
            {
                foreach (string key in keys.ToArray())
                {
                    cancel.ThrowIfCancellationRequested();
                    string parent = Path.GetDirectoryName(key) ?? "";
                    if (blocked.Contains(parent))
                    {
                        blocked.Add(key);
                        continue;
                    }

                    baseline.TryGetValue(key, out var previous);
                    local.Nodes.TryGetValue(key, out var here);
                    remote.Nodes.TryGetValue(key, out var there);
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
                        continue;
                    }

                    FolderPairStore.Verify(pair);
                    string path = Path.Combine(pair.Local, key);
                    switch (action)
                    {
                        case SyncAction.Forget:
                            dirty |= baseline.Remove(key);
                            continue;
                        case SyncAction.Conflict:
                            conflicts.Add(key);
                            continue;
                        case SyncAction.TrashRemote:
                            await VerifyRemoteRootAsync(cancel);
                            await ResolveRemoteAsync(key, file!, false, cancel);
                            await RequireLocalAsync(key, null, cancel);
                            await drive.TrashAsync(file!, cancel);
                            removed++;
                            dirty |= baseline.Remove(key);
                            continue;
                        case SyncAction.RecoverLocal:
                            await VerifyRemoteRootAsync(cancel);
                            await ResolveRemoteAsync(key, null, false, cancel);
                            await RequireLocalAsync(key, hash, cancel);
                            MoveToRecovery(key);
                            removed++;
                            dirty |= baseline.Remove(key);
                            continue;
                        case SyncAction.Upload:
                            string target = await ResolveRemoteAsync(key, file, true, cancel);
                            await RequireLocalAsync(key, hash, cancel);
                            await drive.UploadAsync(path, target, file, cancel);
                            // Upload completion does not identify the stored entity. Read it back before checkpointing.
                            file = await PairInventory.ReadVersionAsync(drive, target, cancel);
                            if (await ReceiveAsync(key, file, hash, false, cancel) != hash)
                            {
                                throw new IOException("云端回读与上传前的文件内容不同，未记录为已同步。");
                            }

                            uploaded++;
                            break;
                        case SyncAction.Download:
                            hash = await ReceiveAsync(key, file!, hash, true, cancel);
                            downloaded++;
                            break;
                        case SyncAction.CompareContents:
                            if (await ReceiveAsync(key, file!, hash, false, cancel) != hash)
                            {
                                conflicts.Add(key);
                                continue;
                            }

                            break;
                        default:
                            throw new InvalidOperationException("未知的同步操作。");
                    }

                    baseline[key] = new FileBaseline(hash!, file!.Version!);
                    dirty = true;
                }
            }
            finally
            {
                if (dirty)
                {
                    store.SaveBaseline(pair, baseline);
                }
            }

            return new PairSyncResult(uploaded, downloaded, removed, conflicts.ToArray());
        }
    }

    private async Task VerifyRemoteRootAsync(CancellationToken cancel)
    {
        if (await drive.FolderIdAsync(pair.Remote, cancel) != pair.RemoteId)
        {
            throw new IOException("配对的云端目录已被替换；请核对后重新配对。");
        }
    }

    private async Task VerifyVersionAsync(DriveEntry expected, CancellationToken cancel)
    {
        var current = await PairInventory.ReadVersionAsync(drive, expected.Uri, cancel);
        if (current.Version != expected.Version)
        {
            throw new IOException("云端文件在同步期间已变化，将重新扫描。");
        }
    }

    // Resolve each segment from its parent. Missing intermediate directories can be created for an upload only.
    private async Task<string> ResolveRemoteAsync(string key, DriveEntry? expected, bool createParents, CancellationToken cancel)
    {
        string parent = pair.Remote;
        string[] parts = key.Split('/');
        for (int i = 0; i < parts.Length; i++)
        {
            string name = parts[i];
            string child = DriveClient.ChildUri(parent, name);
            var siblings = await drive.ListAsync(parent, cancel);
            var matches = siblings.Where(item => string.Equals(item.Name.Normalize(), name, StringComparison.OrdinalIgnoreCase)).ToArray();
            var item = matches.FirstOrDefault();
            if (matches.Length > 1 || item != null && (item.SkipSync || item.Name.Normalize() != name))
            {
                throw new IOException("云端名称冲突或条目类型已变化。");
            }

            if (item != null)
            {
                PairInventory.ValidateRemote(parent, item);
                child = item.Uri;
            }

            if (i == parts.Length - 1)
            {
                if ((item == null) != (expected == null) || item?.IsFolder == true)
                {
                    throw new IOException("云端文件在同步期间已变化。");
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
                    if (expected != null)
                    {
                        throw new IOException("云端父目录在同步期间已变化。");
                    }

                    return child;
                }

                FolderPairStore.Verify(pair);
                await drive.CreateFolderAsync(parent, name, cancel);
            }
            else if (!item.IsFolder)
            {
                throw new IOException("云端父目录已被文件替换。");
            }

            parent = child;
        }

        throw new InvalidDataException("同步路径为空。");
    }

    private async Task RequireLocalAsync(string key, string? expectedHash, CancellationToken cancel)
    {
        FolderPairStore.Verify(pair);
        cancel.ThrowIfCancellationRequested();
        string path = pair.Local;
        string[] parts = key.Split('/');
        for (int i = 0; i < parts.Length; i++)
        {
            path = Path.Combine(path, parts[i]);
            var kind = MacFile.Inspect(path);
            if (kind == null && expectedHash == null)
            {
                return;
            }

            if (i == parts.Length - 1 && kind == NodeKind.File && expectedHash != null && await PairInventory.HashAsync(path, cancel) == expectedHash)
            {
                return;
            }

            if (kind != NodeKind.Directory || i == parts.Length - 1)
            {
                break;
            }
        }

        throw new IOException("本地文件在同步期间已变化，将重新扫描。");
    }

    private void EnsureLocalDirectory(string relative)
    {
        FolderPairStore.Verify(pair);
        string parent = pair.Local;
        foreach (string name in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            parent = Path.Combine(parent, name);
            if (MacFile.Inspect(parent) == null)
            {
                Directory.CreateDirectory(parent);
            }

            FolderPairStore.CheckDirectory(parent);
        }
    }

    private string RecoveryPath(string key)
    {
        string backup = Path.Combine(PairInventory.Recovery, Guid.NewGuid().ToString("N"), key);
        EnsureLocalDirectory(Path.GetDirectoryName(backup)!);
        FolderPairStore.CheckDirectory(Path.GetDirectoryName(Path.Combine(pair.Local, key))!);
        return Path.Combine(pair.Local, backup);
    }

    private void MoveToRecovery(string key)
    {
        File.Move(Path.Combine(pair.Local, key), RecoveryPath(key));
    }

    private async Task<string> ReceiveAsync(string key, DriveEntry file, string? expectedHash, bool install, CancellationToken cancel)
    {
        string parent = Path.GetDirectoryName(key) ?? "";
        await RequireLocalAsync(key, expectedHash, cancel);
        EnsureLocalDirectory(parent);
        string path = Path.Combine(pair.Local, key);
        string temp = Path.Combine(Path.GetDirectoryName(path)!, $".cloudreve-{Guid.NewGuid():N}.part");
        try
        {
            await drive.DownloadAsync(file, temp, cancel);
            string hash = await PairInventory.HashAsync(temp, cancel);
            await VerifyVersionAsync(file, cancel);
            await RequireLocalAsync(key, expectedHash, cancel);
            if (install)
            {
                if (expectedHash != null)
                {
                    File.Replace(temp, path, RecoveryPath(key));
                }
                else
                {
                    File.Move(temp, path, false);
                }
            }

            return hash;
        }
        finally
        {
            FolderPairStore.CheckDirectory(Path.GetDirectoryName(temp)!);
            File.Delete(temp);
        }
    }
}
