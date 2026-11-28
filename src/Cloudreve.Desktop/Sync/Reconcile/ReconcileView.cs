using Cloudreve.Sync;

namespace Cloudreve.Desktop;

// What the reconcilers decide from: the last synced state, both scans and the paths this pass already set aside.
// Reconcilers never touch the disk or the network; the pass executes their decisions and updates this view.
internal sealed class ReconcileView(Dictionary<string, FileBaseline> baseline, PairInventory local, PairInventory remote, PairIgnoreRules rules,
    ConflictSet conflicts, HashSet<string> blocked)
{
    internal Dictionary<string, FileBaseline> Baseline { get; } = baseline;
    internal PairInventory Local { get; } = local;
    internal PairInventory Remote { get; } = remote;
    internal PairIgnoreRules Rules { get; } = rules;
    internal ConflictSet Conflicts { get; } = conflicts;
    // Paths whose whole subtree this pass leaves alone.
    internal HashSet<string> Blocked { get; } = blocked;

    internal static bool Within(HashSet<string> roots, string key)
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

    internal bool IsBlocked(string key)
    {
        return Within(Blocked, key);
    }

    // A path is safe to change only when it and every parent were scanned as ordinary folders under exactly this name.
    internal static bool SafePath(PairInventory inventory, string key)
    {
        return UnsafeCause(inventory, key) == null;
    }

    internal static ConflictCause? UnsafeCause(PairInventory inventory, string key)
    {
        for (string? path = key; !string.IsNullOrEmpty(path); path = PairKey.Parent(path))
        {
            if (inventory.Excluded.Contains(path))
            {
                return ConflictCause.UnsupportedItem;
            }

            if (inventory.Nodes.TryGetValue(path, out var entry))
            {
                if (entry.Key != path)
                {
                    return ConflictCause.NameCollision;
                }

                if (entry.Kind == NodeKind.Unsupported)
                {
                    return inventory.Collisions.Contains(path) ? ConflictCause.NameCollision : ConflictCause.UnsupportedItem;
                }

                if (path != key && entry.Kind != NodeKind.Directory)
                {
                    return ConflictCause.TypeMismatch;
                }
            }
        }

        return null;
    }

    // Why two scanned nodes at one path cannot be reconciled as the same kind of item.
    internal static ConflictCause KindCause(PairNode? here, PairNode? there, PairInventory local, PairInventory remote)
    {
        if (here?.Kind == NodeKind.Unsupported)
        {
            return local.Collisions.Contains(here.Key) ? ConflictCause.NameCollision : ConflictCause.UnsupportedItem;
        }

        if (there?.Kind == NodeKind.Unsupported)
        {
            return remote.Collisions.Contains(there.Key) ? ConflictCause.NameCollision : ConflictCause.UnsupportedItem;
        }

        return ConflictCause.TypeMismatch;
    }
}
