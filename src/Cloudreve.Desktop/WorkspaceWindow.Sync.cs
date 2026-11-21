using Avalonia.Platform.Storage;
using Avalonia.Controls.Notifications;
using Serilog;
using Serilog.Events;

namespace Cloudreve.Desktop;

internal sealed partial class WorkspaceWindow
{
    private readonly string settingsRoot;
    private FolderPairStore? pairStore;
    private FolderPair? pair;
    private CancellationTokenSource? pairLifetime;
    private Task pairTask = Task.CompletedTask;
    private PairWake? wakePair;
    private FileSystemWatcher? pairWatcher;
    private IReadOnlyList<FileConflict> fileConflicts = [];
    private SyncActivity pairActivity = new();
    private HashSet<string> lastConflicts = new(StringComparer.OrdinalIgnoreCase);
    private WindowNotificationManager? notifications;

    private void RestorePair(DriveSession saved)
    {
        profiles.Release(sessions);
        pairStore = new FolderPairStore(settingsRoot, saved);
        pairActivity = new SyncActivity();
        fileConflicts = [];
        lastConflicts.Clear();
        pair = null;
        PairLocation.Text = UiText.Get("未配对本地文件夹");
        PairStatus.Text = null;
        try
        {
            pair = pairStore.Load();
            if (pair != null)
            {
                profiles.Reserve(sessions, pair.Local);
                StartPairLoop();
            }
        }
        catch (Exception ex)
        {
            Log.Logger.WriteFailure(LogEventLevel.Warning, "Restore folder pair", ex);
            PairStatus.Text = UiText.Get("无法读取配对记录：") + ex.Message;
            if (pair == null)
            {
                pairStore = null;
            }
        }
    }

    private async Task ConfigurePairAsync()
    {
        if (pair != null)
        {
            await UnpairAsync();
            return;
        }

        var selected = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = UiText.Get("选择与当前云端目录双向同步的本地文件夹"), AllowMultiple = false
        });
        using (var folder = selected.FirstOrDefault())
        {
            if (folder == null || pairStore == null || shutdown.IsCancellationRequested)
            {
                return;
            }

            string local = folder.TryGetLocalPath() ?? throw new IOException(UiText.Get("请选择本机文件夹。"));
            string remoteId = await drive!.FolderIdAsync(directory, shutdown.Token);
            profiles.Reserve(sessions, local);
            try
            {
                pair = pairStore.Create(local, directory, remoteId);
            }
            catch
            {
                profiles.Release(sessions);
                throw;
            }

            StartPairLoop();
        }
    }

    private void StartPairLoop()
    {
        profiles.Reserve(sessions, pair!.Local);
        var engine = new FolderPairSync(pairStore!, pair!, drive!, pairActivity);
        PairLocation.Text = $"{pair!.Local} ↔ {pair.Remote}";
        pairLifetime = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
        wakePair = new PairWake();
        pairWatcher = wakePair.WatchLocal(pair.Local);
        pairTask = Task.WhenAll(RunPairLoopAsync(engine, pairLifetime.Token, wakePair),
            wakePair.WatchRemoteAsync(drive!, pair.Remote, pairLifetime.Token));
    }

    private void RequestSync()
    {
        wakePair?.Signal();
    }

    private async Task RunPairLoopAsync(FolderPairSync engine, CancellationToken cancel, PairWake wake)
    {
        try
        {
            while (true)
            {
                cancel.ThrowIfCancellationRequested();
                PairStatus.Text = UiText.Get("正在同步…");
                try
                {
                    var result = await Task.Run(() => engine.RunAsync(cancel), cancel);
                    cancel.ThrowIfCancellationRequested();
                    fileConflicts = result.Choices;
                    if (Program.Preferences.Current.ConflictAlerts && IsVisible && result.Conflicts.Any(key => !lastConflicts.Contains(key)))
                    {
                        notifications ??= new WindowNotificationManager(this) { MaxItems = 1 };
                        notifications.Show(new Notification(UiText.Get("同步冲突"), UiText.Format($"{result.Conflicts.Count} 项未覆盖，请查看配对状态。"), NotificationType.Warning));
                    }

                    lastConflicts = result.Conflicts.ToHashSet(StringComparer.OrdinalIgnoreCase);
                    Log.Debug("Pair scan completed: uploaded {Uploaded}, downloaded {Downloaded}, removed {Removed}, conflicts {Conflicts}",
                        result.Uploaded, result.Downloaded, result.Removed, result.Conflicts.Count);
                    UpdateControls();
                    PairStatus.Text = result.Conflicts.Count == 0
                        ? UiText.Format($"{DateTime.Now:HH:mm:ss} 已同步：上传 {result.Uploaded}，下载 {result.Downloaded}，移除 {result.Removed}")
                        : UiText.Format($"{result.Conflicts.Count} 项未覆盖，请核对：") + string.Join(UiText.Get("、"), result.Conflicts);
                }
                catch (Exception ex) when (!cancel.IsCancellationRequested)
                {
                    Log.Logger.WriteFailure(LogEventLevel.Warning, "Synchronize folder pair", ex);
                    PairStatus.Text = UiText.Get("同步未完成，将自动重试：") + ex.Message;
                }

                await wake.WaitAsync(TimeSpan.FromSeconds(30), cancel);
            }
        }
        catch (Exception) when (cancel.IsCancellationRequested)
        {
            // Closing the app or unpairing may also interrupt an in-flight HTTP request.
        }
    }

    private async Task StopPairLoopAsync()
    {
        pairWatcher?.Dispose();
        pairWatcher = null;
        pairLifetime?.Cancel();
        await pairTask;
        pairLifetime?.Dispose();
        pairLifetime = null;
        wakePair = null;
    }

    internal async Task PrepareToQuitAsync()
    {
        shutdown.Cancel();
        StopWatching();
        await StopPairLoopAsync();
    }

    private async Task UnpairAsync()
    {
        await StopPairLoopAsync();
        pairActivity = new SyncActivity();
        lastConflicts.Clear();
        fileConflicts = [];
        if (pair != null)
        {
            pairStore!.Disconnect(pair);
            pair = null;
        }

        profiles.Release(sessions);

        PairLocation.Text = UiText.Get("未配对本地文件夹");
        PairStatus.Text = null;
    }

    private async Task UploadSelectedAsync()
    {
        if (drive == null)
        {
            return;
        }

        var selected = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = UiText.Get("上传到当前云端目录"), AllowMultiple = false });
        using (var file = selected.FirstOrDefault())
        {
            if (file == null)
            {
                return;
            }

            string path = file.TryGetLocalPath() ?? throw new IOException(UiText.Get("请选择本机文件。"));
            string target = DriveClient.ChildUri(directory, file.Name);
            if ((await drive.ListAsync(directory, shutdown.Token)).Any(entry => string.Equals(entry.Name.Normalize(), file.Name.Normalize(), StringComparison.OrdinalIgnoreCase)))
            {
                throw new IOException(UiText.Get("云端已有同名项目，未覆盖。请更名后上传。"));
            }

            StatusText.Text = UiText.Get("正在上传 ") + file.Name;
            await drive.UploadAsync(path, target, null, shutdown.Token);
            await LoadAsync(directory);
            StatusText.Text = UiText.Get("已上传 ") + file.Name;
            RequestSync();
        }
    }
}