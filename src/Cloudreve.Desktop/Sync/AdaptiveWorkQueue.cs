using System.Net;
using System.Runtime.ExceptionServices;

namespace Cloudreve.Desktop;

// Only the coordinator changes the window, merges results or queues discovered children.
internal sealed class AdaptiveWorkQueue
{
    private const int Maximum = 6;
    internal int Limit { get; private set; } = 2;
    private int healthyCompletions;

    internal Task RunAsync<T, TResult>(IEnumerable<T> items, Func<T, CancellationToken, Task<TResult>> process,
        Action<TResult> accept, CancellationToken cancel)
    {
        return TraverseAsync(items, process, (result, _) => accept(result), cancel);
    }

    internal async Task TraverseAsync<T, TResult>(IEnumerable<T> items, Func<T, CancellationToken, Task<TResult>> process,
        Action<TResult, Action<T>> accept, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        var pending = new Queue<T>(items);
        Action<T> enqueue = pending.Enqueue;
        var running = new List<Task<TResult>>(Maximum);
        ExceptionDispatchInfo? failure = null;
        bool backedOff = false;
        using (var batch = CancellationTokenSource.CreateLinkedTokenSource(cancel))
        {
            while (pending.Count > 0 || running.Count > 0)
            {
                while (failure == null && !cancel.IsCancellationRequested && running.Count < Limit && pending.TryDequeue(out var item))
                {
                    running.Add(StartAsync(item));
                }

                if (running.Count == 0)
                {
                    break;
                }

                var completed = await Task.WhenAny(running);
                running.Remove(completed);
                try
                {
                    accept(await completed, enqueue);
                    bool hasBacklog = pending.Count > Limit - running.Count;
                    if (failure == null && hasBacklog && Limit < Maximum && ++healthyCompletions >= Limit)
                    {
                        Limit++;
                        healthyCompletions = 0;
                    }
                }
                catch (Exception ex)
                {
                    if (!backedOff && (ex is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable } ||
                                       ex is TimeoutException || ex is OperationCanceledException && !batch.IsCancellationRequested))
                    {
                        Limit = Math.Max(1, Limit / 2);
                        backedOff = true;
                    }

                    healthyCompletions = 0;
                    failure ??= ExceptionDispatchInfo.Capture(ex);
                    await batch.CancelAsync();
                }
            }

            async Task<TResult> StartAsync(T item)
            {
                return await process(item, batch.Token);
            }
        }

        failure?.Throw();
        cancel.ThrowIfCancellationRequested();
    }
}
