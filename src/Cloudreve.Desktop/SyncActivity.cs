namespace Cloudreve.Desktop;

internal sealed record ActivityEntry(DateTime Time, string Operation, string Key, string Status);

internal sealed record ActivityTransfer(long Id, string Phase, long Completed, long Total, bool Paused)
{
    private readonly long started = System.Diagnostics.Stopwatch.GetTimestamp();
    internal double BytesPerSecond
    {
        get { return Completed / Math.Max(0.001, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds); }
    }
}

internal sealed record ActiveFile(ActivityEntry Entry, ActivityTransfer? Transfer);
internal sealed record ActivitySnapshot(long Revision, ActiveFile[] Active, ActivityEntry[] Recent);

internal sealed class SyncActivity
{
    internal const int Capacity = 100;
    private sealed class SharedState
    {
        internal readonly HashSet<SyncActivity> Active = [];
        internal readonly Queue<ActivityEntry> Recent = new();
        internal long Revision;
    }

    private readonly SharedState state;
    private ActivityEntry? current;
    private ActivityTransfer? transfer;
    private TaskCompletionSource? resume;
    internal SyncActivity() : this(new SharedState())
    {
    }

    private SyncActivity(SharedState state)
    {
        this.state = state;
    }

    internal SyncActivity CreateWorker()
    {
        return new SyncActivity(state);
    }

    internal void Start(string operation, string key)
    {
        lock (state)
        {
            current = new ActivityEntry(DateTime.Now, operation, key, UiText.Get("处理中"));
            state.Active.Add(this);
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
            EndTransfer();
            current = null;
            state.Active.Remove(this);
            state.Revision++;
        }
    }

    internal void BeginTransfer(string phase, long total)
    {
        lock (state)
        {
            EndTransfer();
            transfer = new ActivityTransfer(++state.Revision, phase, 0, total, false);
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
            return new ActivitySnapshot(state.Revision, active, state.Recent.Reverse().ToArray());
        }
    }
}
