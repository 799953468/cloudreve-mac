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

    internal SessionFileStore Store { get; } = store;
    internal DriveSession Account { get; private set; } = null!;
    internal FolderPairStore PairStore { get; private set; } = null!;
    internal FolderPair? Pair { get; private set; }
    internal SyncActivity Activity { get; } = new();
    internal (long Used, long Total)? Capacity { get; private set; }
    internal bool PushLost { get; private set; }
    internal bool Expired { get; private set; }
    internal event Action<string>? Notice;

    internal void Bind(DriveSession account)
    {
        var current = new object();
        binding = current;
        Account = account;
        Expired = account.RefreshExpires <= DateTimeOffset.UtcNow;
        PushLost = false;
        PairStore = new FolderPairStore(Path.Combine(Store.DirectoryPath, "folder-pairs"), account);
        Pair = PairStore.Load();
        if (Pair != null)
        {
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
            var engine = new FolderPairSync(PairStore, Pair, client, Activity);
            wake = new PairWake();
            watcher = wake.WatchLocal(Pair.Local);
            workers = Task.WhenAll(capacity, ScanAsync(engine, wake, lifetime.Token),
                wake.WatchRemoteAsync(client, Pair.Remote, lifetime.Token, connected => { PushLost = !connected; }));
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

    private async Task ScanAsync(FolderPairSync engine, PairWake signal, CancellationToken cancel)
    {
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                try
                {
                    var result = await Task.Run(() => engine.RunAsync(cancel), cancel);
                    if (Program.Preferences.Current.ConflictAlerts && result.Conflicts.Any(key => !conflicts.Contains(key)))
                    {
                        Notice?.Invoke(UiText.Get("同步冲突"));
                    }

                    conflicts = result.Conflicts.ToHashSet(StringComparer.OrdinalIgnoreCase);
                }
                catch (Exception ex) when (!cancel.IsCancellationRequested)
                {
                    CheckExpiry(ex);
                    Log.Logger.WriteFailure(LogEventLevel.Warning, "Synchronize folder pair", ex);
                }

                await signal.WaitAsync(TimeSpan.FromSeconds(30), cancel);
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
            wake?.Signal();
        }
    }

    internal async Task StopAsync()
    {
        watcher?.Dispose();
        watcher = null;
        lifetime?.Cancel();
        await workers;
        lock (bindingGate)
        {
            binding = null;
        }

        lifetime?.Dispose();
        lifetime = null;
        profiles.Release(Store);
    }
}