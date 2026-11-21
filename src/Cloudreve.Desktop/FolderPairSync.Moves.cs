using Cloudreve.Sync;

namespace Cloudreve.Desktop;

internal sealed partial class FolderPairSync
{
    private async Task<bool> ApplyFileMovesAsync(PairInventory local, PairInventory remote, Dictionary<string, FileBaseline> baseline,
        PairIgnoreRules rules, HashSet<string> conflicts, HashSet<string> blocked, CancellationToken cancel)
    {
        var localIds = local.Nodes.Values.Where(node => node.LocalId != null).ToLookup(node => node.LocalId!);
        var remoteIds = remote.Nodes.Values.Where(node => node.Remote?.Id != null).ToLookup(node => node.Remote!.Id!);
        var knownLocal = baseline.Values.ToLookup(entry => entry.LocalId);
        var knownRemote = baseline.Values.ToLookup(entry => entry.RemoteId);
        bool changed = false;
        foreach (var (oldKey, previous) in baseline.ToArray())
        {
            cancel.ThrowIfCancellationRequested();
            if (previous.IsDirectory || rules.Excludes(oldKey) || previous.LocalId == null || string.IsNullOrEmpty(previous.RemoteId))
            {
                continue;
            }

            var left = localIds[previous.LocalId].ToArray();
            var right = remoteIds[previous.RemoteId].ToArray();
            if (!left.Concat(right).Any(node => node.Key != oldKey))
            {
                continue;
            }

            // Never let a failed match fall through to the ordinary delete/create decisions.
            var involved = left.Concat(right).Select(node => node.Key).Append(oldKey).Distinct().ToArray();
            if (left.Length != 1 || right.Length != 1 || knownLocal[previous.LocalId].Count() != 1 || knownRemote[previous.RemoteId].Count() != 1 ||
                involved.Any(key => conflicts.Contains(key) || BlockedPath(blocked, key) || !SafeMovePath(local, key) || !SafeMovePath(remote, key)))
            {
                conflicts.UnionWith(involved);
                continue;
            }

            var here = left[0];
            var there = right[0];
            bool localMoved = here.Key != oldKey;
            bool remoteMoved = there.Key != oldKey;
            string target = localMoved ? here.Key : there.Key;
            if (here.Kind != NodeKind.File || there.Kind != NodeKind.File || here.Hash != previous.LocalHash || there.Remote!.Version != previous.RemoteVersion ||
                oldKey.Equals(target, StringComparison.OrdinalIgnoreCase) || localMoved && remoteMoved && here.Key != there.Key ||
                localMoved && local.Nodes.ContainsKey(oldKey) || remoteMoved && remote.Nodes.ContainsKey(oldKey) ||
                !localMoved && local.Nodes.ContainsKey(target) || !remoteMoved && remote.Nodes.ContainsKey(target) || baseline.ContainsKey(target))
            {
                conflicts.UnionWith(involved);
                continue;
            }

            string parent = Path.GetDirectoryName(target) ?? "";
            bool rename = Path.GetDirectoryName(oldKey) == Path.GetDirectoryName(target);
            if (!remoteMoved && !rename && Path.GetFileName(oldKey) != Path.GetFileName(target))
            {
                // Cloudreve has separate move and rename requests, not an atomic combined operation.
                conflicts.UnionWith(involved);
                continue;
            }

            await VerifyRemoteRootAsync(cancel);
            await ResolveRemoteAsync(there.Key, there.Remote, false, cancel);
            await RequireLocalAsync(here.Key, here.Hash, cancel);
            if (MacFile.Identity(Path.Combine(pair.Local, here.Key)) != previous.LocalId)
            {
                throw new IOException(UiText.Get("移动期间本地文件已被替换。"));
            }

            if (localMoved)
            {
                await RequireLocalAsync(oldKey, null, cancel);
            }

            if (remoteMoved)
            {
                await ResolveRemoteAsync(oldKey, null, false, cancel);
            }

            activity?.Start(UiText.Get("同步移动"), target);
            if (!remoteMoved)
            {
                string destination = await ResolveRemoteAsync(target, null, true, cancel);
                await VerifyRemoteRootAsync(cancel);
                await VerifyVersionAsync(there.Remote, cancel);
                if (rename)
                {
                    await drive.RenameAsync(there.Remote, Path.GetFileName(target), cancel);
                }
                else
                {
                    string directory = pair.Remote;
                    foreach (string segment in parent.Split('/', StringSplitOptions.RemoveEmptyEntries))
                    {
                        directory = DriveClient.ChildUri(directory, segment);
                    }

                    await drive.MoveAsync(there.Remote, directory, cancel);
                }

                var moved = there.Remote with { Uri = destination, Name = Path.GetFileName(target) };
                await VerifyVersionAsync(moved, cancel);
                remote.Nodes.Remove(oldKey);
                remote.Nodes.Add(target, there with { Key = target, Remote = moved });
            }
            else if (!localMoved)
            {
                EnsureLocalDirectory(parent);
                await RequireLocalAsync(target, null, cancel);
                await RequireLocalAsync(oldKey, here.Hash, cancel);
                if (MacFile.Identity(Path.Combine(pair.Local, oldKey)) != previous.LocalId)
                {
                    throw new IOException(UiText.Get("移动期间本地文件已被替换。"));
                }

                File.Move(Path.Combine(pair.Local, oldKey), Path.Combine(pair.Local, target), false);
                local.Nodes.Remove(oldKey);
                local.Nodes.Add(target, here with { Key = target });
            }

            baseline.Remove(oldKey);
            baseline.Add(target, previous);
            changed = true;
            activity?.Finish(UiText.Get("完成"));
        }

        return changed;
    }

    private static bool SafeMovePath(PairInventory inventory, string key)
    {
        for (string? path = key; !string.IsNullOrEmpty(path); path = Path.GetDirectoryName(path))
        {
            if (inventory.Excluded.Contains(path) || inventory.Nodes.TryGetValue(path, out var entry) &&
                (entry.Key != path || entry.Kind == NodeKind.Unsupported || path != key && entry.Kind != NodeKind.Directory))
            {
                return false;
            }
        }

        return true;
    }
}