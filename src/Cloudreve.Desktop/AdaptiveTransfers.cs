using System.Net;
using System.Runtime.ExceptionServices;

namespace Cloudreve.Desktop;

// One scheduler per mount. Only its coordinator adjusts the window or applies checkpoints.
internal sealed class AdaptiveTransfers
{
    private const int Maximum = 6;
    internal int Limit { get; private set; } = 2;
    private int healthyCompletions;

    internal async Task RunAsync<T, TResult>(IReadOnlyList<T> items, Func<T, CancellationToken, Task<TResult>> transfer,
        Action<TResult> checkpoint, CancellationToken cancel)
    {
        var running = new List<Task<TResult>>(Maximum);
        int next = 0;
        ExceptionDispatchInfo? failure = null;
        bool backedOff = false;
        using (var batch = CancellationTokenSource.CreateLinkedTokenSource(cancel))
        {
            while (next < items.Count || running.Count > 0)
            {
                while (failure == null && !cancel.IsCancellationRequested && next < items.Count && running.Count < Limit)
                {
                    running.Add(StartAsync(items[next++]));
                }

                if (running.Count == 0)
                {
                    break;
                }

                var completed = await Task.WhenAny(running);
                running.Remove(completed);
                try
                {
                    checkpoint(await completed);
                    if (failure == null && next < items.Count && Limit < Maximum && ++healthyCompletions >= Limit)
                    {
                        Limit++;
                        healthyCompletions = 0;
                    }
                }
                catch (Exception ex)
                {
                    if (!backedOff && (ex is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable } ||
                                       ex is OperationCanceledException && !batch.IsCancellationRequested))
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
                return await transfer(item, batch.Token);
            }
        }

        failure?.Throw();
        cancel.ThrowIfCancellationRequested();
    }
}
