using Cloudreve.Sync;

namespace Cloudreve.Desktop;

internal enum FileStep
{
    // A parent was set aside, or a folder handles this path.
    Skip,
    // Drop the path from the baseline: ignored, or gone from both sides.
    Forget,
    // The cloud entry may be this app's own unfinished upload; the pass must check its saved session first.
    CheckOwnedUpload,
    // The two sides cannot be reconciled as one file. Blocks the path.
    Conflict,
    // Both sides changed differently. The user can choose a copy.
    ConflictWithChoice,
    // Unchanged content whose identities moved on; refresh them in the baseline.
    UpdateIds,
    TrashRemote,
    RecoverLocal,
    // Upload, download or compare contents; see Action.
    Transfer
}

// Action is the SyncDecision outcome when one was reached; the pass rechecks the paired root before acting on it.
internal sealed record FileDecision(FileStep Step, ConflictCause Cause = default, SyncAction Action = SyncAction.Unchanged,
    FileBaseline? Previous = null, PairNode? Here = null, PairNode? There = null);

internal static class FileReconciler
{
    // Every path known to either side or the baseline, parents first.
    internal static string[] Keys(ReconcileView view)
    {
        return view.Baseline.Keys.Concat(view.Local.Nodes.Keys).Concat(view.Remote.Nodes.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static FileDecision Decide(string key, ReconcileView view, bool ownedUploadChecked = false)
    {
        if (view.IsBlocked(key))
        {
            return new FileDecision(FileStep.Skip);
        }

        if (view.Rules.Excludes(key) || view.Local.Excluded.Contains(key) || view.Remote.Excluded.Contains(key))
        {
            return new FileDecision(FileStep.Forget);
        }

        view.Baseline.TryGetValue(key, out var previous);
        view.Local.Nodes.TryGetValue(key, out var here);
        view.Remote.Nodes.TryGetValue(key, out var there);
        if (previous?.IsDirectory == true)
        {
            return new FileDecision(FileStep.Skip);
        }

        if (!ownedUploadChecked && there?.Key == key && there.Remote?.UploadSessionId != null)
        {
            return new FileDecision(FileStep.CheckOwnedUpload, Previous: previous, Here: here, There: there);
        }

        if (here?.Kind == NodeKind.Unsupported || there?.Kind == NodeKind.Unsupported)
        {
            return new FileDecision(FileStep.Conflict, ReconcileView.KindCause(here, there, view.Local, view.Remote));
        }

        if (here != null && here.Key != key || there != null && there.Key != key)
        {
            return new FileDecision(FileStep.Conflict, ConflictCause.NameCollision);
        }

        if (here != null && there != null && here.Kind != there.Kind ||
            previous != null && (here?.Kind == NodeKind.Directory || there?.Kind == NodeKind.Directory))
        {
            return new FileDecision(FileStep.Conflict, ConflictCause.TypeMismatch);
        }

        if (here?.Kind == NodeKind.Directory || there?.Kind == NodeKind.Directory)
        {
            return new FileDecision(FileStep.Skip);
        }

        var file = there?.Remote;
        string? hash = here?.Hash;
        var action = SyncDecision.ForFile(previous, hash, file?.Version);
        return action switch
        {
            SyncAction.Unchanged => previous!.LocalId != here!.LocalId || previous.LocalGeneration != here.LocalGeneration || previous.RemoteId != file!.Id
                ? new FileDecision(FileStep.UpdateIds, Previous: previous, Here: here, There: there)
                : new FileDecision(FileStep.Skip),
            SyncAction.Forget => new FileDecision(FileStep.Forget, Action: action),
            SyncAction.Conflict => new FileDecision(FileStep.ConflictWithChoice, ChoiceCause(hash, file), action, Previous: previous, Here: here, There: there),
            SyncAction.TrashRemote => new FileDecision(FileStep.TrashRemote, Action: action, Previous: previous, Here: here, There: there),
            SyncAction.RecoverLocal => new FileDecision(FileStep.RecoverLocal, Action: action, Previous: previous, Here: here, There: there),
            SyncAction.Upload or SyncAction.Download or SyncAction.CompareContents =>
                new FileDecision(FileStep.Transfer, Action: action, Previous: previous, Here: here, There: there),
            _ => throw new InvalidOperationException(UiText.Get("sync.unknownAction"))
        };
    }

    internal static ConflictCause ChoiceCause(string? localHash, DriveEntry? remote)
    {
        return localHash == null ? ConflictCause.DeletedLocally : remote == null ? ConflictCause.DeletedInCloud : ConflictCause.ContentsDiffer;
    }
}
