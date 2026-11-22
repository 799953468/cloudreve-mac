using System.Threading.Channels;
using Serilog;
using Serilog.Events;

namespace Cloudreve.Desktop;

internal sealed class PairWake(TimeSpan pollInterval)
{
    // Changes are hints, not a journal: one pending rescan covers any burst of events.
    private readonly Channel<bool> signals = Channel.CreateBounded<bool>(1);
    private volatile bool localAvailable;
    private volatile bool remoteAvailable;

    internal PairWake() : this(TimeSpan.FromSeconds(30))
    {
    }

    internal void Signal()
    {
        signals.Writer.TryWrite(true);
    }

    internal void SetRemoteAvailable(bool available)
    {
        remoteAvailable = available;
    }

    internal async Task WaitForNextScanAsync(bool retry, CancellationToken cancel)
    {
        if (retry)
        {
            // Notifications during a failed pass must not bypass the retry delay.
            await Task.Delay(pollInterval, cancel);
            signals.Reader.TryRead(out _);
            return;
        }

        while (true)
        {
            // Recheck both watchers on short ticks without walking either tree.
            if (await WaitAsync(pollInterval, cancel) || !localAvailable || !remoteAvailable)
            {
                return;
            }
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
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
            };
            watcher.Changed += OnLocalChange;
            watcher.Created += OnLocalChange;
            watcher.Deleted += OnLocalChange;
            watcher.Renamed += (_, change) =>
            {
                if (IsRelevant(change.OldName) || IsRelevant(change.Name))
                {
                    Signal();
                }
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
        if (IsRelevant(change.Name))
        {
            Signal();
        }
    }

    internal static bool IsRelevant(string? relativePath)
    {
        return relativePath == null || !relativePath.Split(Path.DirectorySeparatorChar).Any(PairInventory.Reserved);
    }

    internal async Task WatchRemoteAsync(DriveClient client, string root, CancellationToken cancel, Action<bool>? connectionChanged = null, ILogger? logger = null)
    {
        logger = (logger ?? Log.Logger).ForContext("Endpoint", "GET /api/v4/file/events");
        bool? connected = null;
        try
        {
            await client.WatchAsync(root, message =>
            {
                bool wasDisconnected = connected == false;
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

                if (message is "event" or "resumed" || online && wasDisconnected)
                {
                    Signal();
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
}
