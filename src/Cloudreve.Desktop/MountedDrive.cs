using Serilog;
using Serilog.Events;

namespace Cloudreve.Desktop;

// A mount lives for the session, independently of all three desktop windows.
internal sealed class MountedDrive(DriveProfiles profiles, SessionFileStore store, HttpClient api, HttpClient storage)
{
    private CancellationTokenSource? lifetime;
    private FileSystemWatcher? watcher;
    private Task workers = Task.CompletedTask;
    private PairWake? wake;
    private readonly object bindingGate = new();
    private object? binding;
    private HashSet<string> conflicts = [];
    private bool runningPass;
    private bool remoteUnavailable;

    internal SessionFileStore Store { get; } = store;
    internal DriveSession Account { get; private set; } = null!;
    internal FolderPairStore PairStore { get; private set; } = null!;
    internal FolderPair? Pair { get; private set; }
    internal SyncActivity Activity { get; private set; } = new();
    internal (long Used, long Total)? Capacity { get; private set; }
    internal bool PushLost { get; private set; }
    internal bool Expired { get; private set; }
    internal event Action<string>? Notice;

    internal TrayState ReadTrayState()
    {
        return Activity.ReadTrayState(runningPass, Pair == null || Expired || remoteUnavailable);
    }

    internal void Bind(DriveSession account)
    {
        var current = new object();
        binding = current;
        Account = account;
        Expired = account.RefreshExpires <= DateTimeOffset.UtcNow;
        PushLost = false;
        remoteUnavailable = false;
        PairStore = new FolderPairStore(Path.Combine(Store.DirectoryPath, "folder-pairs"), account);
        Pair = PairStore.Load();
        if (Pair != null)
        {
            Activity = new SyncActivity(PairStore.TransferDirectory(Pair));
            profiles.Reserve(Store, Pair.Local);
        }
        var client = new DriveClient(api, storage, account, rotated =>
        {
            lock (bindingGate)
            {
                if (binding == current)
                {
                    Store.Save(rotated);
                    Account = rotated;
                }
            }
        });
        lifetime = new CancellationTokenSource();
        var capacity = ReadCapacityAsync(client, lifetime.Token);
        workers = capacity;
        if (Pair != null)
        {
            wake = new PairWake();
            var engine = new FolderPairSync(PairStore, Pair, client, Activity, wake.BeginFullRemoteInventory);
            watcher = wake.WatchLocal(Pair.Local);
            ILogger logger = Log.Logger.ForContext("PairId", Pair.Marker);
            workers = Task.WhenAll(capacity, ScanAsync(engine, wake, logger, lifetime.Token),
                wake.WatchRemoteAsync(client, Pair.Remote, lifetime.Token, connected => { PushLost = !connected; }, logger));
        }
    }

    private async Task ReadCapacityAsync(DriveClient drive, CancellationToken cancel)
    {
        try
        {
            Capacity = await drive.CapacityAsync(cancel);
        }
        catch (Exception ex) when (!cancel.IsCancellationRequested)
        {
            CheckExpiry(ex);
            Log.Logger.WriteFailure(LogEventLevel.Debug, "Read drive capacity", ex);
        }
        catch (Exception) when (cancel.IsCancellationRequested)
        {
        }
    }

    private void CheckExpiry(Exception ex)
    {
        if (!Expired && (Account.RefreshExpires <= DateTimeOffset.UtcNow || ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized }))
        {
            Expired = true;
            if (Program.Preferences.Current.CredentialAlerts)
            {
                Notice?.Invoke(DesktopText.Get("settings.driveStatus.credentialExpired"));
            }
        }
    }

    private async Task ScanAsync(FolderPairSync engine, PairWake signal, ILogger logger, CancellationToken cancel)
    {
        PairScanScope? scope = null;
        string? fullReason = "Startup";
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                bool retry = false;
                var attempt = new PairScanAttempt { FullReason = scope == null ? fullReason : null };
                PairScanScope? retryScope = null;
                try
                {
                    runningPass = true;
                    var result = await Task.Run(() => engine.RunAsync(cancel, attempt, scope), cancel);
                    remoteUnavailable = false;
                    // The pass finished everything else, so even after a full scan only the deferred paths need another look.
                    retry = result.DeferredPaths is { Count: > 0 };
                    retryScope = retry ? new PairScanScope(result.DeferredPaths!) : null;
                    logger.ForContext("ScanScope", attempt.Scope == null ? "Full" : "Paths").ForContext("FullScanReason", attempt.Scope == null ? attempt.FullReason : null).Debug("Folder pair scan finished in {ElapsedMs} ms: {Uploaded} uploaded, {Downloaded} downloaded, {Removed} removed, {Conflicts} conflicts, {DeferredDirectories} directories deferred, {DeferredFiles} files deferred",
                        attempt.ElapsedMs, result.Uploaded, result.Downloaded, result.Removed, result.Conflicts.Count, result.DeferredDirectories, result.DeferredFiles);
                    if (Program.Preferences.Current.ConflictAlerts && result.Conflicts.Any(key => !conflicts.Contains(key)))
                    {
                        Notice?.Invoke(UiText.Get("同步冲突"));
                    }

                    conflicts.RemoveWhere(key => attempt.Scope == null || attempt.Scope.Covers(key));
                    conflicts.UnionWith(result.Conflicts);
                }
                catch (Exception ex) when (!cancel.IsCancellationRequested)
                {
                    retry = true;
                    retryScope = attempt.Scope;
                    if (ex is HttpRequestException request)
                    {
                        remoteUnavailable = request.StatusCode == null || (int)request.StatusCode >= 500;
                    }
                    else if (ex is OperationCanceledException)
                    {
                        remoteUnavailable = true;
                    }
                    CheckExpiry(ex);
                    logger.ForContext("Stage", attempt.Stage).ForContext("ElapsedMs", attempt.ElapsedMs)
                        .ForContext("ScanScope", attempt.Scope == null ? "Full" : "Paths").ForContext("FullScanReason", attempt.Scope == null ? attempt.FullReason : null)
                        .WriteFailure(LogEventLevel.Warning, "Synchronize folder pair", ex);
                }
                finally
                {
                    runningPass = false;
                }

                scope = await signal.WaitForNextScanAsync(retry, cancel, retryScope);
                fullReason = signal.LastFullReason;
            }
        }
        catch (Exception) when (cancel.IsCancellationRequested)
        {
        }
    }

    internal void SaveRules(string text)
    {
        if (Pair != null)
        {
            PairStore.SaveRules(Pair, text);
            wake?.Signal("IgnoreRulesChanged");
        }
    }

    internal async Task StopAsync()
    {
        watcher?.Dispose();
        watcher = null;
        lifetime?.Cancel();
        await workers;
        await Activity.FlushHistoryAsync();
        lock (bindingGate)
        {
            binding = null;
        }

        lifetime?.Dispose();
        lifetime = null;
        profiles.Release(Store);
    }
}
