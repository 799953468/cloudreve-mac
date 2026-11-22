using System.Security.Cryptography;
using Cloudreve.Sync;

namespace Cloudreve.Desktop;

internal sealed record PairNode(string Key, NodeKind Kind, string? Hash = null, DriveEntry? Remote = null, string? LocalId = null, uint LocalGeneration = 0);

internal enum RemoteVersionFailure
{
    UploadStillPending,
    UnsupportedEntry,
    MissingVersion,
    UriMismatch
}

internal sealed class PairInventory
{
    internal const string Recovery = ".cloudreve-recovery";
    internal readonly Dictionary<string, PairNode> Nodes = new(StringComparer.OrdinalIgnoreCase);
    internal readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase);

    internal static bool Reserved(string name)
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
                throw new InvalidDataException(UiText.Get("同步目录包含无法安全映射的文件名。"));
            }
        }
    }

    private bool Add(PairNode node)
    {
        if (Nodes.TryAdd(node.Key, node))
        {
            return true;
        }

        Nodes[node.Key] = node with { Kind = NodeKind.Unsupported, Remote = null };
        return false;
    }

    internal static async Task<string> HashAsync(string path, CancellationToken cancel)
    {
        FolderPairStore.CheckDirectory(Path.GetDirectoryName(path)!);
        if (MacFile.Inspect(path) != NodeKind.File)
        {
            throw new IOException(UiText.Get("文件在同步期间已变化。"));
        }

        using (var input = File.OpenRead(path))
        {
            return Convert.ToHexString(await SHA256.HashDataAsync(input, cancel));
        }
    }

    internal static async Task<PairNode> ReadLocalFileAsync(string key, string path, FileBaseline? previous, CancellationToken cancel)
    {
        var version = MacFile.GetContentVersion(path);
        if (version is { } current && previous is { IsDirectory: false, LocalHash.Length: 64 } &&
            previous.LocalId == current.LocalId && previous.LocalGeneration == current.Generation)
        {
            return new PairNode(key, NodeKind.File, previous.LocalHash, LocalId: current.LocalId, LocalGeneration: current.Generation);
        }

        string hash = await HashAsync(path, cancel);
        // Never checkpoint a digest against metadata sampled only after the read.
        if (version != null && MacFile.GetContentVersion(path) != version)
        {
            throw new IOException(UiText.Get("文件在同步期间已变化。"));
        }

        return new PairNode(key, NodeKind.File, hash, LocalId: version?.LocalId ?? MacFile.Identity(path), LocalGeneration: version?.Generation ?? 0);
    }

    private sealed record LocalScanEntry(string Key, PairNode? Node, List<string>? Children = null);

    private static IEnumerable<string> LocalChildren(string directory, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        FolderPairStore.CheckDirectory(directory);
        foreach (string path in Directory.EnumerateFileSystemEntries(directory))
        {
            cancel.ThrowIfCancellationRequested();
            if (!Reserved(Path.GetFileName(path)))
            {
                yield return path;
            }
        }
    }

    internal static async Task<PairInventory> ReadLocalAsync(FolderPair pair, PairIgnoreRules rules, IReadOnlyDictionary<string, FileBaseline> baseline,
        AdaptiveWorkQueue scan, CancellationToken cancel, Action<int>? reportCount = null, PairScanScope? scope = null)
    {
        var inventory = new PairInventory();
        // Enumeration and cached metadata reads are synchronous even when no file needs hashing.
        await scan.TraverseAsync(Children(pair.Local, cancel), (path, token) => Task.Run(() => ReadEntryAsync(path, token), token),
            (entry, enqueue) =>
            {
                if (entry.Node == null)
                {
                    inventory.Excluded.Add(entry.Key);
                }
                else if (inventory.Add(entry.Node) && entry.Children != null)
                {
                    foreach (string child in entry.Children)
                    {
                        enqueue(child);
                    }
                }

                reportCount?.Invoke(inventory.Nodes.Count);
            }, cancel);
        return inventory;

        IEnumerable<string> Children(string directory, CancellationToken token)
        {
            var children = LocalChildren(directory, token);
            return scope == null ? children : children.Where(path => scope.Visits(Path.GetRelativePath(pair.Local, path).Normalize()));
        }

        async Task<LocalScanEntry> ReadEntryAsync(string path, CancellationToken token)
        {
            string key = Path.GetRelativePath(pair.Local, path).Normalize();
            ValidateKey(key);
            var kind = MacFile.Inspect(path) ?? throw new IOException(UiText.Get("扫描期间本地目录已变化。"));
            if (rules.Excludes(key, kind == NodeKind.Directory))
            {
                return new LocalScanEntry(key, null);
            }

            if (kind == NodeKind.File)
            {
                baseline.TryGetValue(key, out var previous);
                return new LocalScanEntry(key, await ReadLocalFileAsync(key, path, previous, token));
            }

            if (kind == NodeKind.Directory)
            {
                var directory = new PairNode(key, kind, LocalId: MacFile.Identity(path));
                return new LocalScanEntry(key, directory, Children(path, token).ToList());
            }

            return new LocalScanEntry(key, new PairNode(key, kind));
        }
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
            throw new InvalidDataException(UiText.Get("云端条目不属于正在同步的目录。"));
        }
    }

    internal static async Task<DriveEntry> ReadVersionAsync(DriveClient drive, string target, CancellationToken cancel)
    {
        return RequireVersion(await drive.StatAsync(target, cancel), target);
    }

    internal static DriveEntry RequireVersion(DriveEntry file, string target)
    {
        RemoteVersionFailure? reason = null;
        if (file.UploadSessionId != null)
        {
            reason = RemoteVersionFailure.UploadStillPending;
        }
        else if (file.SkipSync || file.IsFolder)
        {
            reason = RemoteVersionFailure.UnsupportedEntry;
        }
        else if (string.IsNullOrEmpty(file.Version))
        {
            reason = RemoteVersionFailure.MissingVersion;
        }
        else if (!SameUri(file.Uri, target))
        {
            reason = RemoteVersionFailure.UriMismatch;
        }

        if (reason.HasValue)
        {
            throw new InvalidDataException(UiText.Get("无法验证云端文件版本。"))
            {
                Data = { [nameof(RemoteVersionFailure)] = reason.Value }
            };
        }

        return file;
    }

    internal static async Task<PairInventory> ReadRemoteAsync(string rootUri, string rootKey, DriveClient drive, PairIgnoreRules rules,
        AdaptiveWorkQueue scan, CancellationToken cancel, Action<int>? reportCount = null, PairScanScope? scope = null)
    {
        var inventory = new PairInventory();
        var root = (Key: rootKey, Uri: rootUri);
        await scan.TraverseAsync([root], async (parent, token) => (Parent: parent, Entries: await drive.ListAsync(parent.Uri, token)), (listing, enqueue) =>
        {
            var parent = listing.Parent;
            foreach (var entry in listing.Entries)
            {
                cancel.ThrowIfCancellationRequested();
                if (Reserved(entry.Name))
                {
                    inventory.Excluded.Add(Path.Combine(parent.Key, entry.Name));
                    continue;
                }

                string key = Path.Combine(parent.Key, entry.Name).Normalize();
                if (scope != null && !scope.Visits(key))
                {
                    continue;
                }

                ValidateRemote(parent.Uri, entry);
                ValidateKey(key);
                if (rules.Excludes(key, entry.IsFolder))
                {
                    inventory.Excluded.Add(key);
                    continue;
                }

                var kind = entry.SkipSync ? NodeKind.Unsupported : entry.IsFolder ? NodeKind.Directory : NodeKind.File;
                if (inventory.Add(new PairNode(key, kind, Remote: entry)) && kind == NodeKind.Directory)
                {
                    enqueue((key, entry.Uri));
                }
            }

            reportCount?.Invoke(inventory.Nodes.Count);
        }, cancel);

        // Resolve only unambiguous files after listings (including all pages) have passed validation.
        var missingVersions = inventory.Nodes.Values.Where(node => node.Kind == NodeKind.File && string.IsNullOrEmpty(node.Remote!.Version));
        await scan.RunAsync(missingVersions, async (node, token) => node with { Remote = await ReadVersionAsync(drive, node.Remote!.Uri, token) },
            node => { inventory.Nodes[node.Key] = node; }, cancel);

        return inventory;
    }
}
