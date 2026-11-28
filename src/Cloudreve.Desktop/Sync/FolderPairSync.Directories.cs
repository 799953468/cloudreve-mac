using Cloudreve.Sync;

namespace Cloudreve.Desktop;

internal sealed partial class FolderPairSync
{
    private static bool BlockedPath(HashSet<string> blocked, string key)
    {
        for (string? path = key; !string.IsNullOrEmpty(path); path = PairKey.Parent(path))
        {
            if (blocked.Contains(path))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<bool> SyncDirectoriesAsync(PairInventory local, PairInventory remote, Dictionary<string, FileBaseline> baseline,
        PairIgnoreRules rules, HashSet<string> conflicts, HashSet<string> blocked, HashSet<string> deferred, List<PairNode> directoryTrash, CancellationToken cancel)
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
                if (previous.LocalId != null)
                {
                    activity?.MarkSyncWork();
                    await VerifyRemoteRootAsync(cancel);
                    await ResolveRemoteAsync(key, null, false, cancel);
                    if (DeferIfChanged(key))
                    {
                        continue;
                    }

                    var removal = LocalFile.ChangeDirectoryEntry(path, create: false);
                    if (removal == DirectoryChangeResult.Applied)
                    {
                        local.Nodes.Remove(key);
                        dirty |= baseline.Remove(key);
                        continue;
                    }

                    if (removal == DirectoryChangeResult.PathChanged)
                    {
                        DeferIfChanged(key, operationRaced: true);
                        continue;
                    }
                }

                // A non-empty local directory or one without a verified identity is never removed.
                conflicts.Add(key);
                blocked.Add(key);
                continue;
            }

            if (here == null || there == null)
            {
                activity?.MarkSyncWork();
                FolderPairStore.Verify(pair);
                await VerifyRemoteRootAsync(cancel);
                string name = Path.GetFileName(key);
                string relativeParent = PairKey.Parent(key) ?? "";
                string parent = relativeParent.Length == 0 ? pair.Remote : remote.Nodes[relativeParent].Remote!.Uri;
                if (there == null)
                {
                    await ResolveRemoteAsync(key, null, false, cancel);
                    if (DeferIfChanged(key))
                    {
                        continue;
                    }

                    await drive.CreateFolderAsync(parent, name, cancel);
                    var directory = await drive.StatAsync(DriveClient.ChildUri(parent, name), cancel);
                    PairInventory.ValidateRemote(parent, directory);
                    if (!directory.IsFolder || directory.SkipSync || directory.Name.Normalize() != name)
                    {
                        throw new IOException(UiText.Get("sync.createdFolderUnverified"));
                    }

                    there = new PairNode(key, NodeKind.Directory, Remote: directory);
                    remote.Nodes.Add(key, there);
                }
                else
                {
                    if (string.IsNullOrEmpty(there.Remote!.Id) || await drive.FolderIdAsync(there.Remote.Uri, cancel) != there.Remote.Id)
                    {
                        throw new IOException(UiText.Get("sync.remoteFolderChanged"));
                    }

                    if (DeferIfChanged(key))
                    {
                        continue;
                    }

                    if (LocalFile.ChangeDirectoryEntry(path, create: true) == DirectoryChangeResult.PathChanged)
                    {
                        DeferIfChanged(key, operationRaced: true);
                        continue;
                    }

                    here = new PairNode(key, NodeKind.Directory, LocalId: LocalFile.Identity(path));
                    local.Nodes.Add(key, here);
                }

                if (DeferIfChanged(key))
                {
                    continue;
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

        bool DeferIfChanged(string key, bool operationRaced = false)
        {
            string? changed = ChangedLocalDirectory(local, key);
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
    }

    private string? ChangedLocalDirectory(PairInventory local, string key)
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
                for (string? ancestor = key; !string.IsNullOrEmpty(ancestor); ancestor = PairKey.Parent(ancestor))
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
}
