namespace Cloudreve.Desktop;

// Pair keys are relative paths that always use '/' (the remote separator), on every platform.
// Use these helpers instead of Path.GetDirectoryName/Path.Combine, which switch to '\' on Windows.
internal static class PairKey
{
    // Same contract as Path.GetDirectoryName: null for an empty key, "" for a top-level entry.
    internal static string? Parent(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        int separator = key.LastIndexOf('/');
        return separator < 0 ? "" : key[..separator];
    }

    internal static string Combine(string parent, string name)
    {
        return parent.Length == 0 ? name : parent + "/" + name;
    }

    // Convert a path relative to the local root (or a watcher event name) to a normalized key.
    internal static string FromLocal(string relative)
    {
        if (Path.DirectorySeparatorChar != '/')
        {
            relative = relative.Replace(Path.DirectorySeparatorChar, '/');
        }

        return relative.Normalize();
    }
}
