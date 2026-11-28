using Cloudreve.Sync;

namespace Cloudreve.Desktop;

// Targets cover their subtrees. Ancestors are visited only to resolve and verify those targets.
internal sealed class PairScanScope
{
    private readonly HashSet<string> targets = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> ancestors = new(StringComparer.OrdinalIgnoreCase);

    internal PairScanScope(IEnumerable<string> keys)
    {
        foreach (string key in keys)
        {
            PairInventory.ValidateKey(key);
            targets.Add(key);
            for (string? parent = PairKey.Parent(key); !string.IsNullOrEmpty(parent); parent = PairKey.Parent(parent))
            {
                ancestors.Add(parent);
            }
        }
    }

    // Paths known to be deleted rather than moved, so their disappearance does not need a full scan to explain it.
    // Local ones were checked against a complete local inventory: their identity is nowhere else on disk. Cloud ones
    // were found in the trash with the identity the baseline knew; a trashed folder confirms everything below it.
    internal IReadOnlySet<string> ConfirmedLocalDeletions { get; init; } = new HashSet<string>();
    internal IReadOnlySet<string> ConfirmedRemoteDeletions { get; init; } = new HashSet<string>();

    internal IEnumerable<string> Targets
    {
        get { return targets; }
    }

    internal IEnumerable<string> Ancestors
    {
        get { return ancestors; }
    }

    internal bool Covers(string key)
    {
        for (string? path = key; !string.IsNullOrEmpty(path); path = PairKey.Parent(path))
        {
            if (targets.Contains(path))
            {
                return true;
            }
        }

        return false;
    }

    internal bool Visits(string key)
    {
        return ancestors.Contains(key) || Covers(key);
    }

    // Returns why this path scan cannot be trusted on its own, or null when it can.
    internal string? FullInventoryReason(Dictionary<string, FileBaseline> baseline, PairInventory local, PairInventory remote, BaselineDatabase database)
    {
        // Only a full scan can show that a directory recreated in place is not one half of a move. Below a replacement
        // that cannot be adopted, the full scan blocks the subtree too, so escalating there would repeat on every change.
        var blocked = baseline.Keys.Where(key => ReplacedLocally(key, baseline, local) && !RecreatedCandidate(key, baseline, local, remote))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string key in baseline.Keys)
        {
            if (RecreatedCandidate(key, baseline, local, remote) && !Within(blocked, key))
            {
                return "LocalDirectoryRecreated";
            }
        }

        var replaced = ReplacedAncestors(baseline, local);
        foreach (string parent in ancestors)
        {
            if (Covers(parent) || Within(replaced, parent))
            {
                continue;
            }

            // A change to an ancestor can affect siblings outside this batch.
            if (!baseline.TryGetValue(parent, out var known) || !known.IsDirectory)
            {
                return "AncestorNotSynced";
            }

            if (!local.Nodes.TryGetValue(parent, out var here) || here.Kind != NodeKind.Directory || here.LocalId != known.LocalId || here.Key != parent)
            {
                return "LocalAncestorChanged";
            }

            if (!remote.Nodes.TryGetValue(parent, out var there) || there.Kind != NodeKind.Directory || there.Remote!.Id != known.RemoteId || there.Key != parent)
            {
                return "RemoteAncestorChanged";
            }
        }

        var localIds = local.Nodes.Values.Where(node => node.LocalId != null).Select(node => node.LocalId!).ToHashSet();
        var remoteIds = remote.Nodes.Values.Where(node => node.Remote?.Id != null).Select(node => node.Remote!.Id!).ToHashSet();
        foreach (var (key, known) in baseline)
        {
            if (Within(replaced, key))
            {
                continue;
            }

            // The database returned only visited paths. A lone disappearance can be one half of a move.
            if (!local.Nodes.ContainsKey(key) && known.LocalId != null && !localIds.Contains(known.LocalId) && !ConfirmedLocalDeletions.Contains(key))
            {
                return "LocalItemDisappeared";
            }

            if (!remote.Nodes.ContainsKey(key) && known.RemoteId != null && !remoteIds.Contains(known.RemoteId) && !ConfirmedRemoteDeletion(key))
            {
                return "RemoteItemDisappeared";
            }
        }

        // Still checked for replaced trees: a replacement that came from elsewhere in the pair is a move.
        return database.HasOutsideIdentity(baseline, local, remote) ? "IdentityMovedFromOutsideScope" : null;
    }

    // Any local replacement that is not adopted stays a conflict that blocks its whole subtree in a full scan too,
    // so walking the pair would only reach the same result. Paths inside it are blocked by this pass instead.
    private HashSet<string> ReplacedAncestors(Dictionary<string, FileBaseline> baseline, PairInventory local)
    {
        return ancestors.Where(parent => !Covers(parent) && ReplacedLocally(parent, baseline, local)).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    // Tracked paths this scan no longer finds in the cloud, whose identity is nowhere else in the scan and which are not
    // yet confirmed deleted. Each one makes FullInventoryReason ask for a full scan unless the trash confirms it.
    internal List<string> UnexplainedRemoteDisappearances(Dictionary<string, FileBaseline> baseline, PairInventory local, PairInventory remote)
    {
        var replaced = ReplacedAncestors(baseline, local);
        var remoteIds = remote.Nodes.Values.Where(node => node.Remote?.Id != null).Select(node => node.Remote!.Id!).ToHashSet();
        return baseline.Where(entry => !Within(replaced, entry.Key) && !remote.Nodes.ContainsKey(entry.Key) && entry.Value.RemoteId != null &&
                                       !remoteIds.Contains(entry.Value.RemoteId) && !ConfirmedRemoteDeletion(entry.Key))
            .Select(entry => entry.Key).ToList();
    }

    internal PairScanScope WithConfirmedRemoteDeletions(IEnumerable<string> keys)
    {
        var confirmed = new HashSet<string>(ConfirmedRemoteDeletions, StringComparer.OrdinalIgnoreCase);
        confirmed.UnionWith(keys);
        return new PairScanScope(targets) { ConfirmedLocalDeletions = ConfirmedLocalDeletions, ConfirmedRemoteDeletions = confirmed };
    }

    private bool ConfirmedRemoteDeletion(string key)
    {
        for (string? path = key; !string.IsNullOrEmpty(path); path = PairKey.Parent(path))
        {
            if (ConfirmedRemoteDeletions.Contains(path))
            {
                return true;
            }
        }

        return false;
    }

    private static bool RecreatedCandidate(string key, Dictionary<string, FileBaseline> baseline, PairInventory local, PairInventory remote)
    {
        return baseline.TryGetValue(key, out var known) && local.Nodes.TryGetValue(key, out var here) && here.Key == key &&
               DirectoryReconciler.MayBeRecreatedInPlace(known, here, remote.Nodes.GetValueOrDefault(key));
    }

    // Mirrors the identity check in SyncDirectoriesAsync that marks the directory as a conflict.
    private static bool ReplacedLocally(string key, Dictionary<string, FileBaseline> baseline, PairInventory local)
    {
        return baseline.TryGetValue(key, out var known) && known.IsDirectory && known.LocalId != null &&
               local.Nodes.TryGetValue(key, out var here) && here.Kind == NodeKind.Directory && here.Key == key && here.LocalId != known.LocalId;
    }

    private static bool Within(HashSet<string> roots, string key)
    {
        for (string? path = key; !string.IsNullOrEmpty(path); path = PairKey.Parent(path))
        {
            if (roots.Contains(path))
            {
                return true;
            }
        }

        return false;
    }
}
