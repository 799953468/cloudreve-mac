namespace Cloudreve.Desktop;

// Why a path was left alone. A conflict never changes either copy; it blocks the path (and, for folders, the subtree)
// until the user or a later scan settles it.
internal enum ConflictCause
{
    // A file differs on both sides without sync history, or both sides changed it.
    ContentsDiffer,
    // Deleted here while the cloud copy changed.
    DeletedLocally,
    // Deleted in the cloud while the local copy changed.
    DeletedInCloud,
    // A file on one side and a folder on the other, or a path whose parent is no longer a folder.
    TypeMismatch,
    // A symbolic link, special file, cloud item that must not be synced, or a name this system cannot store.
    UnsupportedItem,
    // Names that differ only by letter case or Unicode normalization.
    NameCollision,
    // The local folder at this path is a different folder than the one last synced.
    DirectoryReplaced,
    // The cloud folder at this path is a different folder than the one last synced.
    CloudFolderReplaced,
    // A folder deleted on one side still holds new or changed items on the other.
    DirectoryNotEmpty,
    // A move or rename that cannot be matched to one source and one destination safely.
    UncertainMove,
    // Sync history from an older version lacks the identities needed to propagate this change safely.
    UnverifiedIdentity,
    // Another client's upload to this path has not finished yet. Abandoned ones are cleaned up after a day.
    UploadInProgress
}

// Conflicts found by one pass. The first cause recorded for a path wins; later checks only confirm it.
internal sealed class ConflictSet
{
    private readonly Dictionary<string, ConflictCause> causes = new(StringComparer.OrdinalIgnoreCase);

    internal IReadOnlyDictionary<string, ConflictCause> Causes
    {
        get { return causes; }
    }

    internal IEnumerable<string> Keys
    {
        get { return causes.Keys; }
    }

    internal int Count
    {
        get { return causes.Count; }
    }

    internal void Add(string key, ConflictCause cause)
    {
        causes.TryAdd(key, cause);
    }

    internal void AddRange(IEnumerable<string> keys, ConflictCause cause)
    {
        foreach (string key in keys)
        {
            Add(key, cause);
        }
    }

    internal bool Contains(string key)
    {
        return causes.ContainsKey(key);
    }
}
