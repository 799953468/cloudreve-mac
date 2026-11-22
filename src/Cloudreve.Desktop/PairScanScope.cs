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
            for (string? parent = Path.GetDirectoryName(key); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
            {
                ancestors.Add(parent);
            }
        }
    }

    internal bool Covers(string key)
    {
        for (string? path = key; !string.IsNullOrEmpty(path); path = Path.GetDirectoryName(path))
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

    internal bool NeedsFullInventory(Dictionary<string, FileBaseline> baseline, PairInventory local, PairInventory remote)
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
            if (Visits(key))
            {
                // A lone disappearance may be one half of a move delivered in a different batch.
                if (!local.Nodes.ContainsKey(key) && known.LocalId != null && !localIds.Contains(known.LocalId) ||
                    !remote.Nodes.ContainsKey(key) && known.RemoteId != null && !remoteIds.Contains(known.RemoteId))
                {
                    return true;
                }
            }
            else if (known.LocalId != null && localIds.Contains(known.LocalId) || known.RemoteId != null && remoteIds.Contains(known.RemoteId))
            {
                // An identity crosses the scan boundary. Move and hard-link decisions need both sides.
                return true;
            }
        }

        return false;
    }
}
