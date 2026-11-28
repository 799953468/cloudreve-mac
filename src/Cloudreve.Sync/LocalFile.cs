namespace Cloudreve.Sync;

internal enum NodeKind
{
    File,
    Directory,
    Unsupported
}

internal enum DirectoryChangeResult
{
    Applied,
    PathChanged,
    NotEmpty
}

// No-follow file system primitives used by the sync engine. Each platform supplies the same contract:
// links and other name-surrogate entries are never traversed or adopted as ordinary files.
internal static partial class LocalFile
{
    internal readonly record struct VolumeIdentity(ulong MountId, Guid Uuid);

    // LocalId names one file system object; Generation changes whenever that object's contents may have changed.
    internal readonly record struct ContentVersion(string LocalId, uint Generation);

    internal static bool TryClone(FileStream source, string destination)
    {
        return OperatingSystem.IsMacOS() && Darwin.TryClone(source, destination);
    }

    internal static DirectoryChangeResult ChangeDirectoryEntry(string path, bool create)
    {
        if (OperatingSystem.IsWindows())
        {
            return Win32.ChangeDirectoryEntry(path, create);
        }

        return OperatingSystem.IsMacOS() ? Darwin.ChangeDirectoryEntry(path, create) : throw Unsupported();
    }

    internal static string NormalizeSystemPath(string path)
    {
        if (OperatingSystem.IsMacOS())
        {
            return Darwin.NormalizeSystemPath(path);
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    internal static VolumeIdentity? ReadVolumeIdentity(string path)
    {
        // Windows identities embed the 64-bit volume serial directly; there is no legacy mount ID to upgrade.
        return OperatingSystem.IsMacOS() ? Darwin.ReadVolumeIdentity(path) : null;
    }

    internal static ContentVersion? GetContentVersion(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return Win32.GetContentVersion(path);
        }

        return OperatingSystem.IsMacOS() ? Darwin.GetContentVersion(path) : throw Unsupported();
    }

    internal static string? Identity(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return Win32.Identity(path);
        }

        return OperatingSystem.IsMacOS() ? Darwin.Identity(path) : throw Unsupported();
    }

    internal static NodeKind? Inspect(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return Win32.Inspect(path);
        }

        return OperatingSystem.IsMacOS() ? Darwin.Inspect(path) : throw Unsupported();
    }

    // Whether a name received from the server can be created as-is on this computer.
    internal static bool IsLocalName(string name)
    {
        return !OperatingSystem.IsWindows() || Win32.IsPortableName(name);
    }

    // Hide app bookkeeping entries in Explorer; dot-prefixed names are already hidden in Finder.
    internal static void Hide(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
        }
    }

    private static PlatformNotSupportedException Unsupported()
    {
        return new PlatformNotSupportedException("Cloudreve sync supports macOS and Windows.");
    }
}
