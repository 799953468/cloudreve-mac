using Cloudreve.Sync;

namespace Cloudreve.Desktop;

internal sealed record FileConflict(Guid PairId, string Key, string? LocalHash, DriveEntry? Remote);

internal enum ConflictChoice
{
    KeepLocal,
    KeepRemote,
    KeepBoth
}

internal sealed record PairSyncResult(int Uploaded, int Downloaded, int Removed, IReadOnlyList<string> Conflicts, IReadOnlyList<FileConflict> Choices);

internal sealed partial class FolderPairSync(FolderPairStore store, FolderPair pair, DriveClient drive, SyncActivity? activity = null)
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

            var rules = new PairIgnoreRules(store.ReadRules(pair));
            var local = await PairInventory.ReadLocalAsync(pair, rules, cancel);
            await VerifyRemoteRootAsync(cancel);
            var remote = await PairInventory.ReadRemoteAsync(pair.Remote, "", drive, rules, cancel);
            await VerifyRemoteRootAsync(cancel);
            FolderPairStore.Verify(pair);

            // Visit parents first. A collision or an unsupported node also protects its descendants.
            var conflicts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var choices = new List<FileConflict>();
            var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var directoryTrash = new List<PairNode>();
            var trashedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int uploaded = 0, downloaded = 0, removed = 0;
            bool dirty = false;
            try
            {
                dirty = await SyncDirectoriesAsync(local, remote, baseline, rules, conflicts, blocked, directoryTrash, cancel);
                dirty |= await ApplyFileMovesAsync(local, remote, baseline, rules, conflicts, blocked, cancel);
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
                        if (previous!.LocalId != here!.LocalId || previous.RemoteId != file!.Id)
                        {
                            baseline[key] = previous with { LocalId = here.LocalId, RemoteId = file!.Id };
                            dirty = true;
                        }

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
                            activity?.Start(UiText.Get("上传"), key);
                            string target = await ResolveRemoteAsync(key, file, true, cancel);
                            await RequireLocalAsync(key, hash, cancel);
                            await drive.UploadAsync(path, target, file, cancel, activity);
                            // Upload completion does not identify the stored entity. Read it back before checkpointing.
                            file = await PairInventory.ReadVersionAsync(drive, target, cancel);
                            if (await ReceiveAsync(key, file, hash, false, cancel) != hash)
                            {
                                throw new IOException(UiText.Get("云端回读与上传前的文件内容不同，未记录为已同步。"));
                            }

                            uploaded++;
                            break;
                        case SyncAction.Download:
                            activity?.Start(UiText.Get("下载"), key);
                            hash = await ReceiveAsync(key, file!, hash, true, cancel);
                            downloaded++;
                            break;
                        case SyncAction.CompareContents:
                            activity?.Start(UiText.Get("核对内容"), key);
                            if (await ReceiveAsync(key, file!, hash, false, cancel) != hash)
                            {
                                conflicts.Add(key);
                                activity?.Finish(UiText.Get("冲突"));
                                choices.Add(new FileConflict(pair.Marker, key, hash, file));
                                continue;
                            }

                            break;
                        default:
                            throw new InvalidOperationException(UiText.Get("未知的同步操作。"));
                    }

                    baseline[key] = new FileBaseline(hash!, file!.Version!, MacFile.Identity(path), file.Id);
                    dirty = true;
                    activity?.Finish(UiText.Get("完成"));
                }

                // These trees were blocked during the file pass. Trash only after rechecking their contents.
                foreach (var directory in directoryTrash)
                {
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
                if (trashedDirectories.Count > 0)
                {
                    foreach (string key in baseline.Keys.Where(key => BlockedPath(trashedDirectories, key)).ToArray())
                    {
                        baseline.Remove(key);
                    }
                }

                if (dirty || trashedDirectories.Count > 0)
                {
                    store.SaveBaseline(pair, baseline);
                }
            }

            return new PairSyncResult(uploaded, downloaded, removed, conflicts.ToArray(), choices);
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
                string copyKey = Path.Combine(Path.GetDirectoryName(key) ?? "", "local-copy-" + Guid.NewGuid().ToString("N") + extension);
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
                if (await ReceiveAsync(key, file, hash, false, cancel) != hash)
                {
                    throw new IOException(UiText.Get("上传回读与本地内容不一致，未记录为已解决。"));
                }

                baseline[key] = new FileBaseline(hash, file.Version!, MacFile.Identity(path), file.Id);
            }
            else if (choice == ConflictChoice.KeepLocal && file != null)
            {
                await drive.TrashAsync(file, cancel);
                baseline.Remove(key);
            }
            else if (file != null)
            {
                string received = await ReceiveAsync(key, file, hash, true, cancel);
                baseline[key] = new FileBaseline(received, file.Version!, MacFile.Identity(path), file.Id);
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
                throw new IOException(UiText.Get("云端名称冲突或条目类型已变化。"));
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
                    if (expected != null)
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

        throw new IOException(UiText.Get("本地文件在同步期间已变化，将重新扫描。"));
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
            await drive.DownloadAsync(file, temp, cancel, activity);
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