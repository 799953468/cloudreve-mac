using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cloudreve.Sync;

public sealed class SyncEngine
{
    private readonly string local;
    private readonly string remote;
    private readonly string state;
    private readonly SyncResult result = new();
    private Dictionary<string, string> previous = [];
    private readonly Dictionary<string, string> next = [];

    public SyncEngine(string localDirectory, string remoteDirectory, string stateFile)
    {
        local = NormalizeSystemPath(localDirectory);
        remote = NormalizeSystemPath(remoteDirectory);
        state = NormalizeSystemPath(stateFile);
    }

    private static string NormalizeSystemPath(string path)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        foreach (string alias in new[] { "/var", "/tmp", "/etc" })
        {
            if (full == alias || full.StartsWith(alias + "/", StringComparison.Ordinal))
            {
                string expected = "/private" + alias;
                if (Directory.ResolveLinkTarget(alias, true)?.FullName == expected)
                {
                    return expected + full[alias.Length..];
                }
            }
        }

        return full;
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
                previous = JsonSerializer.Deserialize(File.ReadAllText(state), SyncJson.Default.DictionaryStringString)
                    ?? throw new InvalidDataException("同步记录无效。");
            }

            VisitDirectory(local, remote, "");
            string temporary = state + "." + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(next, SyncJson.Default.DictionaryStringString));
                File.Move(temporary, state, true);
            }
            finally
            {
                File.Delete(temporary);
            }
        }

        return result;
    }

    private static bool Within(string path, string root)
    {
        string normalized = path.Normalize();
        string parent = root.Normalize();
        return normalized.Equals(parent, StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static void CheckDirectory(string directory)
    {
        var current = new DirectoryInfo(directory);
        while (current != null)
        {
            if (MacFile.Inspect(current.FullName) != NodeKind.Directory)
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

            bucket.Add(new Entry(name, MacFile.Inspect(path) ?? throw new IOException($"扫描期间文件消失：{path}")));
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
        string? l = Digest(left);
        string? r = Digest(right);
        previous.TryGetValue(name, out string? baseline);
        if (l != null && l == r)
        {
            next[name] = l;
            result.Unchanged++;
        }
        else if (l != null && (r == null || r == baseline))
        {
            CopyChecked(left, right, l, r);
            next[name] = l;
            result.Uploaded++;
        }
        else if (r != null && (l == null || l == baseline))
        {
            CopyChecked(right, left, r, l);
            next[name] = r;
            result.Downloaded++;
        }
        else
        {
            // Keep the common ancestor until the user reconciles both copies.
            if (baseline != null)
            {
                next[name] = baseline;
            }

            result.Issues.Add($"两端内容不同，未覆盖：{name}");
        }
    }

    private static string? Digest(string path)
    {
        CheckDirectory(Path.GetDirectoryName(path)!);
        var type = MacFile.Inspect(path);
        if (type == null)
        {
            return null;
        }

        if (type != NodeKind.File)
        {
            throw new IOException($"文件类型已变化：{path}");
        }

        using (var input = File.OpenRead(path))
        {
            return Convert.ToHexString(SHA256.HashData(input));
        }
    }

    private static void CopyChecked(string source, string destination, string expectedSource, string? expectedDestination)
    {
        string stage = Path.Combine(Path.GetDirectoryName(destination)!, ".cloudreve-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.Copy(source, stage, false);
            if (Digest(stage) != expectedSource || Digest(destination) != expectedDestination)
            {
                throw new IOException($"复制期间文件发生变化，已停止：{destination}");
            }

            File.Move(stage, destination, expectedDestination != null);
        }
        finally
        {
            File.Delete(stage);
        }
    }
}
