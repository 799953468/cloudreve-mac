using Cloudreve.Sync;

namespace Cloudreve.Desktop;

internal sealed partial class FolderPairSync
{
    private static bool BlockedPath(HashSet<string> blocked, string key)
    {
        for (string? path = key; !string.IsNullOrEmpty(path); path = Path.GetDirectoryName(path))
        {
            if (blocked.Contains(path))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<bool> SyncDirectoriesAsync(PairInventory local, PairInventory remote, Dictionary<string, FileBaseline> baseline,
        PairIgnoreRules rules, HashSet<string> conflicts, HashSet<string> blocked, List<PairNode> directoryTrash, CancellationToken cancel)
    {
        ProtectMovedTrees(local, remote, baseline, conflicts, blocked);
        var keys = baseline.Where(entry => entry.Value.IsDirectory).Select(entry => entry.Key)
            .Concat(local.Nodes.Values.Concat(remote.Nodes.Values).Where(node => node.Kind == NodeKind.Directory).Select(node => node.Key))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        bool dirty = false;
        foreach (string key in keys)
        {
            cancel.ThrowIfCancellationRequested();
            if (rules.Excludes(key, true))
            {
                dirty |= baseline.Remove(key);
                continue;
            }

            if (BlockedPath(blocked, key))
            {
                continue;
            }

            baseline.TryGetValue(key, out var previous);
            local.Nodes.TryGetValue(key, out var here);
            remote.Nodes.TryGetValue(key, out var there);
            if (!SafeMovePath(local, key) || !SafeMovePath(remote, key) || previous?.IsDirectory == false ||
                here != null && here.Kind != NodeKind.Directory || there != null && there.Kind != NodeKind.Directory ||
                here != null && previous?.LocalId != null && here.LocalId != previous.LocalId ||
                there != null && previous?.RemoteId != null && there.Remote?.Id != previous.RemoteId)
            {
                conflicts.Add(key);
                blocked.Add(key);
                continue;
            }

            if (here == null && there == null)
            {
                dirty |= baseline.Remove(key);
                continue;
            }

            string path = Path.Combine(pair.Local, key);
            if (previous != null && here == null)
            {
                blocked.Add(key);
                if (previous.LocalId != null && !string.IsNullOrEmpty(previous.RemoteId) && there!.Remote!.Id == previous.RemoteId)
                {
                    directoryTrash.Add(there);
                }
                else
                {
                    conflicts.Add(key);
                }

                continue;
            }

            if (previous != null && there == null)
            {
                FolderPairStore.Verify(pair);
                if (here != null && previous.LocalId != null && MacFile.Identity(path) == previous.LocalId)
                {
                    FolderPairStore.CheckDirectory(path);
                    if (!Directory.EnumerateFileSystemEntries(path).Any())
                    {
                        await VerifyRemoteRootAsync(cancel);
                        await ResolveRemoteAsync(key, null, false, cancel);
                        FolderPairStore.Verify(pair);
                        FolderPairStore.CheckDirectory(path);
                        if (MacFile.Identity(path) == previous.LocalId)
                        {
                            Directory.Delete(path, false);
                            local.Nodes.Remove(key);
                            dirty |= baseline.Remove(key);
                            continue;
                        }
                    }
                }

                conflicts.Add(key);
                blocked.Add(key);
                continue;
            }

            if (here == null || there == null)
            {
                FolderPairStore.Verify(pair);
                await VerifyRemoteRootAsync(cancel);
                string name = Path.GetFileName(key);
                string relativeParent = Path.GetDirectoryName(key) ?? "";
                string parent = relativeParent.Length == 0 ? pair.Remote : remote.Nodes[relativeParent].Remote!.Uri;
                if (there == null)
                {
                    FolderPairStore.CheckDirectory(path);
                    if (MacFile.Identity(path) != here!.LocalId)
                    {
                        throw new IOException(UiText.Get("本地目录在同步期间已变化。"));
                    }

                    await ResolveRemoteAsync(key, null, false, cancel);
                    await drive.CreateFolderAsync(parent, name, cancel);
                    var directory = await drive.StatAsync(DriveClient.ChildUri(parent, name), cancel);
                    PairInventory.ValidateRemote(parent, directory);
                    if (!directory.IsFolder || directory.SkipSync || directory.Name.Normalize() != name)
                    {
                        throw new IOException(UiText.Get("无法确认新建的云端目录。"));
                    }

                    there = new PairNode(key, NodeKind.Directory, Remote: directory);
                    remote.Nodes.Add(key, there);
                }
                else
                {
                    if (string.IsNullOrEmpty(there.Remote!.Id) || await drive.FolderIdAsync(there.Remote.Uri, cancel) != there.Remote.Id)
                    {
                        throw new IOException(UiText.Get("云端目录在同步期间已变化。"));
                    }

                    await RequireLocalAsync(key, null, cancel);
                    EnsureLocalDirectory(key);
                    here = new PairNode(key, NodeKind.Directory, LocalId: MacFile.Identity(path));
                    local.Nodes.Add(key, here);
                }
            }

            var current = new FileBaseline("", "", here!.LocalId, there!.Remote!.Id, true);
            if (current != previous)
            {
                baseline[key] = current;
                dirty = true;
            }
        }

        return dirty;
    }

    private static void ProtectMovedTrees(PairInventory local, PairInventory remote, Dictionary<string, FileBaseline> baseline,
        HashSet<string> conflicts, HashSet<string> blocked)
    {
        var missing = baseline.Where(entry => entry.Value.IsDirectory && !local.Nodes.ContainsKey(entry.Key) && remote.Nodes.ContainsKey(entry.Key))
            .Select(entry => entry.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (missing.Count == 0)
        {
            return;
        }

        // A directory rename, or moving a child out before deleting its parent, is not an unambiguous deletion.
        var localById = local.Nodes.Values.Where(node => node.LocalId != null).ToLookup(node => node.LocalId!);
        foreach (var (key, previous) in baseline)
        {
            if (previous.LocalId == null || local.Nodes.ContainsKey(key))
            {
                continue;
            }

            foreach (var moved in localById[previous.LocalId])
            {
                for (string? ancestor = key; !string.IsNullOrEmpty(ancestor); ancestor = Path.GetDirectoryName(ancestor))
                {
                    if (missing.Contains(ancestor))
                    {
                        blocked.Add(ancestor);
                        conflicts.Add(ancestor);
                        blocked.Add(moved.Key);
                        conflicts.Add(moved.Key);
                    }
                }
            }
        }
    }

    private async Task<bool> TryTrashDirectoryAsync(PairNode directory, Dictionary<string, FileBaseline> baseline, PairIgnoreRules rules, CancellationToken cancel)
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
        string? parent = Path.GetDirectoryName(directory.Key);
        if (!string.IsNullOrEmpty(parent) && (!baseline.TryGetValue(parent, out var knownParent) || knownParent.LocalId == null ||
                                              MacFile.Identity(Path.Combine(pair.Local, parent)) != knownParent.LocalId))
        {
            return false;
        }

        if (MacFile.Inspect(path) != null)
        {
            return false;
        }

        cancel.ThrowIfCancellationRequested();
        activity?.Start(UiText.Get("移到云端回收站"), directory.Key);
        await drive.TrashAsync(target, cancel);
        activity?.Finish(UiText.Get("完成"));
        return true;
    }
}
