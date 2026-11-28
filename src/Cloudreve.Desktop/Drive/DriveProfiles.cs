using Cloudreve.Sync;

namespace Cloudreve.Desktop;

internal sealed class DriveProfiles(SessionFileStore primary)
{
    private readonly Dictionary<SessionFileStore, string> reservations = [];

    internal SessionFileStore Primary { get; } = primary;

    internal SessionFileStore Create()
    {
        return new SessionFileStore(Path.Combine(Primary.DirectoryPath, "profiles", Guid.NewGuid().ToString("N")));
    }

    internal IEnumerable<SessionFileStore> OtherStores()
    {
        string profiles = Path.Combine(Primary.DirectoryPath, "profiles");
        if (!Directory.Exists(profiles))
        {
            yield break;
        }

        FolderPairStore.CheckDirectory(profiles);
        foreach (string path in Directory.EnumerateDirectories(profiles).Order(StringComparer.Ordinal))
        {
            if (Guid.TryParseExact(Path.GetFileName(path), "N", out _) && LocalFile.Inspect(path) == NodeKind.Directory &&
                LocalFile.Inspect(Path.Combine(path, "drives.json")) == NodeKind.File)
            {
                yield return new SessionFileStore(path);
            }
        }
    }

    internal void Reserve(SessionFileStore owner, string local)
    {
        string path = LocalFile.NormalizeSystemPath(local);
        lock (reservations)
        {
            if (Overlaps(path, Primary.DirectoryPath) || reservations.Any(item => item.Key != owner && Overlaps(item.Value, path)))
            {
                throw new IOException(UiText.Get("pair.folderOverlaps"));
            }

            reservations[owner] = path;
        }
    }

    internal void Release(SessionFileStore owner)
    {
        lock (reservations)
        {
            reservations.Remove(owner);
        }
    }

    private static bool Overlaps(string left, string right)
    {
        left = Path.TrimEndingDirectorySeparator(left).Normalize();
        right = Path.TrimEndingDirectorySeparator(right).Normalize();
        return left.Equals(right, StringComparison.OrdinalIgnoreCase) ||
               left.StartsWith(AsDirectory(right), StringComparison.OrdinalIgnoreCase) ||
               right.StartsWith(AsDirectory(left), StringComparison.OrdinalIgnoreCase);
    }

    // "C:" and "/" already end in a separator; other directory paths get one so "/a" does not match "/ab".
    internal static string AsDirectory(string path)
    {
        return Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;
    }
}