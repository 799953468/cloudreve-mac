using Serilog;
using Serilog.Events;

namespace Cloudreve.Desktop;

internal enum ConflictReason
{
    ContentsDiffer,
    DeletedLocally,
    DeletedInCloud,
    NeedsManualReview
}

// One unresolved path from the latest scans. Choice is null when the app cannot offer an automatic resolution,
// for example a file and a folder sharing one name, or a name this system cannot store.
internal sealed record PairConflict(string Key, FileConflict? Choice)
{
    internal ConflictReason Reason
    {
        get
        {
            return Choice switch
            {
                null => ConflictReason.NeedsManualReview,
                { LocalHash: null } => ConflictReason.DeletedLocally,
                { Remote: null } => ConflictReason.DeletedInCloud,
                _ => ConflictReason.ContentsDiffer
            };
        }
    }

    internal bool CanKeepBoth
    {
        get { return Reason == ConflictReason.ContentsDiffer; }
    }
}

// A mount lives for the session, independently of all three desktop windows.
internal sealed class MountedDrive(DriveProfiles profiles, SessionFileStore store, HttpClient api, HttpClient storage)
{
    private CancellationTokenSource? lifetime;
    private FileSystemWatcher? watcher;
    private Task workers = Task.CompletedTask;
    private PairWake? wake;
    private readonly object bindingGate = new();
    private object? binding;
    private readonly object conflictGate = new();
    private Dictionary<string, PairConflict> conflicts = new(StringComparer.OrdinalIgnoreCase);
    private PairConflict[] conflictList = [];
    // A pass and a conflict resolution both take the pair lock, which does not wait; take turns here instead.
    private readonly SemaphoreSlim passGate = new(1, 1);
    private FolderPairSync? engine;
    private ILogger? pairLogger;
    // Conflicts the newer-version rule already declined or failed to resolve; a changed conflict is a new record and is tried again.
    private readonly HashSet<PairConflict> autoDeclined = [];
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

    internal IReadOnlyList<PairConflict> Conflicts
    {
        get
        {
            lock (conflictGate)
            {
                return conflictList;
            }
        }
    }

    internal int ConflictRevision { get; private set; }

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
        engine = null;
        pairLogger = null;
        autoDeclined.Clear();
        lock (conflictGate)
        {
            conflicts = new Dictionary<string, PairConflict>(StringComparer.OrdinalIgnoreCase);
            conflictList = [];
            ConflictRevision++;
        }
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
            engine = new FolderPairSync(PairStore, Pair, client, Activity, wake.BeginFullRemoteInventory);
            watcher = wake.WatchLocal(Pair.Local);
            ILogger logger = Log.Logger.ForContext("PairId", Pair.Marker);
            pairLogger = logger;
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
                Notice?.Invoke(UiText.Get("settings.driveStatus.credentialExpired"));
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
                    PairSyncResult result;
                    await passGate.WaitAsync(cancel);
                    try
                    {
                        runningPass = true;
                        result = await Task.Run(() => engine.RunAsync(cancel, attempt, scope), cancel);
                    }
                    finally
                    {
                        passGate.Release();
                    }

                    remoteUnavailable = false;
                    // The pass finished everything else, so even after a full scan only the deferred paths need another look.
                    retry = result.DeferredPaths is { Count: > 0 };
                    retryScope = retry ? new PairScanScope(result.DeferredPaths!) : null;
                    // Passes that changed nothing stay at Debug so periodic scans do not flood the log.
                    var summaryLevel = result.Uploaded + result.Downloaded + result.Removed + result.Conflicts.Count > 0 ? LogEventLevel.Information : LogEventLevel.Debug;
                    logger.ForContext("ScanScope", attempt.Scope == null ? "Full" : "Paths").ForContext("FullScanReason", attempt.Scope == null ? attempt.FullReason : null).Write(summaryLevel, "Folder pair scan finished in {ElapsedMs} ms: {Uploaded} uploaded, {Downloaded} downloaded, {Removed} removed, {Conflicts} conflicts, {DeferredDirectories} directories deferred, {DeferredFiles} files deferred",
                        attempt.ElapsedMs, result.Uploaded, result.Downloaded, result.Removed, result.Conflicts.Count, result.DeferredDirectories, result.DeferredFiles);
                    var added = UpdateConflicts(result, attempt.Scope, logger);
                    if (Program.Preferences.Current.AutoResolveNewer)
                    {
                        await ResolveNewerAsync(engine, logger, cancel);
                    }

                    // Only alert about conflicts that are still waiting for the user.
                    var open = Conflicts.ToHashSet();
                    if (Program.Preferences.Current.ConflictAlerts && added.Any(open.Contains))
                    {
                        Notice?.Invoke(UiText.Get("app.syncConflictNotice"));
                    }
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

    // Replaces the conflicts this pass covered. Returns the ones that are new.
    internal IReadOnlyList<PairConflict> UpdateConflicts(PairSyncResult result, PairScanScope? scope, ILogger logger)
    {
        var choices = new Dictionary<string, FileConflict>(StringComparer.OrdinalIgnoreCase);
        foreach (var choice in result.Choices)
        {
            choices[choice.Key] = choice;
        }

        var added = new List<PairConflict>();
        var cleared = new List<string>();
        lock (conflictGate)
        {
            var next = new Dictionary<string, PairConflict>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, conflict) in conflicts)
            {
                if (scope != null && !scope.Covers(key))
                {
                    next[key] = conflict;
                }
            }

            foreach (string key in result.Conflicts)
            {
                var conflict = new PairConflict(key, choices.GetValueOrDefault(key));
                next[key] = conflict;
                if (!conflicts.ContainsKey(key))
                {
                    added.Add(conflict);
                }
            }

            cleared.AddRange(conflicts.Keys.Where(key => !next.ContainsKey(key)));
            bool changed = added.Count > 0 || cleared.Count > 0 || next.Any(entry => !conflicts.TryGetValue(entry.Key, out var old) || old != entry.Value);
            if (changed)
            {
                conflicts = next;
                conflictList = next.Values.OrderBy(conflict => conflict.Key, StringComparer.OrdinalIgnoreCase).ToArray();
                ConflictRevision++;
            }
        }

        // Relative paths only: the pair root stays out of the log, as everywhere else.
        foreach (var conflict in added)
        {
            logger.ForContext("LocalHash", conflict.Choice?.LocalHash).ForContext("RemoteVersion", conflict.Choice?.Remote?.Version)
                .Warning("Sync conflict at {ConflictKey}: {ConflictReason}; the path is not synced until it is resolved", conflict.Key, conflict.Reason);
        }

        foreach (string key in cleared)
        {
            logger.Information("Sync conflict cleared at {ConflictKey}", key);
        }

        return added;
    }

    // Keeps whichever side of a two-sided content conflict was modified more recently.
    private async Task ResolveNewerAsync(FolderPairSync current, ILogger logger, CancellationToken cancel)
    {
        autoDeclined.IntersectWith(Conflicts);
        foreach (var conflict in Conflicts.Where(item => item.Reason == ConflictReason.ContentsDiffer && !autoDeclined.Contains(item)))
        {
            cancel.ThrowIfCancellationRequested();
            ConflictChoice? choice;
            DateTimeOffset? local, remote;
            try
            {
                (choice, local, remote) = await current.NewerSideAsync(conflict.Choice!, cancel);
            }
            catch (Exception ex) when (!cancel.IsCancellationRequested)
            {
                autoDeclined.Add(conflict);
                logger.ForContext("ConflictKey", conflict.Key).WriteFailure(LogEventLevel.Warning, "Read modification times for sync conflict", ex);
                continue;
            }

            if (choice is not { } newer)
            {
                autoDeclined.Add(conflict);
                logger.Information("Sync conflict at {ConflictKey} left for the user: local modified {LocalModified}, cloud modified {CloudModified}",
                    conflict.Key, local, remote);
                continue;
            }

            logger.Information("Sync conflict at {ConflictKey} keeps the newer {ConflictChoice}: local modified {LocalModified}, cloud modified {CloudModified}",
                conflict.Key, newer, local, remote);
            Activity.Start(UiText.Get(newer == ConflictChoice.KeepLocal ? "activity.upload" : "activity.download"), conflict.Key);
            try
            {
                await ResolveConflictAsync(conflict, newer, cancel);
                Activity.Finish(UiText.Get("activity.done"));
            }
            catch (Exception) when (!cancel.IsCancellationRequested)
            {
                // ResolveConflictAsync logged the failure; leave the conflict for the user instead of retrying every pass.
                autoDeclined.Add(conflict);
                Activity.Finish(UiText.Get("activity.failed"));
            }
            catch
            {
                Activity.Finish(UiText.Get("activity.cancelled"));
                throw;
            }
        }
    }

    internal async Task ResolveConflictAsync(PairConflict conflict, ConflictChoice choice, CancellationToken cancel = default)
    {
        var current = engine;
        var logger = pairLogger ?? Log.Logger;
        if (current == null || conflict.Choice == null)
        {
            throw new InvalidOperationException(UiText.Get("pair.conflictFromOtherPair"));
        }

        logger.Information("Resolving sync conflict at {ConflictKey} ({ConflictReason}) with {ConflictChoice}", conflict.Key, conflict.Reason, choice);
        string? copyKey;
        await passGate.WaitAsync(cancel);
        try
        {
            copyKey = await Task.Run(() => current.ResolveConflictAsync(conflict.Choice, choice, cancel), cancel);
        }
        catch (Exception ex)
        {
            logger.ForContext("ConflictKey", conflict.Key).ForContext("ConflictChoice", choice).WriteFailure(LogEventLevel.Warning, "Resolve sync conflict", ex);
            throw;
        }
        finally
        {
            passGate.Release();
        }

        lock (conflictGate)
        {
            if (conflicts.Remove(conflict.Key))
            {
                conflictList = conflicts.Values.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase).ToArray();
                ConflictRevision++;
            }
        }

        logger.Information("Resolved sync conflict at {ConflictKey} with {ConflictChoice}", conflict.Key, choice);
        // The next path scan records the outcome and uploads a kept copy; it reports the path again if it still conflicts.
        wake?.SignalPaths(copyKey == null ? [conflict.Key] : [conflict.Key, copyKey]);
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
