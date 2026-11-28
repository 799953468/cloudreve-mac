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

    internal bool NeedsFullInventory(Dictionary<string, FileBaseline> baseline, PairInventory local, PairInventory remote, BaselineDatabase database)
    {
        foreach (string parent in ancestors)
        {
            if (Covers(parent))
            {
                continue;
            }

            // A change to an ancestor can affect siblings outside this batch.
            if (!baseline.TryGetValue(parent, out var known) || !known.IsDirectory ||
                !local.Nodes.TryGetValue(parent, out var here) || here.Kind != NodeKind.Directory || here.LocalId != known.LocalId ||
                !remote.Nodes.TryGetValue(parent, out var there) || there.Kind != NodeKind.Directory || there.Remote!.Id != known.RemoteId ||
                here.Key != parent || there.Key != parent)
            {
                return true;
            }
        }

        var localIds = local.Nodes.Values.Where(node => node.LocalId != null).Select(node => node.LocalId!).ToHashSet();
        var remoteIds = remote.Nodes.Values.Where(node => node.Remote?.Id != null).Select(node => node.Remote!.Id!).ToHashSet();
        foreach (var (key, known) in baseline)
        {
            // The database returned only visited paths. A lone disappearance can be one half of a move.
            if (!local.Nodes.ContainsKey(key) && known.LocalId != null && !localIds.Contains(known.LocalId) ||
                !remote.Nodes.ContainsKey(key) && known.RemoteId != null && !remoteIds.Contains(known.RemoteId))
            {
                return true;
            }
        }

        return database.HasOutsideIdentity(baseline, local, remote);
    }
}
