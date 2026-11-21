using System.Threading.Channels;
using Serilog;
using Serilog.Events;

namespace Cloudreve.Desktop;

internal sealed class PairWake
{
    // Changes are hints, not a journal: one pending rescan covers any burst of events.
    private readonly Channel<bool> signals = Channel.CreateBounded<bool>(1);

    internal void Signal()
    {
        signals.Writer.TryWrite(true);
    }

    internal async Task WaitAsync(TimeSpan interval, CancellationToken cancel)
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
                return;
            }
        }

        await Task.Delay(TimeSpan.FromMilliseconds(200), cancel);
        signals.Reader.TryRead(out _);
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
            watcher.Error += (_, _) => { Signal(); };
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
        {
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

    internal async Task WatchRemoteAsync(DriveClient client, string root, CancellationToken cancel)
    {
        try
        {
            await client.WatchAsync(root, message =>
            {
                if (message is "event" or "resumed")
                {
                    Signal();
                }
            }, cancel);
        }
        catch (Exception) when (cancel.IsCancellationRequested)
        {
            // The session owns this subscription; shutdown is not a sync failure.
        }
        catch (Exception ex)
        {
            Log.Logger.WriteFailure(LogEventLevel.Warning, "Watch remote folder pair", ex);
        }
    }
}