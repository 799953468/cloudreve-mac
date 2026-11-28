using Serilog;
using Serilog.Events;

namespace Cloudreve.Desktop;

// Decides what each pass of one pair covers and runs passes one at a time.
//
// Watchers and push events feed PairWake with changed paths. When PairWake asks for the whole pair because the app just
// started, push reconnected or the local watcher lost events, the coordinator first tries a catch-up: a complete local
// walk compared with the baseline, plus a cloud search since the change cursor. Only when that cannot be trusted does it
// fall back to listing every cloud folder. A scheduled full scan still verifies the pair now and then, because the
// cloud search cannot see permanent deletions, emptied trash or items moved out of the pair.
internal sealed class PairSyncCoordinator(FolderPairSync engine, PairWake wake, DriveClient drive, ILogger logger,
    TimeSpan? firstVerificationDelay = null, TimeSpan? verificationInterval = null, TimeSpan? verificationCheck = null)
{
    internal static readonly TimeSpan DefaultFirstVerificationDelay = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan DefaultVerificationInterval = TimeSpan.FromHours(12);
    internal const string VerificationReason = "ScheduledVerification";
    private const string RetryReason = "RetryAfterFailedFullScan";

    private readonly TimeSpan firstDelay = firstVerificationDelay ?? DefaultFirstVerificationDelay;
    private readonly TimeSpan interval = verificationInterval ?? DefaultVerificationInterval;
    private readonly SemaphoreSlim gate = new(1, 1);
    private long nextVerificationTicks = DateTimeOffset.MaxValue.UtcTicks;

    internal bool Running { get; private set; }

    // Called on the caller's context after each pass, before the next one can start.
    internal Func<PairSyncResult, PairScanAttempt, Task>? Completed { get; set; }
    internal Action<Exception, PairScanAttempt>? Failed { get; set; }

    internal DateTimeOffset NextVerification
    {
        get { return new DateTimeOffset(Interlocked.Read(ref nextVerificationTicks), TimeSpan.Zero); }
        private set { Interlocked.Exchange(ref nextVerificationTicks, value.UtcTicks); }
    }

    // Passes and conflict resolutions both take the pair lock, which does not wait; they take turns here instead.
    internal async Task<T> ExclusiveAsync<T>(Func<Task<T>> work, CancellationToken cancel)
    {
        await gate.WaitAsync(cancel);
        try
        {
            return await work();
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task RunAsync(CancellationToken cancel)
    {
        var timer = SignalVerificationWhenDueAsync(verificationCheck ?? TimeSpan.FromSeconds(30), cancel);
        try
        {
            var request = new ScanRequest(null, "Startup", []);
            string? failedCatchUp = null;
            while (!cancel.IsCancellationRequested)
            {
                var attempt = new PairScanAttempt();
                bool retry = false;
                PairScanScope? retryScope = null;
                try
                {
                    var result = await ExclusiveAsync(async () =>
                    {
                        Running = true;
                        try
                        {
                            // Scans enumerate synchronously; keep them off the caller's (UI) thread.
                            return await Task.Run(() => RunPassAsync(request, attempt, cancel), cancel);
                        }
                        finally
                        {
                            Running = false;
                        }
                    }, cancel);
                    failedCatchUp = null;
                    // The pass finished everything else, so even after a full scan only the deferred paths need another look.
                    retry = result.DeferredPaths is { Count: > 0 };
                    retryScope = retry ? new PairScanScope(result.DeferredPaths!) : null;
                    if (Completed != null)
                    {
                        await Completed(result, attempt);
                    }
                }
                catch (Exception ex) when (!cancel.IsCancellationRequested)
                {
                    retry = true;
                    // A failed catch-up is planned again rather than replaced by a path scan that lacks its confirmations.
                    failedCatchUp = attempt.CatchUpReason;
                    retryScope = failedCatchUp == null ? attempt.Scope : null;
                    Failed?.Invoke(ex, attempt);
                }

                var scope = await wake.WaitForNextScanAsync(retry, cancel, retryScope);
                string? reason = wake.LastFullReason;
                if (reason == RetryReason && failedCatchUp != null)
                {
                    reason = failedCatchUp;
                }

                request = new ScanRequest(scope, reason, wake.LastPaths);
            }
        }
        catch (Exception) when (cancel.IsCancellationRequested)
        {
        }

        try
        {
            await timer;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task<PairSyncResult> RunPassAsync(ScanRequest request, PairScanAttempt attempt, CancellationToken cancel)
    {
        attempt.ServerTime = await ReadServerTimeAsync(cancel);
        var scope = request.Scope;
        string? reason = request.FullReason;
        bool cloud = reason is "Startup" or "RemoteCatchUp";
        bool local = reason is "Startup" or "LocalWatcherOverflow" or "LocalWatcherError";
        if (reason != null && (cloud || local) && attempt.ServerTime is { } now)
        {
            var plan = await engine.PlanCatchUpAsync(local, cloud, request.Paths, now, cancel);
            if (plan.Scope != null)
            {
                attempt.CatchUpReason = reason;
                attempt.CoversCloudChanges = cloud;
                logger.ForContext("CatchUpReason", reason).Information(
                    "Catching up without a full scan: {LocalChanges} local changes, {CloudChanges} cloud changes, {CloudDeletions} cloud deletions; checking {Paths} paths",
                    plan.LocalChanges, plan.CloudChanges, plan.CloudDeletions, plan.Scope.Targets.Count());
                ScheduleAfterCatchUp(now);
                scope = plan.Scope;
                reason = null;
            }
            else
            {
                reason += ":" + plan.FullReason;
            }
        }

        attempt.FullReason = scope == null ? reason : null;
        var result = await engine.RunAsync(cancel, attempt, scope);
        if (attempt.Scope == null)
        {
            NextVerification = DateTimeOffset.UtcNow + interval;
        }

        return result;
    }

    private async Task<DateTimeOffset?> ReadServerTimeAsync(CancellationToken cancel)
    {
        try
        {
            return await drive.ServerTimeAsync(cancel);
        }
        catch (Exception ex) when (!cancel.IsCancellationRequested && ex is HttpRequestException or InvalidDataException or InvalidOperationException)
        {
            // Without the server clock this pass can neither catch up nor advance the cursor; it still syncs normally.
            logger.WriteFailure(LogEventLevel.Debug, "Read server time", ex);
            return null;
        }
    }

    // The first catch-up of a session schedules the next full verification from the last one, but never sooner than firstDelay.
    private void ScheduleAfterCatchUp(DateTimeOffset serverNow)
    {
        if (NextVerification != DateTimeOffset.MaxValue)
        {
            return;
        }

        var lastFull = engine.ReadLastFullScan();
        var wait = lastFull is { } last ? interval - (serverNow - last) : TimeSpan.Zero;
        NextVerification = DateTimeOffset.UtcNow + (wait > firstDelay ? wait : firstDelay);
    }

    private async Task SignalVerificationWhenDueAsync(TimeSpan check, CancellationToken cancel)
    {
        while (true)
        {
            await Task.Delay(check, cancel);
            if (DateTimeOffset.UtcNow >= NextVerification)
            {
                NextVerification = DateTimeOffset.MaxValue;
                wake.Signal(VerificationReason);
            }
        }
    }

    private sealed record ScanRequest(PairScanScope? Scope, string? FullReason, IReadOnlyCollection<string> Paths);
}
