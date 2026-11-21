using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cloudreve.Sync;

namespace Cloudreve.Desktop;

internal sealed record FolderPair(string Local, string Remote, Guid Marker, string RemoteId);

internal sealed class FolderPairStore
{
    private const string MarkerName = ".cloudreve-sync-root";
    private readonly string directory;
    private readonly string settings;

    internal FolderPairStore(string settingsRoot, DriveSession session)
    {
        string identity = JsonSerializer.Serialize(new[] { session.Site, session.UserId, session.DriveId, session.Root });
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        settings = MacFile.NormalizeSystemPath(settingsRoot);
        directory = Path.Combine(settings, key);
    }

    internal FolderPair? Load()
    {
        string path = Path.Combine(directory, "pair.json");
        if (!File.Exists(path))
        {
            return null;
        }

        return JsonSerializer.Deserialize<FolderPair>(File.ReadAllText(path)) ?? throw new InvalidDataException("同步配对记录无效。");
    }

    internal FolderPair Create(string localDirectory, string remote, string remoteId)
    {
        string local = MacFile.NormalizeSystemPath(localDirectory);
        CheckDirectory(local);
        if (IsWithin(settings, local) || IsWithin(local, settings))
        {
            throw new IOException("同步目录不能包含应用的设置目录。");
        }

        if (Load() != null)
        {
            throw new IOException("请先停止原目录配对。");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(remoteId);
        var pair = new FolderPair(local, remote, Guid.NewGuid(), remoteId);
        string marker = Path.Combine(local, MarkerName);
        using (var file = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(file))
        {
            writer.Write(pair.Marker.ToString("N"));
        }

        try
        {
            WriteJson(Path.Combine(directory, "pair.json"), pair, false);
            return pair;
        }
        catch
        {
            Verify(pair);
            File.Delete(marker);
            throw;
        }
    }

    internal void Disconnect(FolderPair pair)
    {
        using (Lock(pair))
        {
            if (Directory.Exists(pair.Local))
            {
                CheckDirectory(pair.Local);
                string marker = Path.Combine(pair.Local, MarkerName);
                if (MacFile.Inspect(marker) == NodeKind.File && new FileInfo(marker).Length <= 128 && File.ReadAllText(marker) == pair.Marker.ToString("N"))
                {
                    File.Delete(marker);
                }
            }

            File.Delete(Path.Combine(directory, "pair.json"));
            File.Delete(BaselinePath(pair));
        }
    }

    internal static void Verify(FolderPair pair)
    {
        CheckDirectory(pair.Local);
        string path = Path.Combine(pair.Local, MarkerName);
        if (MacFile.Inspect(path) != NodeKind.File || new FileInfo(path).Length > 128 || File.ReadAllText(path) != pair.Marker.ToString("N"))
        {
            throw new IOException("本地同步目录已移动、被替换或磁盘未挂载；未同步。");
        }
    }

    internal Dictionary<string, FileBaseline> ReadBaseline(FolderPair pair)
    {
        string path = BaselinePath(pair);
        if (!File.Exists(path))
        {
            return [];
        }

        return JsonSerializer.Deserialize<Dictionary<string, FileBaseline>>(File.ReadAllText(path)) ?? throw new InvalidDataException("同步基线无效。");
    }

    internal void SaveBaseline(FolderPair pair, Dictionary<string, FileBaseline> baseline)
    {
        WriteJson(BaselinePath(pair), baseline);
    }

    internal FileStream Lock(FolderPair pair)
    {
        var lease = new FileStream(BaselinePath(pair) + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            if (Load() != pair)
            {
                throw new IOException("同步配对已更改。");
            }

            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private string BaselinePath(FolderPair pair)
    {
        return Path.Combine(directory, pair.Marker.ToString("N") + ".json");
    }

    internal static void CheckDirectory(string path)
    {
        for (var dir = new DirectoryInfo(path); dir != null; dir = dir.Parent)
        {
            if (MacFile.Inspect(dir.FullName) != NodeKind.Directory)
            {
                throw new IOException("同步目录不存在，或路径包含链接。");
            }
        }
    }

    private static bool IsWithin(string path, string root)
    {
        path = Path.GetFullPath(path).Normalize();
        root = Path.GetFullPath(root).Normalize();
        return path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteJson<T>(string path, T value, bool overwrite = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(output, value);
                output.Flush(true);
            }

            File.Move(temp, path, overwrite);
        }
        finally
        {
            File.Delete(temp);
        }
    }
}
