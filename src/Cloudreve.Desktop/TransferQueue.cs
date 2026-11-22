using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Cloudreve.Sync;
using Serilog;
using Serilog.Events;

namespace Cloudreve.Desktop;

internal sealed record FileTransfer(string Key, SyncAction Action, PairNode? Local, DriveEntry? Remote, FileBaseline? Previous);
internal sealed record QueuedFile(Guid Id, FileTransfer File);
internal sealed record UploadCheckpoint(JsonElement Ticket, int NextPart, List<string> Tags, bool Merged = false)
{
    internal string SessionId { get { return DesktopLogin.Required(Ticket, "session_id"); } }

    internal string StorageKind
    {
        get
        {
            if (Ticket.TryGetProperty("storage_policy", out var policy) && policy.ValueKind == JsonValueKind.Object &&
                !(policy.TryGetProperty("relay", out var relay) && relay.ValueKind == JsonValueKind.True))
            {
                return DesktopLogin.Required(policy, "type");
            }

            return "local";
        }
    }

    internal bool Expired
    {
        get
        {
            return Ticket.TryGetProperty("expires", out var value) && value.ValueKind == JsonValueKind.Number &&
                   value.TryGetInt64(out long seconds) && seconds <= DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
    }
}

internal sealed record DownloadCheckpoint(long Length, string Hash, bool Complete);

// The pair lease protects the manifest. Each worker only writes checkpoints for its own ID.
internal sealed class TransferQueue(string directory)
{
    private const string Manifest = "pending.json";
    private QueuedFile[] entries = [];
    private Dictionary<string, QueuedFile> byKey = [];

    internal IReadOnlyList<QueuedFile> Entries { get { return entries; } }

    internal static TransferQueue Open(string directory)
    {
        var queue = new TransferQueue(directory);
        var saved = ReadCache(Path.Combine(directory, Manifest), DesktopJson.Default.QueuedFileArray) ?? [];
        var ids = new HashSet<Guid>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in saved)
        {
            if (entry?.File == null || !IsSafeKey(entry.File.Key) || entry.Id == Guid.Empty || !ids.Add(entry.Id) || !keys.Add(entry.File.Key))
            {
                saved = [];
                break;
            }
        }

        queue.SetEntries(saved);
        return queue;
    }

    internal static bool IsSafeKey(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        try
        {
            PairInventory.ValidateKey(key);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    internal bool RestoreVerified(Dictionary<string, FileBaseline> baseline, PairIgnoreRules rules)
    {
        bool changed = false;
        foreach (var entry in entries)
        {
            var job = entry.File;
            baseline.TryGetValue(job.Key, out var previous);
            if (previous != job.Previous || rules.Excludes(job.Key))
            {
                continue;
            }

            var result = ReadCache(Files(entry).VerifiedPath, DesktopJson.Default.FileBaseline);
            if (result is not { IsDirectory: false, LocalHash.Length: 64, RemoteVersion.Length: > 0 } || result == previous ||
                job.Action != SyncAction.Download && result.LocalHash != job.Local?.Hash ||
                job.Action != SyncAction.Upload && result.RemoteVersion != job.Remote?.Version)
            {
                continue;
            }

            baseline[job.Key] = result;
            changed = true;
        }

        return changed;
    }

    internal static T? ReadCache<T>(string path, JsonTypeInfo<T> typeInfo) where T : class
    {
        var kind = MacFile.Inspect(path);
        if (kind == null)
        {
            return null;
        }

        if (kind != NodeKind.File)
        {
            throw new IOException(UiText.Get("传输缓存不是普通文件。"));
        }

        using (var input = File.OpenRead(path))
        {
            try
            {
                return JsonSerializer.Deserialize(input, typeInfo);
            }
            catch (JsonException ex)
            {
                Log.Logger.ForContext("CacheType", typeof(T).Name).WriteFailure(LogEventLevel.Warning, "Read transfer cache", ex);
                return null;
            }
        }
    }

    internal QueuedFile[] Reconcile(List<FileTransfer> pending, Func<string, bool>? isDeferred = null)
    {
        // Only fresh scan results may run. Preserve the position and checkpoints of exact matches.
        var remaining = pending.ToDictionary(file => file.Key);
        var next = new List<QueuedFile>(pending.Count);
        foreach (var entry in entries)
        {
            if (isDeferred?.Invoke(entry.File.Key) == true)
            {
                // Keep its existing checkpoint, but do not run work from a stale directory scan.
                next.Add(entry);
                remaining.Remove(entry.File.Key);
                continue;
            }

            if (remaining.TryGetValue(entry.File.Key, out var file) && entry.File == file)
            {
                next.Add(new QueuedFile(entry.Id, file));
                remaining.Remove(file.Key);
            }
        }

        foreach (var file in pending)
        {
            if (remaining.ContainsKey(file.Key))
            {
                next.Add(new QueuedFile(Guid.NewGuid(), file));
            }
        }

        Save(next.ToArray());
        // Also reap checkpoints left behind by a crash between publishing the manifest and cleanup.
        var live = entries.Select(entry => entry.Id).ToHashSet();
        foreach (string path in Directory.EnumerateFiles(directory))
        {
            string stem = Path.GetFileNameWithoutExtension(path);
            bool abandonedWrite = Guid.TryParseExact(Path.GetExtension(path).TrimStart('.'), "N", out _) &&
                (stem == Manifest || Path.GetExtension(stem) is ".upload" or ".download" or ".verified");
            if (abandonedWrite || Guid.TryParseExact(stem, "N", out var id) && !live.Contains(id))
            {
                File.Delete(path);
            }
        }

        return isDeferred == null ? entries : entries.Where(entry => !isDeferred(entry.File.Key)).ToArray();
    }

    internal void Complete(HashSet<string> keys)
    {
        if (keys.Count > 0)
        {
            Save(entries.Where(entry => !keys.Contains(entry.File.Key)).ToArray());
        }
    }

    private void Save(QueuedFile[] next)
    {
        if (!entries.SequenceEqual(next))
        {
            FolderPairStore.WriteJson(Path.Combine(directory, Manifest), next, DesktopJson.Default.QueuedFileArray);
            var keep = next.Select(entry => entry.Id).ToHashSet();
            foreach (var entry in entries.Where(entry => !keep.Contains(entry.Id)))
            {
                Files(entry).Delete();
            }

            SetEntries(next);
        }
    }

    private void SetEntries(QueuedFile[] next)
    {
        entries = next;
        byKey = entries.ToDictionary(entry => entry.File.Key);
    }

    internal QueuedFile? Find(string key)
    {
        return byKey.GetValueOrDefault(key);
    }

    internal TransferFiles Files(QueuedFile entry)
    {
        return new TransferFiles(Path.Combine(directory, entry.Id.ToString("N")));
    }
}

internal sealed class TransferFiles(string prefix)
{
    internal string PartPath { get { return prefix + ".part"; } }
    internal string DownloadPath { get { return prefix + ".download"; } }
    internal string UploadPath { get { return prefix + ".upload"; } }
    internal string VerifiedPath { get { return prefix + ".verified"; } }
    private string ReceiptsPath { get { return prefix + ".receipts"; } }

    internal sealed record Receipt(int NextPart, string? Tag, bool Merged);

    internal void StartUpload(JsonElement ticket)
    {
        File.Delete(ReceiptsPath);
        FolderPairStore.WriteJson(UploadPath, ticket, DesktopJson.Default.JsonElement);
    }

    internal void ConfirmPart(int nextPart, string? tag, bool merged = false)
    {
        // Each acknowledged part costs one small append regardless of the number of signed URLs.
        var options = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var output = new FileStream(ReceiptsPath, options))
        {
            JsonSerializer.Serialize(output, new Receipt(nextPart, tag, merged), DesktopJson.Default.Receipt);
            output.WriteByte((byte)'\n');
            output.Flush(true);
        }
    }

    internal UploadCheckpoint? ReadUpload()
    {
        var ticket = TransferQueue.ReadCache(UploadPath, DesktopJson.Default.JsonDocument);
        if (ticket == null)
        {
            return null;
        }

        UploadCheckpoint state;
        using (ticket)
        {
            var root = ticket.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("session_id", out var id) ||
                id.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(id.GetString()))
            {
                return null;
            }

            state = new UploadCheckpoint(root.Clone(), 0, []);
        }

        var kind = MacFile.Inspect(ReceiptsPath);
        if (kind == null)
        {
            return state;
        }

        if (kind != NodeKind.File)
        {
            throw new IOException(UiText.Get("传输缓存不是普通文件。"));
        }

        byte[] journal = File.ReadAllBytes(ReceiptsPath);
        int position = 0;
        while (position < journal.Length)
        {
            int end = Array.IndexOf(journal, (byte)'\n', position);
            if (end < 0)
            {
                break;
            }

            Receipt? receipt;
            try
            {
                receipt = JsonSerializer.Deserialize(journal.AsSpan(position, end - position), DesktopJson.Default.Receipt);
            }
            catch (JsonException ex)
            {
                Log.Logger.ForContext("VerifiedParts", state.NextPart).WriteFailure(LogEventLevel.Debug, "Read upload receipt journal", ex);
                break;
            }

            if (receipt == null || state.Merged || receipt.NextPart != state.NextPart + (receipt.Merged ? 0 : 1))
            {
                break;
            }

            if (receipt.Tag != null)
            {
                state.Tags.Add(receipt.Tag);
            }

            state = state with { NextPart = receipt.NextPart, Merged = receipt.Merged };
            position = end + 1;
        }

        if (position != journal.Length)
        {
            // An incomplete final receipt is not an acknowledgement. Replay its part.
            using (var output = File.OpenWrite(ReceiptsPath))
            {
                output.SetLength(position);
                output.Flush(true);
            }
        }

        return state;
    }

    internal void Delete()
    {
        File.Delete(VerifiedPath);
        File.Delete(ReceiptsPath);
        File.Delete(UploadPath);
        File.Delete(PartPath);
        File.Delete(DownloadPath);
    }
}
