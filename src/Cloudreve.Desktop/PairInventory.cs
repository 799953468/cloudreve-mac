using System.Security.Cryptography;
using Cloudreve.Sync;

namespace Cloudreve.Desktop;

internal sealed record PairNode(string Key, NodeKind Kind, string? Hash = null, DriveEntry? Remote = null);

internal sealed class PairInventory
{
    internal const string Recovery = ".cloudreve-recovery";
    internal readonly Dictionary<string, PairNode> Nodes = new(StringComparer.OrdinalIgnoreCase);

    private static bool Reserved(string name)
    {
        return name.Equals(".cloudreve-sync-root", StringComparison.OrdinalIgnoreCase) ||
            name.Equals(Recovery, StringComparison.OrdinalIgnoreCase) || name.Equals(".DS_Store", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith(".cloudreve-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".part", StringComparison.OrdinalIgnoreCase);
    }

    internal static void ValidateKey(string key)
    {
        foreach (string part in key.Split('/'))
        {
            if (string.IsNullOrWhiteSpace(part) || part is "." or ".." || part.IndexOfAny(['\\', '\0']) >= 0 || Reserved(part))
            {
                throw new InvalidDataException("同步目录包含无法安全映射的文件名。");
            }
        }
    }

    private bool Add(PairNode node)
    {
        if (Nodes.TryAdd(node.Key, node))
        {
            return true;
        }

        Nodes[node.Key] = node with { Kind = NodeKind.Unsupported };
        return false;
    }

    internal static async Task<string> HashAsync(string path, CancellationToken cancel)
    {
        FolderPairStore.CheckDirectory(Path.GetDirectoryName(path)!);
        if (MacFile.Inspect(path) != NodeKind.File)
        {
            throw new IOException("文件在同步期间已变化。");
        }

        using (var input = File.OpenRead(path))
        {
            return Convert.ToHexString(await SHA256.HashDataAsync(input, cancel));
        }
    }

    internal static async Task<PairInventory> ReadLocalAsync(FolderPair pair, CancellationToken cancel)
    {
        var inventory = new PairInventory();
        var pending = new Stack<string>();
        pending.Push(pair.Local);
        while (pending.TryPop(out string? parent))
        {
            FolderPairStore.CheckDirectory(parent);
            foreach (string path in Directory.EnumerateFileSystemEntries(parent))
            {
                cancel.ThrowIfCancellationRequested();
                if (Reserved(Path.GetFileName(path)))
                {
                    continue;
                }

                string key = Path.GetRelativePath(pair.Local, path).Normalize();
                ValidateKey(key);
                var kind = MacFile.Inspect(path) ?? throw new IOException("扫描期间本地目录已变化。");
                string? hash = kind == NodeKind.File ? await HashAsync(path, cancel) : null;
                if (inventory.Add(new PairNode(key, kind, hash)) && kind == NodeKind.Directory)
                {
                    pending.Push(path);
                }
            }
        }

        return inventory;
    }

    internal static bool SameUri(string left, string right)
    {
        var a = new Uri(left, UriKind.Absolute);
        var b = new Uri(right, UriKind.Absolute);
        // Decode components separately: an encoded slash must not introduce a directory.
        return a.Scheme == "cloudreve" && b.Scheme == a.Scheme && a.Authority == b.Authority && a.UserInfo == b.UserInfo &&
            a.Fragment.Length == 0 && b.Fragment.Length == 0 && a.Query == b.Query &&
            a.Segments.Select(Uri.UnescapeDataString).SequenceEqual(b.Segments.Select(Uri.UnescapeDataString));
    }

    internal static void ValidateRemote(string parent, DriveEntry entry)
    {
        if (!SameUri(DriveClient.ChildUri(parent, entry.Name), entry.Uri))
        {
            throw new InvalidDataException("云端条目不属于正在同步的目录。");
        }
    }

    internal static async Task<DriveEntry> ReadVersionAsync(DriveClient drive, string target, CancellationToken cancel)
    {
        var file = await drive.StatAsync(target, cancel);
        if (file.SkipSync || file.IsFolder || string.IsNullOrEmpty(file.Version) || !SameUri(file.Uri, target))
        {
            throw new InvalidDataException("无法验证云端文件版本。");
        }

        return file;
    }

    internal static async Task<PairInventory> ReadRemoteAsync(FolderPair pair, DriveClient drive, CancellationToken cancel)
    {
        var inventory = new PairInventory();
        var pending = new Stack<(string Key, string Uri)>();
        pending.Push(("", pair.Remote));
        while (pending.TryPop(out var parent))
        {
            foreach (var entry in await drive.ListAsync(parent.Uri, cancel))
            {
                cancel.ThrowIfCancellationRequested();
                if (Reserved(entry.Name))
                {
                    continue;
                }

                string key = Path.Combine(parent.Key, entry.Name).Normalize();
                ValidateRemote(parent.Uri, entry);
                ValidateKey(key);
                var kind = entry.SkipSync ? NodeKind.Unsupported : entry.IsFolder ? NodeKind.Directory : NodeKind.File;
                var file = kind == NodeKind.File && string.IsNullOrEmpty(entry.Version) ? await ReadVersionAsync(drive, entry.Uri, cancel) : entry;
                if (inventory.Add(new PairNode(key, kind, Remote: file)) && kind == NodeKind.Directory)
                {
                    pending.Push((key, entry.Uri));
                }
            }
        }

        return inventory;
    }
}
