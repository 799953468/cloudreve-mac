using Cloudreve.Sync;

namespace Cloudreve.Desktop;

internal sealed partial class FolderPairSync
{
    private async Task<bool> ApplyFileMovesAsync(PairInventory local, PairInventory remote, Dictionary<string, FileBaseline> baseline,
        PairIgnoreRules rules, HashSet<string> conflicts, HashSet<string> blocked, CancellationToken cancel)
    {
        var localPaths = PathsByIdentity(local, node => node.LocalId);
        var remotePaths = PathsByIdentity(remote, node => node.Remote?.Id);
        var candidates = new List<KeyValuePair<string, FileBaseline>>();
        foreach (var entry in baseline)
        {
            cancel.ThrowIfCancellationRequested();
            var previous = entry.Value;
            if (previous.IsDirectory || previous.LocalId == null || string.IsNullOrEmpty(previous.RemoteId))
            {
                continue;
            }
            if ((localPaths.TryGetValue(previous.LocalId, out string? leftPath) && leftPath != entry.Key ||
                remotePaths.TryGetValue(previous.RemoteId, out string? rightPath) && rightPath != entry.Key) && !rules.Excludes(entry.Key))
            {
                candidates.Add(entry);
            }
        }
        if (candidates.Count == 0)
        {
            return false;
        }

        var wantedLocal = candidates.Select(entry => entry.Value.LocalId).ToHashSet();
        var wantedRemote = candidates.Select(entry => entry.Value.RemoteId).ToHashSet();
        var localIds = local.Nodes.Values.Where(node => wantedLocal.Contains(node.LocalId)).ToLookup(node => node.LocalId);
        var remoteIds = remote.Nodes.Values.Where(node => wantedRemote.Contains(node.Remote?.Id)).ToLookup(node => node.Remote!.Id);
        var knownLocal = baseline.Values.Where(entry => wantedLocal.Contains(entry.LocalId)).ToLookup(entry => entry.LocalId);
        var knownRemote = baseline.Values.Where(entry => wantedRemote.Contains(entry.RemoteId)).ToLookup(entry => entry.RemoteId);
        bool changed = false;
        foreach (var (oldKey, previous) in candidates)
        {
            cancel.ThrowIfCancellationRequested();
            var left = localIds[previous.LocalId].ToArray();
            var right = remoteIds[previous.RemoteId].ToArray();

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

            string parent = PairKey.Parent(target) ?? "";
            bool rename = PairKey.Parent(oldKey) == PairKey.Parent(target);
            if (!remoteMoved && !rename && Path.GetFileName(oldKey) != Path.GetFileName(target))
            {
                // Cloudreve has separate move and rename requests, not an atomic combined operation.
                conflicts.UnionWith(involved);
                continue;
            }

            await VerifyRemoteRootAsync(cancel);
            await ResolveRemoteAsync(there.Key, there.Remote, false, cancel);
            await RequireLocalAsync(here.Key, here.Hash, cancel);
            if (LocalFile.Identity(Path.Combine(pair.Local, here.Key)) != previous.LocalId)
            {
                throw new IOException(UiText.Get("sync.localReplacedDuringMove"));
            }

            if (localMoved)
            {
                await RequireLocalAsync(oldKey, null, cancel);
            }

            if (remoteMoved)
            {
                await ResolveRemoteAsync(oldKey, null, false, cancel);
            }

            activity?.Start(UiText.Get("activity.move"), target);
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
            changed = true;
            activity?.Finish(UiText.Get("activity.done"));
        }

        return changed;
    }

    private static Dictionary<string, string?> PathsByIdentity(PairInventory inventory, Func<PairNode, string?> identity)
    {
        var paths = new Dictionary<string, string?>(inventory.Nodes.Count);
        foreach (var node in inventory.Nodes.Values)
        {
            string? id = identity(node);
            if (id != null && !paths.TryAdd(id, node.Key))
            {
                // A duplicate, even at the original path, needs the full ambiguity checks.
                paths[id] = null;
            }
        }
        return paths;
    }

    private static bool SafeMovePath(PairInventory inventory, string key)
    {
        for (string? path = key; !string.IsNullOrEmpty(path); path = PairKey.Parent(path))
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
