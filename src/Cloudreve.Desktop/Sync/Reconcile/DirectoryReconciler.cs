using Cloudreve.Sync;

namespace Cloudreve.Desktop;

internal enum DirectoryAction
{
    // Drop the folder from the baseline: it is ignored, or gone from both sides.
    Forget,
    // A parent was already set aside.
    Skip,
    Conflict,
    // Deleted locally; trash the cloud folder after the file pass, once its contents are rechecked.
    TrashWhenVerified,
    // Deleted in the cloud; remove the local folder if it is still empty.
    RemoveLocal,
    CreateRemote,
    CreateLocal,
    // Present on both sides; record their identities.
    Record
}

internal readonly record struct DirectoryDecision(DirectoryAction Action, ConflictCause Cause = default)
{
    internal static DirectoryDecision Conflict(ConflictCause cause)
    {
        return new DirectoryDecision(DirectoryAction.Conflict, cause);
    }
}

internal static class DirectoryReconciler
{
    // Build tools and package managers delete and recreate their output folders, which gives them new identities.
    // A directory that reappears at the same path on the same volume, while its cloud folder is unchanged, can be the
    // same folder. Decide also requires that neither identity belongs to another tracked or scanned item, which only a
    // full scan can show, so a path scan escalates when this holds.
    internal static bool MayBeRecreatedInPlace(FileBaseline previous, PairNode here, PairNode? there)
    {
        return previous.IsDirectory && previous.LocalId != null && here.Kind == NodeKind.Directory && here.LocalId != null &&
               here.LocalId != previous.LocalId && Volume(here.LocalId) == Volume(previous.LocalId) &&
               !string.IsNullOrEmpty(previous.RemoteId) && there?.Kind == NodeKind.Directory && there.Remote?.Id == previous.RemoteId;
    }

    private static string Volume(string localId)
    {
        int end = localId.IndexOf(':');
        return end < 0 ? localId : localId[..end];
    }

    // Every folder known to either side or the baseline, parents first.
    internal static string[] Keys(ReconcileView view)
    {
        return view.Baseline.Where(entry => entry.Value.IsDirectory).Select(entry => entry.Key)
            .Concat(view.Local.Nodes.Values.Concat(view.Remote.Nodes.Values).Where(node => node.Kind == NodeKind.Directory).Select(node => node.Key))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    // A directory rename, or moving a child out before deleting its parent, is not an unambiguous deletion.
    // Returns the missing folders and the moved items that must both be set aside.
    internal static IEnumerable<string> MovedOutOfMissingFolders(ReconcileView view)
    {
        var missing = view.Baseline.Where(entry => entry.Value.IsDirectory && !view.Local.Nodes.ContainsKey(entry.Key) && view.Remote.Nodes.ContainsKey(entry.Key))
            .Select(entry => entry.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (missing.Count == 0)
        {
            yield break;
        }

        var localById = view.Local.Nodes.Values.Where(node => node.LocalId != null).ToLookup(node => node.LocalId!);
        foreach (var (key, previous) in view.Baseline)
        {
            if (previous.LocalId == null || view.Local.Nodes.ContainsKey(key))
            {
                continue;
            }

            foreach (var moved in localById[previous.LocalId])
            {
                for (string? ancestor = key; !string.IsNullOrEmpty(ancestor); ancestor = PairKey.Parent(ancestor))
                {
                    if (missing.Contains(ancestor))
                    {
                        yield return ancestor;
                        yield return moved.Key;
                    }
                }
            }
        }
    }

    // scannedIds and trackedIds are every local identity in this scan and in the baseline. A recreated directory must
    // match neither: the old identity elsewhere means the folder moved, and a tracked new identity means another folder moved here.
    internal static DirectoryDecision Decide(string key, ReconcileView view, bool adoptRecreated, HashSet<string> scannedIds, HashSet<string> trackedIds)
    {
        if (view.Rules.Excludes(key, true))
        {
            return new DirectoryDecision(DirectoryAction.Forget);
        }

        if (view.IsBlocked(key))
        {
            return new DirectoryDecision(DirectoryAction.Skip);
        }

        view.Baseline.TryGetValue(key, out var previous);
        view.Local.Nodes.TryGetValue(key, out var here);
        view.Remote.Nodes.TryGetValue(key, out var there);
        if ((ReconcileView.UnsafeCause(view.Local, key) ?? ReconcileView.UnsafeCause(view.Remote, key)) is { } unsafeCause)
        {
            return DirectoryDecision.Conflict(unsafeCause);
        }

        if (previous?.IsDirectory == false || here != null && here.Kind != NodeKind.Directory || there != null && there.Kind != NodeKind.Directory)
        {
            return DirectoryDecision.Conflict(ReconcileView.KindCause(here, there, view.Local, view.Remote));
        }

        if (here != null && previous?.LocalId != null && here.LocalId != previous.LocalId &&
            !(adoptRecreated && MayBeRecreatedInPlace(previous, here, there) && !scannedIds.Contains(previous.LocalId) && !trackedIds.Contains(here.LocalId!)))
        {
            return DirectoryDecision.Conflict(ConflictCause.DirectoryReplaced);
        }

        if (there != null && previous?.RemoteId != null && there.Remote?.Id != previous.RemoteId)
        {
            return DirectoryDecision.Conflict(ConflictCause.CloudFolderReplaced);
        }

        if (here == null && there == null)
        {
            return new DirectoryDecision(DirectoryAction.Forget);
        }

        if (previous != null && here == null)
        {
            return previous.LocalId != null && !string.IsNullOrEmpty(previous.RemoteId) && there!.Remote!.Id == previous.RemoteId
                ? new DirectoryDecision(DirectoryAction.TrashWhenVerified)
                : DirectoryDecision.Conflict(ConflictCause.UnverifiedIdentity);
        }

        if (previous != null && there == null)
        {
            // A folder without a verified identity is never removed.
            return previous.LocalId != null ? new DirectoryDecision(DirectoryAction.RemoveLocal) : DirectoryDecision.Conflict(ConflictCause.UnverifiedIdentity);
        }

        if (there == null)
        {
            return new DirectoryDecision(DirectoryAction.CreateRemote);
        }

        return here == null ? new DirectoryDecision(DirectoryAction.CreateLocal) : new DirectoryDecision(DirectoryAction.Record);
    }
}
