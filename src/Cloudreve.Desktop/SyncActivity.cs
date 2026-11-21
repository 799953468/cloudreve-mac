namespace Cloudreve.Desktop;

internal sealed record ActivityEntry(DateTime Time, string Operation, string Key, string Status);

internal sealed record ActivityTransfer(long Id, string Phase, long Completed, long Total, bool Paused);

internal sealed record ActivitySnapshot(long Revision, ActivityEntry? Current, ActivityEntry[] Recent, ActivityTransfer? Transfer);

internal sealed class SyncActivity
{
    internal const int Capacity = 100;
    private readonly object gate = new();
    private readonly Queue<ActivityEntry> recent = new();
    private ActivityEntry? current;
    private ActivityTransfer? transfer;
    private TaskCompletionSource? resume;
    private long revision;

    internal void Start(string operation, string key)
    {
        lock (gate)
        {
            current = new ActivityEntry(DateTime.Now, operation, key, UiText.Get("处理中"));
            revision++;
        }
    }

    internal void Finish(string status)
    {
        lock (gate)
        {
            if (current == null)
            {
                return;
            }

            if (recent.Count == Capacity)
            {
                recent.Dequeue();
            }

            recent.Enqueue(current with { Time = DateTime.Now, Status = status });
            EndTransfer();
            current = null;
            revision++;
        }
    }

    internal void BeginTransfer(string phase, long total)
    {
        lock (gate)
        {
            EndTransfer();
            transfer = new ActivityTransfer(++revision, phase, 0, total, false);
        }
    }

    internal void EndTransfer()
    {
        lock (gate)
        {
            if (transfer != null)
            {
                transfer = null;
                resume?.TrySetResult();
                resume = null;
                revision++;
            }
        }
    }

    internal void SetPaused(long id, bool paused)
    {
        lock (gate)
        {
            if (transfer == null || transfer.Id != id || transfer.Paused == paused)
            {
                return;
            }

            if (paused)
            {
                resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            else
            {
                resume?.TrySetResult();
                resume = null;
            }

            transfer = transfer with { Paused = paused };
            revision++;
        }
    }

    internal Task WaitForResumeAsync(CancellationToken cancel)
    {
        lock (gate)
        {
            cancel.ThrowIfCancellationRequested();
            return resume?.Task.WaitAsync(cancel) ?? Task.CompletedTask;
        }
    }

    internal void ReportBytes(long position)
    {
        lock (gate)
        {
            if (transfer != null && position > transfer.Completed)
            {
                transfer = transfer with { Completed = Math.Min(position, transfer.Total) };
                revision++;
            }
        }
    }

    internal ActivitySnapshot? ChangesSince(long knownRevision)
    {
        lock (gate)
        {
            if (knownRevision == revision)
            {
                return null;
            }

            return new ActivitySnapshot(revision, current, recent.Reverse().ToArray(), transfer);
        }
    }
}