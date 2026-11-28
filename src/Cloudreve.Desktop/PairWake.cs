using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Serilog;
using Serilog.Events;

namespace Cloudreve.Desktop;

internal sealed class PairWake(TimeSpan pollInterval)
{
    private const int MaxPaths = 1024;
    private readonly object gate = new();
    private readonly HashSet<string> pending = new(StringComparer.OrdinalIgnoreCase);
    private bool full;
    private bool remoteCatchUp;
    private long lastFull = Stopwatch.GetTimestamp();
    private readonly Channel<bool> signals = Channel.CreateBounded<bool>(1);
    private volatile bool localAvailable;
    private volatile bool remoteAvailable;

    internal PairWake() : this(TimeSpan.FromSeconds(30))
    {
    }

    internal void Signal()
    {
        lock (gate)
        {
            full = true;
            pending.Clear();
            signals.Writer.TryWrite(true);
        }
    }

    internal void RequestRemoteCatchUp()
    {
        lock (gate)
        {
            remoteCatchUp = true;
            signals.Writer.TryWrite(true);
        }
    }

    internal void BeginFullRemoteInventory()
    {
        lock (gate)
        {
            // This listing covers subscription gaps reported before its first request.
            remoteCatchUp = false;
            if (!full && pending.Count == 0)
            {
                signals.Reader.TryRead(out _);
            }
        }
    }

    internal void SignalPaths(params IEnumerable<string?> paths)
    {
        lock (gate)
        {
            foreach (string? path in paths)
            {
                if (full)
                {
                    break;
                }

                if (!IsRelevant(path))
                {
                    continue;
                }

                if (!TransferQueue.IsSafeKey(path))
                {
                    full = true;
                    break;
                }

                pending.Add(path!.Normalize());
                if (pending.Count > MaxPaths)
                {
                    full = true;
                    break;
                }
            }

            if (full || pending.Count != 0)
            {
                signals.Writer.TryWrite(true);
            }
        }
    }

    internal void SetRemoteAvailable(bool available)
    {
        remoteAvailable = available;
    }

    internal async Task<PairScanScope?> WaitForNextScanAsync(bool retry, CancellationToken cancel, PairScanScope? retryScope = null)
    {
        if (retry)
        {
            // Notifications during a failed pass must not bypass the retry delay.
            await Task.Delay(pollInterval, cancel);
            if (retryScope != null)
            {
                SignalPaths(retryScope.Targets);
            }
            return TakeScope(retryScope == null || PollDue());
        }

        while (true)
        {
            // Recheck both watchers on short ticks without walking either tree.
            bool signaled = await WaitAsync(pollInterval, cancel);
            bool poll = PollDue();
            if (signaled || poll)
            {
                return TakeScope(poll);
            }
        }
    }

    private bool PollDue()
    {
        return (!localAvailable || !remoteAvailable) && Stopwatch.GetElapsedTime(lastFull) >= pollInterval;
    }

    private PairScanScope? TakeScope(bool forceFull)
    {
        lock (gate)
        {
            signals.Reader.TryRead(out _);
            PairScanScope? scope = null;
            if (forceFull || full || remoteCatchUp)
            {
                lastFull = Stopwatch.GetTimestamp();
            }
            else
            {
                scope = new PairScanScope(pending);
            }

            pending.Clear();
            full = false;
            remoteCatchUp = false;
            return scope;
        }
    }

    internal async Task<bool> WaitAsync(TimeSpan interval, CancellationToken cancel)
    {
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel))
        {
            timeout.CancelAfter(interval);
            try
            {
                await signals.Reader.ReadAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
            {
                return false;
            }
        }

        await Task.Delay(TimeSpan.FromMilliseconds(200), cancel);
        signals.Reader.TryRead(out _);
        return true;
    }

    internal FileSystemWatcher? WatchLocal(string root)
    {
        FileSystemWatcher? watcher = null;
        try
        {
            watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                // Windows drops the whole batch when this buffer overflows; 64 KB is the largest size it allows on network shares.
                InternalBufferSize = 64 * 1024
            };
            watcher.Changed += OnLocalChange;
            watcher.Created += OnLocalChange;
            watcher.Deleted += OnLocalChange;
            watcher.Renamed += (_, change) =>
            {
                SignalPaths(LocalKey(change.OldName), LocalKey(change.Name));
            };
            watcher.Error += (_, error) =>
            {
                if (error.GetException() is not InternalBufferOverflowException)
                {
                    localAvailable = false;
                }
                Log.Logger.WriteFailure(LogEventLevel.Warning, "Watch local changes; requesting a rescan", error.GetException());
                Signal();
            };
            localAvailable = true;
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
        {
            localAvailable = false;
            watcher?.Dispose();
            Log.Logger.WriteFailure(LogEventLevel.Warning, "Watch local folder pair", ex);
            return null;
        }
    }

    private void OnLocalChange(object sender, FileSystemEventArgs change)
    {
        SignalPaths(LocalKey(change.Name));
    }

    private static string? LocalKey(string? name)
    {
        return name == null ? null : PairKey.FromLocal(name);
    }

    internal static bool IsRelevant(string? relativePath)
    {
        return relativePath == null || !relativePath.Split('/').Any(PairInventory.Reserved);
    }

    internal async Task WatchRemoteAsync(DriveClient client, string root, CancellationToken cancel, Action<bool>? connectionChanged = null, ILogger? logger = null)
    {
        logger = (logger ?? Log.Logger).ForContext("Endpoint", "GET /api/v4/file/events");
        bool? connected = null;
        try
        {
            await client.WatchAsync(root, (message, data) =>
            {
                bool wasDisconnected = connected == false;
                bool firstConnection = connected == null;
                bool online = message is "subscribed" or "resumed" or "connected" or "event";
                if (!online && message != "disconnected")
                {
                    return;
                }

                SetRemoteAvailable(online);
                if (connected != online)
                {
                    connected = online;
                    connectionChanged?.Invoke(online);
                    if (online)
                    {
                        logger.Write(wasDisconnected ? LogEventLevel.Information : LogEventLevel.Debug,
                            "Remote event push connected ({SubscriptionEvent})", message);
                    }
                }

                if (message == "resumed" || online && (wasDisconnected || firstConnection))
                {
                    RequestRemoteCatchUp();
                }
                else if (message == "event")
                {
                    SignalRemoteChanges(data);
                }
            }, cancel, logger);
        }
        catch (Exception) when (cancel.IsCancellationRequested)
        {
            // The session owns this subscription; shutdown is not a sync failure.
        }
        catch (Exception ex)
        {
            SetRemoteAvailable(false);
            connectionChanged?.Invoke(false);
            logger.ForContext("WillRetry", false).WriteFailure(LogEventLevel.Warning, "Watch remote folder pair", ex);
        }
    }

    internal void SignalRemoteChanges(string data)
    {
        var paths = new List<string?>();
        try
        {
            using (var json = JsonDocument.Parse(data))
            {
                if (json.RootElement.ValueKind != JsonValueKind.Array || json.RootElement.GetArrayLength() > MaxPaths)
                {
                    Signal();
                    return;
                }

                foreach (var item in json.RootElement.EnumerateArray())
                {
                    string? type = Field(item, "type");
                    if (type is not ("create" or "modify" or "delete" or "rename"))
                    {
                        Signal();
                        return;
                    }

                    paths.Add(RelativePath(Field(item, "from")));
                    if (type == "rename")
                    {
                        paths.Add(RelativePath(Field(item, "to")));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            Signal();
            return;
        }

        SignalPaths(paths);

        static string? Field(JsonElement item, string name)
        {
            return item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var field) &&
                field.ValueKind == JsonValueKind.String ? field.GetString() : null;
        }

        static string? RelativePath(string? path)
        {
            return path?.StartsWith('/') == true ? path[1..] : path;
        }
    }
}
