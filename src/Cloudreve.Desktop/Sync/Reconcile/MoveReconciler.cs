using Cloudreve.Sync;

namespace Cloudreve.Desktop;

// A tracked file found at a new path on one side. Apply it by moving the other side to match.
internal sealed record FileMove(string OldKey, FileBaseline Previous, PairNode Here, PairNode There, bool LocalMoved, bool RemoteMoved)
{
    internal string Target
    {
        get { return LocalMoved ? Here.Key : There.Key; }
    }

    internal bool Rename
    {
        get { return PairKey.Parent(OldKey) == PairKey.Parent(Target); }
    }
}

internal static class MoveReconciler
{
    // Tracked files whose identity now appears at another path on either side.
    internal static List<KeyValuePair<string, FileBaseline>> Candidates(ReconcileView view, CancellationToken cancel)
    {
        var localPaths = PathsByIdentity(view.Local, node => node.LocalId);
        var remotePaths = PathsByIdentity(view.Remote, node => node.Remote?.Id);
        var candidates = new List<KeyValuePair<string, FileBaseline>>();
        foreach (var entry in view.Baseline)
        {
            cancel.ThrowIfCancellationRequested();
            var previous = entry.Value;
            if (previous.IsDirectory || previous.LocalId == null || string.IsNullOrEmpty(previous.RemoteId))
            {
                continue;
            }

            if ((localPaths.TryGetValue(previous.LocalId, out string? leftPath) && leftPath != entry.Key ||
                 remotePaths.TryGetValue(previous.RemoteId, out string? rightPath) && rightPath != entry.Key) && !view.Rules.Excludes(entry.Key))
            {
                candidates.Add(entry);
            }
        }

        return candidates;
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

    // Identity lookups shared by every candidate of one pass.
    internal sealed class Lookups
    {
        internal required ILookup<string?, PairNode> Local { get; init; }
        internal required ILookup<string?, PairNode> Remote { get; init; }
        internal required ILookup<string?, FileBaseline> KnownLocal { get; init; }
        internal required ILookup<string?, FileBaseline> KnownRemote { get; init; }
    }

    internal static Lookups Index(ReconcileView view, List<KeyValuePair<string, FileBaseline>> candidates)
    {
        var wantedLocal = candidates.Select(entry => entry.Value.LocalId).ToHashSet();
        var wantedRemote = candidates.Select(entry => entry.Value.RemoteId).ToHashSet();
        return new Lookups
        {
            Local = view.Local.Nodes.Values.Where(node => wantedLocal.Contains(node.LocalId)).ToLookup(node => node.LocalId),
            Remote = view.Remote.Nodes.Values.Where(node => wantedRemote.Contains(node.Remote?.Id)).ToLookup(node => node.Remote!.Id),
            KnownLocal = view.Baseline.Values.Where(entry => wantedLocal.Contains(entry.LocalId)).ToLookup(entry => entry.LocalId),
            KnownRemote = view.Baseline.Values.Where(entry => wantedRemote.Contains(entry.RemoteId)).ToLookup(entry => entry.RemoteId)
        };
    }

    // Returns the move to apply, or null after recording every path involved as an uncertain move.
    // A failed match must never fall through to the ordinary delete and create decisions.
    internal static FileMove? Decide(string oldKey, FileBaseline previous, ReconcileView view, Lookups index)
    {
        var left = index.Local[previous.LocalId].ToArray();
        var right = index.Remote[previous.RemoteId].ToArray();
        var involved = left.Concat(right).Select(node => node.Key).Append(oldKey).Distinct().ToArray();
        if (left.Length != 1 || right.Length != 1 || index.KnownLocal[previous.LocalId].Count() != 1 || index.KnownRemote[previous.RemoteId].Count() != 1 ||
            involved.Any(key => view.Conflicts.Contains(key) || view.IsBlocked(key) || !ReconcileView.SafePath(view.Local, key) || !ReconcileView.SafePath(view.Remote, key)))
        {
            view.Conflicts.AddRange(involved, ConflictCause.UncertainMove);
            return null;
        }

        var here = left[0];
        var there = right[0];
        bool localMoved = here.Key != oldKey;
        bool remoteMoved = there.Key != oldKey;
        string target = localMoved ? here.Key : there.Key;
        if (here.Kind != NodeKind.File || there.Kind != NodeKind.File || here.Hash != previous.LocalHash || there.Remote!.Version != previous.RemoteVersion ||
            oldKey.Equals(target, StringComparison.OrdinalIgnoreCase) || localMoved && remoteMoved && here.Key != there.Key ||
            localMoved && view.Local.Nodes.ContainsKey(oldKey) || remoteMoved && view.Remote.Nodes.ContainsKey(oldKey) ||
            !localMoved && view.Local.Nodes.ContainsKey(target) || !remoteMoved && view.Remote.Nodes.ContainsKey(target) || view.Baseline.ContainsKey(target))
        {
            view.Conflicts.AddRange(involved, ConflictCause.UncertainMove);
            return null;
        }

        var move = new FileMove(oldKey, previous, here, there, localMoved, remoteMoved);
        if (!remoteMoved && !move.Rename && Path.GetFileName(oldKey) != Path.GetFileName(target))
        {
            // Cloudreve has separate move and rename requests, not an atomic combined operation.
            view.Conflicts.AddRange(involved, ConflictCause.UncertainMove);
            return null;
        }

        return move;
    }
}
