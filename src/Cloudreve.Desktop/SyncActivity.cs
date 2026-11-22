using Serilog;
using Serilog.Events;

namespace Cloudreve.Desktop;

internal sealed record ActivityEntry(DateTime Time, string Operation, string Key, string Status);

internal sealed record ActivityTransfer(long Id, string Phase, long Completed, long Total, bool Paused, long StartOffset)
{
    private readonly long started = System.Diagnostics.Stopwatch.GetTimestamp();
    internal double BytesPerSecond
    {
        get { return (Completed - StartOffset) / Math.Max(0.001, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds); }
    }
}

internal sealed record ActiveFile(ActivityEntry Entry, ActivityTransfer? Transfer);
internal sealed record WaitingFile(string Key, SyncAction Action);
internal enum ScanPhase
{
    Idle,
    Local,
    Remote,
    Failed
}

internal readonly record struct ScanStatus(ScanPhase Phase, int ItemCount = 0, bool IsScoped = false);
internal sealed record ActivitySnapshot(long Revision, ActiveFile[] Active, ActivityEntry[] Recent, WaitingFile[] Waiting, int WaitingCount, ScanStatus Scan);

internal sealed class SyncActivity
{
    internal const int Capacity = 100;
    private sealed class SharedState
    {
        internal readonly HashSet<SyncActivity> Active = [];
        internal readonly Queue<ActivityEntry> Recent = new();
        internal IReadOnlyList<QueuedFile> Pending = [];
        internal IReadOnlyDictionary<string, QueuedFile> PendingByKey = new Dictionary<string, QueuedFile>();
        internal readonly HashSet<string> StartedKeys = new(StringComparer.Ordinal);
        internal int FirstWaiting;
        internal ScanStatus Scan;
        internal TrayState PendingTrayState;
        internal bool StartedSyncWork;
        internal string? HistoryPath;
        internal Task? HistoryWriter;
        internal TaskCompletionSource? FlushRequested;
        internal bool HistoryDirty;
        internal long Revision;
    }

    private readonly SharedState state;
    private ActivityEntry? current;
    private ActivityTransfer? transfer;
    private TaskCompletionSource? resume;
    internal SyncActivity() : this(new SharedState())
    {
    }

    internal SyncActivity(string directory) : this()
    {
        state.HistoryPath = Path.Combine(directory, "recent.json");
        var recent = TransferQueue.ReadCache(state.HistoryPath, DesktopJson.Default.ActivityEntryArray) ?? [];
        foreach (var entry in recent.TakeLast(Capacity))
        {
            if (entry != null && TransferQueue.IsSafeKey(entry.Key))
            {
                state.Recent.Enqueue(entry);
            }
        }

        SetPending(TransferQueue.Open(directory));
    }

    internal void SetPending(IReadOnlyList<QueuedFile> entries)
    {
        SetPending(entries, entries.ToDictionary(entry => entry.File.Key));
    }

    internal void SetPending(TransferQueue queue)
    {
        SetPending(queue.Entries, queue.ByKey);
    }

    private void SetPending(IReadOnlyList<QueuedFile> entries, IReadOnlyDictionary<string, QueuedFile> byKey)
    {
        lock (state)
        {
            if (ReferenceEquals(state.Pending, entries) && state.StartedKeys.Count == 0)
            {
                return;
            }
            // The queue publishes new arrays and indexes together. Workers only mark started keys.
            state.Pending = entries;
            state.PendingByKey = byKey;
            state.StartedKeys.Clear();
            state.FirstWaiting = 0;
            state.Revision++;
        }
    }

    private SyncActivity(SharedState state)
    {
        this.state = state;
    }

    internal SyncActivity CreateWorker()
    {
        return new SyncActivity(state);
    }

    // The tray samples all drives on the UI thread. Retain brief work until it has been sampled.
    internal TrayState ReadTrayState(bool runningPass, bool disconnected)
    {
        lock (state)
        {
            TrayState pending = state.PendingTrayState;
            state.PendingTrayState = TrayState.Idle;
            if (!runningPass && disconnected)
            {
                return TrayState.Disconnected;
            }

            if (pending == TrayState.Syncing || state.Active.Count > 0 || (runningPass && state.StartedSyncWork))
            {
                return TrayState.Syncing;
            }

            if (pending == TrayState.Scanning || (runningPass && state.Scan.Phase is ScanPhase.Local or ScanPhase.Remote))
            {
                return TrayState.Scanning;
            }

            return disconnected ? TrayState.Disconnected : TrayState.Idle;
        }
    }

    internal void SetScan(ScanPhase phase, int itemCount = 0, bool isScoped = false)
    {
        var scan = new ScanStatus(phase, itemCount, isScoped);
        lock (state)
        {
            if (state.Scan != scan)
            {
                if (phase is ScanPhase.Local or ScanPhase.Remote && state.Scan.Phase != phase)
                {
                    state.StartedSyncWork = false;
                    if (state.PendingTrayState == TrayState.Idle)
                    {
                        state.PendingTrayState = TrayState.Scanning;
                    }
                }
                state.Scan = scan;
                state.Revision++;
            }
        }
    }

    internal void MarkSyncWork()
    {
        lock (state)
        {
            state.StartedSyncWork = true;
            state.PendingTrayState = TrayState.Syncing;
        }
    }

    internal void Start(string operation, string key)
    {
        lock (state)
        {
            current = new ActivityEntry(DateTime.Now, operation, key, UiText.Get("处理中"));
            if (state.PendingByKey.ContainsKey(key) && state.StartedKeys.Add(key))
            {
                while (state.FirstWaiting < state.Pending.Count && state.StartedKeys.Contains(state.Pending[state.FirstWaiting].File.Key))
                {
                    state.FirstWaiting++;
                }
            }

            state.Active.Add(this);
            MarkSyncWork();
            state.Revision++;
        }
    }

    internal void Finish(string status)
    {
        lock (state)
        {
            if (current == null)
            {
                return;
            }

            if (state.Recent.Count == Capacity)
            {
                state.Recent.Dequeue();
            }

            state.Recent.Enqueue(current with { Time = DateTime.Now, Status = status });
            if (state.HistoryPath != null)
            {
                state.HistoryDirty = true;
                if (state.HistoryWriter == null)
                {
                    state.FlushRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    state.HistoryWriter = Task.Run(WriteHistoryAsync);
                }
            }

            EndTransfer();
            current = null;
            state.Active.Remove(this);
            state.Revision++;
        }
    }

    internal Task FlushHistoryAsync()
    {
        lock (state)
        {
            state.FlushRequested?.TrySetResult();
            return state.HistoryWriter ?? Task.CompletedTask;
        }
    }

    private async Task WriteHistoryAsync()
    {
        Task flush;
        lock (state)
        {
            flush = state.FlushRequested!.Task;
        }
        while (true)
        {
            await Task.WhenAny(flush, Task.Delay(100)).ConfigureAwait(false);
            ActivityEntry[] snapshot;
            lock (state)
            {
                snapshot = state.Recent.ToArray();
                state.HistoryDirty = false;
            }
            try
            {
                FolderPairStore.WriteJson(state.HistoryPath!, snapshot, DesktopJson.Default.ActivityEntryArray);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Logger.WriteFailure(LogEventLevel.Warning, "Save recent activity", ex);
            }
            lock (state)
            {
                if (!state.HistoryDirty)
                {
                    state.HistoryWriter = null;
                    state.FlushRequested = null;
                    return;
                }
            }
        }
    }

    internal void BeginTransfer(string phase, long total, long offset = 0)
    {
        lock (state)
        {
            EndTransfer();
            transfer = new ActivityTransfer(++state.Revision, phase, offset, total, false, offset);
        }
    }

    internal void EndTransfer()
    {
        lock (state)
        {
            if (transfer != null)
            {
                transfer = null;
                resume?.TrySetResult();
                resume = null;
                state.Revision++;
            }
        }
    }

    internal void SetPaused(long id, bool paused)
    {
        lock (state)
        {
            var worker = state.Active.FirstOrDefault(item => item.transfer?.Id == id);
            if (worker?.transfer == null || worker.transfer.Paused == paused)
            {
                return;
            }

            if (paused)
            {
                worker.resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            else
            {
                worker.resume?.TrySetResult();
                worker.resume = null;
            }

            worker.transfer = worker.transfer with { Paused = paused };
            state.Revision++;
        }
    }

    internal Task WaitForResumeAsync(CancellationToken cancel)
    {
        lock (state)
        {
            cancel.ThrowIfCancellationRequested();
            return resume?.Task.WaitAsync(cancel) ?? Task.CompletedTask;
        }
    }

    internal void ReportBytes(long position)
    {
        lock (state)
        {
            if (transfer != null && position > transfer.Completed)
            {
                transfer = transfer with { Completed = Math.Min(position, transfer.Total) };
                state.Revision++;
            }
        }
    }

    internal ActivitySnapshot? ChangesSince(long knownRevision)
    {
        lock (state)
        {
            if (knownRevision == state.Revision)
            {
                return null;
            }

            var active = state.Active.Select(worker => new ActiveFile(worker.current!, worker.transfer)).ToArray();
            int waitingCount = state.Pending.Count - state.StartedKeys.Count;
            var waiting = new List<WaitingFile>(Math.Min(waitingCount, Capacity));
            for (int i = state.FirstWaiting; i < state.Pending.Count && waiting.Count < Capacity; i++)
            {
                var file = state.Pending[i].File;
                if (!state.StartedKeys.Contains(file.Key))
                {
                    waiting.Add(new WaitingFile(file.Key, file.Action));
                }
            }
            return new ActivitySnapshot(state.Revision, active, state.Recent.Reverse().ToArray(), waiting.ToArray(), waitingCount, state.Scan);
        }
    }
}
