namespace Cloudreve.Desktop;

internal sealed record FileBaseline(string LocalHash, string RemoteVersion);

internal enum SyncAction
{
    Forget, Unchanged, Upload, Download, CompareContents, TrashRemote, RecoverLocal, Conflict
}

internal static class SyncDecision
{
    // Null means absent from a complete scan, never a network or permission error.
    internal static SyncAction ForFile(FileBaseline? baseline, string? localHash, string? remoteVersion)
    {
        if (localHash == null && remoteVersion == null)
        {
            return SyncAction.Forget;
        }

        if (baseline == null)
        {
            if (localHash == null)
            {
                return SyncAction.Download;
            }

            return remoteVersion == null ? SyncAction.Upload : SyncAction.CompareContents;
        }

        if (localHash == null)
        {
            return remoteVersion == baseline.RemoteVersion ? SyncAction.TrashRemote : SyncAction.Conflict;
        }

        if (remoteVersion == null)
        {
            return localHash == baseline.LocalHash ? SyncAction.RecoverLocal : SyncAction.Conflict;
        }

        if (remoteVersion == baseline.RemoteVersion)
        {
            return localHash == baseline.LocalHash ? SyncAction.Unchanged : SyncAction.Upload;
        }

        return localHash == baseline.LocalHash ? SyncAction.Download : SyncAction.CompareContents;
    }
}
