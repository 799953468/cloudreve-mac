using System.Diagnostics;
using Cloudreve.Sync;

namespace Cloudreve.Desktop;

internal sealed record FileConflict(Guid PairId, string Key, string? LocalHash, DriveEntry? Remote);

internal enum ConflictChoice
{
    KeepLocal,
    KeepRemote,
    KeepBoth
}

// DeferredPaths lists subtrees and files this pass skipped because they changed locally; only they need a retry.
// Causes explains each entry of Conflicts; it is null only for results built by hand.
internal sealed record PairSyncResult(int Uploaded, int Downloaded, int Removed, IReadOnlyList<string> Conflicts, IReadOnlyList<FileConflict> Choices, int DeferredDirectories,
    int DeferredFiles = 0, IReadOnlyList<string>? DeferredPaths = null, IReadOnlyDictionary<string, ConflictCause>? Causes = null);

// A local file no longer matches its scan. Only that path needs another look; the rest of the pass is still valid.
internal sealed class LocalFileChangedException(string message) : IOException(message);

internal sealed class PairScanAttempt
{
    private readonly Stopwatch timer = Stopwatch.StartNew();
    internal string Stage { get; set; } = "Prepare";
    internal PairScanScope? Scope { get; set; }
    // Why this pass scanned the whole pair. Set by the caller for a full request and here when a path scan escalates.
    internal string? FullReason { get; set; }
    internal long ElapsedMs { get { return timer.ElapsedMilliseconds; } }
}

// Entry point for one folder pair. Each RunAsync is a SyncPass; conflict resolution uses the same guarded operations.
internal sealed class FolderPairSync(FolderPairStore store, FolderPair pair, DriveClient drive, SyncActivity? activity = null, Action? beginFullRemoteInventory = null)
{
    private readonly PassResources resources = new();
    private PairOperations? operations;

    private PairOperations Ops
    {
        get { return operations ??= new PairOperations(pair, drive, activity, resources.RemoteScan); }
    }

    internal Task<PairSyncResult> RunAsync(CancellationToken cancel, PairScanAttempt? attempt = null, PairScanScope? scope = null)
    {
        return new SyncPass(store, pair, Ops, resources, activity, beginFullRemoteInventory, attempt ?? new PairScanAttempt(), scope).RunAsync(cancel);
    }

    // Within this margin the two times cannot say which side is newer: FAT stores 2-second times, and an upload
    // copies the local time to the cloud.
    internal static readonly TimeSpan ModifiedTimeMargin = TimeSpan.FromSeconds(2);

    internal static ConflictChoice? NewerSide(DateTimeOffset? local, DateTimeOffset? remote)
    {
        if (local is not { } here || remote is not { } there || (here - there).Duration() <= ModifiedTimeMargin)
        {
            return null;
        }

        return here > there ? ConflictChoice.KeepLocal : ConflictChoice.KeepRemote;
    }

    // Reads both modification times of a file that exists on both sides. Choice is null when they cannot decide.
    internal async Task<(ConflictChoice? Choice, DateTimeOffset? Local, DateTimeOffset? Remote)> NewerSideAsync(FileConflict conflict, CancellationToken cancel)
    {
        PairInventory.ValidateKey(conflict.Key);
        if (conflict.PairId != pair.Marker || conflict.LocalHash == null || conflict.Remote == null)
        {
            return (null, null, null);
        }

        string path = Path.Combine(pair.Local, conflict.Key);
        DateTimeOffset? local = LocalFile.Inspect(path) == NodeKind.File ? new DateTimeOffset(File.GetLastWriteTimeUtc(path)) : null;
        var remote = await drive.ModifiedAsync(conflict.Remote.Uri, cancel);
        return (NewerSide(local, remote), local, remote);
    }

    // Returns the key of the local copy that KeepBoth created, so the caller can scan it.
    internal async Task<string?> ResolveConflictAsync(FileConflict conflict, ConflictChoice choice, CancellationToken cancel)
    {
        PairInventory.ValidateKey(conflict.Key);
        if (conflict.PairId != pair.Marker || !Enum.IsDefined(choice))
        {
            throw new InvalidOperationException(UiText.Get("pair.conflictFromOtherPair"));
        }

        using (store.Lock(pair))
        {
            FolderPairStore.Verify(pair);
            var rules = new PairIgnoreRules(store.ReadRules(pair));
            if (rules.Excludes(conflict.Key))
            {
                throw new IOException(UiText.Get("sync.pathIgnored"));
            }

            await Ops.VerifyRemoteRootAsync(cancel);
            string key = conflict.Key;
            string path = Path.Combine(pair.Local, key);
            string? hash = conflict.LocalHash;
            var file = conflict.Remote;
            await Ops.ResolveRemoteAsync(key, file, false, cancel);
            await Ops.RequireLocalAsync(key, hash, cancel);
            var baseline = store.ReadBaseline(pair);
            string? copyKey = null;
            if (choice == ConflictChoice.KeepBoth)
            {
                if (hash == null || file == null)
                {
                    throw new IOException(UiText.Get("sync.keepBothNeedsFiles"));
                }

                string extension = Path.GetExtension(key);
                copyKey = PairKey.Combine(PairKey.Parent(key) ?? "", "local-copy-" + Guid.NewGuid().ToString("N") + extension);
                if (rules.Excludes(copyKey))
                {
                    throw new IOException(UiText.Get("sync.localCopyIgnored"));
                }

                await Ops.ResolveRemoteAsync(copyKey, null, false, cancel);
                string temp = Path.Combine(Path.GetDirectoryName(path)!, $".cloudreve-{Guid.NewGuid():N}.part");
                try
                {
                    File.Copy(path, temp, false);
                    if (await PairInventory.HashAsync(temp, cancel) != hash)
                    {
                        throw new IOException(UiText.Get("sync.localChangedNotOverwritten"));
                    }

                    File.Move(temp, Path.Combine(pair.Local, copyKey), false);
                }
                finally
                {
                    FolderPairStore.CheckDirectory(Path.GetDirectoryName(temp)!);
                    File.Delete(temp);
                }
            }

            if (choice == ConflictChoice.KeepLocal && hash != null)
            {
                string target = await Ops.ResolveRemoteAsync(key, file, true, cancel);
                await Ops.RequireLocalAsync(key, hash, cancel);
                await drive.UploadAsync(path, target, file, cancel);
                file = await PairInventory.ReadVersionAsync(drive, target, cancel);
                if (await Ops.CompareRemoteHashAsync(key, file, hash, activity, cancel) != hash)
                {
                    throw new IOException(UiText.Get("sync.resolveReadbackMismatch"));
                }

                baseline[key] = new FileBaseline(hash, file.Version!, LocalFile.Identity(path), file.Id);
            }
            else if (choice == ConflictChoice.KeepLocal && file != null)
            {
                await drive.TrashAsync(file, cancel);
                baseline.Remove(key);
            }
            else if (file != null)
            {
                var received = await Ops.InstallDownloadAsync(key, file, hash, activity, cancel);
                baseline[key] = new FileBaseline(received.Hash!, file.Version!, received.LocalId, file.Id, LocalGeneration: received.LocalGeneration);
            }
            else if (hash != null)
            {
                Ops.MoveToRecovery(key);
                baseline.Remove(key);
            }

            store.SaveBaseline(pair, baseline);
            return copyKey;
        }
    }
}
