using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cloudreve.Sync;

internal sealed record LocalFileState(string Hash, LocalFile.ContentVersion? Local = null, LocalFile.ContentVersion? Remote = null);
internal sealed record LocalSyncState(int FormatVersion, Dictionary<string, LocalFileState> Files);

public sealed class SyncEngine
{
    private readonly string local;
    private readonly string remote;
    private readonly string state;
    private readonly SyncResult result = new();
    private Dictionary<string, LocalFileState> previous = [];
    private readonly Dictionary<string, LocalFileState> next = [];

    public SyncEngine(string localDirectory, string remoteDirectory, string stateFile)
    {
        local = LocalFile.NormalizeSystemPath(localDirectory);
        remote = LocalFile.NormalizeSystemPath(remoteDirectory);
        state = LocalFile.NormalizeSystemPath(stateFile);
    }

    public SyncResult Run()
    {
        CheckDirectory(local);
        CheckDirectory(remote);
        if (Within(local, remote) || Within(remote, local) || Within(state, local) || Within(state, remote))
        {
            throw new IOException("本地、云端和状态文件的路径不能重叠。");
        }

        string stateDirectory = Path.GetDirectoryName(state)!;
        Directory.CreateDirectory(stateDirectory);
        CheckDirectory(stateDirectory);
        using (var lease = new FileStream(state + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            if (File.Exists(state))
            {
                using (var input = File.OpenRead(state))
                using (var json = JsonDocument.Parse(input))
                {
                    if (json.RootElement.TryGetProperty("formatVersion", out var format) && format.ValueKind == JsonValueKind.Number)
                    {
                        var saved = json.RootElement.Deserialize(SyncJson.Default.LocalSyncState);
                        previous = saved is { FormatVersion: 1, Files: not null } ? saved.Files : throw new InvalidDataException("同步记录无效。");
                    }
                    else
                    {
#pragma warning disable CS0618 // Keep the one-time migration available in this release.
                        previous = ReadLegacyJsonState(json.RootElement);
#pragma warning restore CS0618
                    }
                }
            }

            VisitDirectory(local, remote, "");
            string temporary = state + "." + Guid.NewGuid().ToString("N");
            try
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(output, new LocalSyncState(1, next), SyncJson.Default.LocalSyncState);
                }
                File.Move(temporary, state, true);
            }
            finally
            {
                File.Delete(temporary);
            }
        }

        return result;
    }

    [Obsolete("Unversioned JSON sync state support will be removed in the next release. New state files include formatVersion.")]
    private static Dictionary<string, LocalFileState> ReadLegacyJsonState(JsonElement root)
    {
        var legacy = root.Deserialize(SyncJson.Default.DictionaryStringString) ?? throw new InvalidDataException("同步记录无效。");
        return legacy.ToDictionary(entry => entry.Key, entry => new LocalFileState(entry.Value));
    }

    private static bool Within(string path, string root)
    {
        string normalized = path.Normalize();
        string parent = root.Normalize();
        return normalized.Equals(parent, StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static void CheckDirectory(string directory)
    {
        var current = new DirectoryInfo(directory);
        while (current != null)
        {
            if (LocalFile.Inspect(current.FullName) != NodeKind.Directory)
            {
                throw new IOException($"目录不存在、已卸载或为符号链接：{current.FullName}");
            }

            current = current.Parent;
        }
    }

    private sealed record Entry(string Name, NodeKind Kind);

    private static Dictionary<string, List<Entry>> Entries(string directory)
    {
        CheckDirectory(directory);
        var entries = new Dictionary<string, List<Entry>>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in Directory.EnumerateFileSystemEntries(directory))
        {
            string name = Path.GetFileName(path);
            if (name.StartsWith('.'))
            {
                continue;
            }

            string key = name.Normalize(NormalizationForm.FormC);
            if (!entries.TryGetValue(key, out var bucket))
            {
                bucket = [];
                entries.Add(key, bucket);
            }

            bucket.Add(new Entry(name, LocalFile.Inspect(path) ?? throw new IOException($"扫描期间文件消失：{path}")));
        }

        return entries;
    }

    private void VisitDirectory(string left, string right, string relative)
    {
        var localEntries = Entries(left);
        var remoteEntries = Entries(right);
        foreach (string key in localEntries.Keys.Union(remoteEntries.Keys, StringComparer.OrdinalIgnoreCase))
        {
            localEntries.TryGetValue(key, out var a);
            remoteEntries.TryGetValue(key, out var b);
            string name = Path.Combine(relative, key);
            Entry? l = a?.First();
            Entry? r = b?.First();
            string lpath = Path.Combine(left, l?.Name ?? r!.Name);
            string rpath = Path.Combine(right, r?.Name ?? l!.Name);
            if (a?.Count > 1 || b?.Count > 1 || (l != null && r != null &&
                (l.Kind != r.Kind || l.Name.Normalize() != r.Name.Normalize())))
            {
                result.Issues.Add($"名称或类型冲突：{name}");
                continue;
            }

            switch (l?.Kind ?? r!.Kind)
            {
                case NodeKind.Unsupported:
                    result.Issues.Add($"已跳过链接或特殊文件：{name}");
                    break;
                case NodeKind.Directory:
                    if (l == null || r == null)
                    {
                        CheckDirectory(l == null ? left : right);
                        Directory.CreateDirectory(l == null ? lpath : rpath);
                    }

                    VisitDirectory(lpath, rpath, name);
                    break;
                case NodeKind.File:
                    ReconcileFile(name, lpath, rpath);
                    break;
            }
        }
    }

    private void ReconcileFile(string name, string left, string right)
    {
        previous.TryGetValue(name, out var known);
        string? baseline = known?.Hash;
        string? l = Digest(left, baseline, known?.Local, out var localVersion);
        string? r = Digest(right, baseline, known?.Remote, out var remoteVersion);
        if (l != null && l == r)
        {
            next[name] = new LocalFileState(l, localVersion, remoteVersion);
            result.Unchanged++;
        }
        else if (l != null && (r == null || r == baseline))
        {
            remoteVersion = CopyChecked(left, right, l, r);
            next[name] = new LocalFileState(l, localVersion, remoteVersion);
            result.Uploaded++;
        }
        else if (r != null && (l == null || l == baseline))
        {
            localVersion = CopyChecked(right, left, r, l);
            next[name] = new LocalFileState(r, localVersion, remoteVersion);
            result.Downloaded++;
        }
        else
        {
            // Keep the common ancestor until the user reconciles both copies.
            if (baseline != null)
            {
                next[name] = known!;
            }

            result.Issues.Add($"两端内容不同，未覆盖：{name}");
        }
    }

    private static string? Digest(string path, string? cachedHash, LocalFile.ContentVersion? cachedVersion, out LocalFile.ContentVersion? version)
    {
        CheckDirectory(Path.GetDirectoryName(path)!);
        var type = LocalFile.Inspect(path);
        version = null;
        if (type == null)
        {
            return null;
        }

        if (type != NodeKind.File)
        {
            throw new IOException($"文件类型已变化：{path}");
        }

        version = LocalFile.GetContentVersion(path);
        if (version.HasValue && version == cachedVersion && cachedHash?.Length == 64)
        {
            return cachedHash;
        }
        using (var input = File.OpenRead(path))
        {
            string hash = Convert.ToHexString(SHA256.HashData(input));
            if (version.HasValue && LocalFile.GetContentVersion(path) != version)
            {
                throw new IOException($"读取期间文件发生变化：{path}");
            }
            return hash;
        }
    }

    private static LocalFile.ContentVersion? CopyChecked(string source, string destination, string expectedSource, string? expectedDestination)
    {
        string stage = Path.Combine(Path.GetDirectoryName(destination)!, ".cloudreve-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.Copy(source, stage, false);
            if (Digest(stage, null, null, out var stagedVersion) != expectedSource || Digest(destination, null, null, out _) != expectedDestination)
            {
                throw new IOException($"复制期间文件发生变化，已停止：{destination}");
            }

            File.Move(stage, destination, expectedDestination != null);
            return stagedVersion;
        }
        finally
        {
            File.Delete(stage);
        }
    }
}
