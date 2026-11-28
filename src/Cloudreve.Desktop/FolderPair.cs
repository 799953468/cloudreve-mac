using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Cloudreve.Sync;

namespace Cloudreve.Desktop;

internal sealed record FolderPair(string Local, string Remote, Guid Marker, string RemoteId);

internal enum DirectoryCheckFailure
{
    Missing,
    FoundFile,
    LinkOrSpecialEntry,
    RootMarkerMismatch
}

internal sealed class FolderPairStore
{
    private const string MarkerName = ".cloudreve-sync-root";
    private readonly string directory;
    private readonly string settings;

    internal FolderPairStore(string settingsRoot, DriveSession session)
    {
        string identity = JsonSerializer.Serialize(new[] { session.Site, session.UserId, session.DriveId, session.Root }, DesktopJson.Default.StringArray);
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        settings = LocalFile.NormalizeSystemPath(settingsRoot);
        directory = Path.Combine(settings, key);
    }

    internal FolderPair? Load()
    {
        string path = Path.Combine(directory, "pair.json");
        if (!File.Exists(path))
        {
            return null;
        }

        return JsonSerializer.Deserialize(File.ReadAllText(path), DesktopJson.Default.FolderPair) ?? throw new InvalidDataException(UiText.Get("同步配对记录无效。"));
    }

    internal FolderPair Create(string localDirectory, string remote, string remoteId)
    {
        string local = LocalFile.NormalizeSystemPath(localDirectory);
        CheckDirectory(local);
        if (IsWithin(settings, local) || IsWithin(local, settings))
        {
            throw new IOException(UiText.Get("同步目录不能包含应用的设置目录。"));
        }

        if (Load() != null)
        {
            throw new IOException(UiText.Get("请先停止原目录配对。"));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(remoteId);
        var pair = new FolderPair(local, remote, Guid.NewGuid(), remoteId);
        string marker = Path.Combine(local, MarkerName);
        using (var file = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(file))
        {
            writer.Write(pair.Marker.ToString("N"));
        }

        LocalFile.Hide(marker);

        try
        {
            WriteJson(Path.Combine(directory, "pair.json"), pair, DesktopJson.Default.FolderPair, overwrite: false);
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
                if (LocalFile.Inspect(marker) == NodeKind.File && new FileInfo(marker).Length <= 128 && File.ReadAllText(marker) == pair.Marker.ToString("N"))
                {
                    File.Delete(marker);
                }
            }

            string cache = CachePath(pair);
            if (LocalFile.Inspect(cache) == NodeKind.Directory)
            {
                Directory.Delete(cache, true);
            }

            File.Delete(Path.Combine(directory, "pair.json"));
            File.Delete(BaselinePath(pair));
            File.Delete(BaselinePath(pair) + ".migrated");
            File.Delete(Path.ChangeExtension(BaselinePath(pair), ".sqlite"));
            File.Delete(RulesPath(pair));
        }
    }

    internal static void Verify(FolderPair pair)
    {
        CheckDirectory(pair.Local);
        string path = Path.Combine(pair.Local, MarkerName);
        if (LocalFile.Inspect(path) != NodeKind.File || new FileInfo(path).Length > 128 || File.ReadAllText(path) != pair.Marker.ToString("N"))
        {
            throw new IOException(UiText.Get("本地同步目录已移动、被替换或磁盘未挂载；未同步。"))
            {
                Data = { [nameof(DirectoryCheckFailure)] = DirectoryCheckFailure.RootMarkerMismatch }
            };
        }
    }

    internal Dictionary<string, FileBaseline> ReadBaseline(FolderPair pair)
    {
        using (var database = OpenBaseline(pair))
        {
            return database.Read();
        }
    }

    internal void SaveBaseline(FolderPair pair, Dictionary<string, FileBaseline> baseline)
    {
        using (var database = OpenBaseline(pair))
        {
            database.SaveChanges(database.Read(), baseline);
        }
    }

    internal BaselineDatabase OpenBaseline(FolderPair pair)
    {
        return new BaselineDatabase(BaselinePath(pair));
    }

    private string CachePath(FolderPair pair)
    {
        return Path.Combine(directory, pair.Marker.ToString("N") + ".transfers");
    }

    internal string TransferDirectory(FolderPair pair)
    {
        CheckDirectory(directory);
        string path = CachePath(pair);
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            CheckDirectory(path);
            return path;
        }

        const UnixFileMode privateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        Directory.CreateDirectory(path, privateDirectory);
        CheckDirectory(path);
        if (File.GetUnixFileMode(path) != privateDirectory)
        {
            File.SetUnixFileMode(path, privateDirectory);
        }

        return path;
    }

    private string RulesPath(FolderPair pair)
    {
        return Path.Combine(directory, pair.Marker.ToString("N") + ".ignore.json");
    }

    internal string ReadRules(FolderPair pair)
    {
        string path = RulesPath(pair);
        if (!File.Exists(path))
        {
            return "";
        }

        return JsonSerializer.Deserialize(File.ReadAllText(path), DesktopJson.Default.String) ?? throw new InvalidDataException(UiText.Get("配对忽略规则无效。"));
    }

    internal void SaveRules(FolderPair pair, string text)
    {
        var next = new PairIgnoreRules(text);
        using (Lock(pair))
        {
            var previous = new PairIgnoreRules(ReadRules(pair));
            var baseline = ReadBaseline(pair);
            var queue = TransferQueue.Open(TransferDirectory(pair));
            var ignored = queue.Entries.Where(entry => previous.Excludes(entry.File.Key) || next.Excludes(entry.File.Key))
                .Select(entry => entry.File.Key).ToHashSet();
            queue.Complete(ignored);
            // Re-included paths must merge from scratch; never replay deletions from the ignored interval.
            foreach (string key in baseline.Keys.ToArray())
            {
                if (previous.Excludes(key, baseline[key].IsDirectory) || next.Excludes(key, baseline[key].IsDirectory))
                {
                    baseline.Remove(key);
                }
            }

            SaveBaseline(pair, baseline);
            WriteJson(RulesPath(pair), text, DesktopJson.Default.String);
        }
    }

    internal FileStream Lock(FolderPair pair)
    {
        var lease = new FileStream(BaselinePath(pair) + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            if (Load() != pair)
            {
                throw new IOException(UiText.Get("同步配对已更改。"));
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
            var kind = LocalFile.Inspect(dir.FullName);
            if (kind != NodeKind.Directory)
            {
                var reason = kind switch
                {
                    null => DirectoryCheckFailure.Missing,
                    NodeKind.File => DirectoryCheckFailure.FoundFile,
                    _ => DirectoryCheckFailure.LinkOrSpecialEntry
                };
                throw new IOException(UiText.Get("同步目录不存在，或路径包含链接。"))
                {
                    Data = { [nameof(DirectoryCheckFailure)] = reason }
                };
            }
        }
    }

    private static bool IsWithin(string path, string root)
    {
        path = Path.GetFullPath(path).Normalize();
        root = Path.GetFullPath(root).Normalize();
        return path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(DriveProfiles.AsDirectory(root), StringComparison.OrdinalIgnoreCase);
    }

    internal static void WriteJson<T>(string path, T value, JsonTypeInfo<T> typeInfo, bool overwrite = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            using (var output = new FileStream(temp, options))
            {
                JsonSerializer.Serialize(output, value, typeInfo);
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
