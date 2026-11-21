using Avalonia.Platform.Storage;
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
    private SemaphoreSlim? wakePair;

    private void RestorePair(DriveSession saved)
    {
        pairStore = new FolderPairStore(settingsRoot, saved);
        pair = null;
        PairLocation.Text = "未配对本地文件夹";
        PairStatus.Text = null;
        try
        {
            pair = pairStore.Load();
            if (pair != null)
            {
                StartPairLoop();
            }
        }
        catch (Exception ex)
        {
            Log.Logger.WriteFailure(LogEventLevel.Warning, "Restore folder pair", ex);
            PairStatus.Text = "无法读取配对记录：" + ex.Message;
            pairStore = null;
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
            Title = "选择与当前云端目录双向同步的本地文件夹", AllowMultiple = false
        });
        using (var folder = selected.FirstOrDefault())
        {
            if (folder == null || pairStore == null || shutdown.IsCancellationRequested)
            {
                return;
            }

            string local = folder.TryGetLocalPath() ?? throw new IOException("请选择本机文件夹。");
            string remoteId = await drive!.FolderIdAsync(directory, shutdown.Token);
            pair = pairStore.Create(local, directory, remoteId);
            StartPairLoop();
        }
    }

    private void StartPairLoop()
    {
        var engine = new FolderPairSync(pairStore!, pair!, drive!);
        PairLocation.Text = $"{pair!.Local} ↔ {pair.Remote}";
        pairLifetime = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
        wakePair = new SemaphoreSlim(0, 1);
        pairTask = RunPairLoopAsync(engine, pairLifetime.Token, wakePair);
    }

    private void RequestSync()
    {
        if (wakePair?.CurrentCount == 0)
        {
            wakePair.Release();
        }
    }

    private async Task RunPairLoopAsync(FolderPairSync engine, CancellationToken cancel, SemaphoreSlim wake)
    {
        try
        {
            while (true)
            {
                cancel.ThrowIfCancellationRequested();
                PairStatus.Text = "正在同步…";
                try
                {
                    var result = await Task.Run(() => engine.RunAsync(cancel), cancel);
                    cancel.ThrowIfCancellationRequested();
                    PairStatus.Text = result.Conflicts.Count == 0
                        ? $"{DateTime.Now:HH:mm:ss} 已同步：上传 {result.Uploaded}，下载 {result.Downloaded}，移除 {result.Removed}"
                        : $"{result.Conflicts.Count} 项未覆盖，请核对：" + string.Join("、", result.Conflicts);
                }
                catch (Exception ex) when (!cancel.IsCancellationRequested)
                {
                    Log.Logger.WriteFailure(LogEventLevel.Warning, "Synchronize folder pair", ex);
                    PairStatus.Text = "同步未完成，将自动重试：" + ex.Message;
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
        pairLifetime?.Cancel();
        await pairTask;
        pairLifetime?.Dispose();
        pairLifetime = null;
        wakePair?.Dispose();
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
        if (pair != null)
        {
            pairStore!.Disconnect(pair);
            pair = null;
        }

        PairLocation.Text = "未配对本地文件夹";
        PairStatus.Text = null;
    }

    private async Task UploadSelectedAsync()
    {
        if (drive == null)
        {
            return;
        }

        var selected = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "上传到当前云端目录", AllowMultiple = false });
        using (var file = selected.FirstOrDefault())
        {
            if (file == null)
            {
                return;
            }

            string path = file.TryGetLocalPath() ?? throw new IOException("请选择本机文件。");
            string target = DriveClient.ChildUri(directory, file.Name);
            if ((await drive.ListAsync(directory, shutdown.Token)).Any(entry => string.Equals(entry.Name.Normalize(), file.Name.Normalize(), StringComparison.OrdinalIgnoreCase)))
            {
                throw new IOException("云端已有同名项目，未覆盖。请更名后上传。");
            }

            StatusText.Text = "正在上传 " + file.Name;
            await drive.UploadAsync(path, target, null, shutdown.Token);
            await LoadAsync(directory);
            StatusText.Text = "已上传 " + file.Name;
            RequestSync();
        }
    }
}
