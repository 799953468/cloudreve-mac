using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Serilog;
using Serilog.Events;

namespace Cloudreve.Desktop;

internal sealed partial class WorkspaceWindow : Window
{
    // Site requests (especially token exchanges) must not follow redirects carrying a body.
    private readonly HttpClient api;
    private readonly HttpClient storage = new(new HttpClientHandler { UseCookies = false });
    private readonly CancellationTokenSource shutdown = new();
    private readonly SemaphoreSlim operations = new(1, 1);
    private readonly Stack<string> history = new();
    private readonly SessionFileStore sessions;
    private readonly DriveProfiles profiles;
    private readonly object sessionGate = new();
    private CancellationTokenSource? watching;
    private DriveSession? account;
    private DriveClient? drive;
    private DesktopLogin? pending;
    private string directory = "";
    private bool busy;
    private bool restoreAttempted;
    internal event Action<string, bool>? StatusChanged;

    internal WorkspaceWindow() : this(SessionFileStore.OpenDefault())
    {
    }

    internal WorkspaceWindow(SessionFileStore sessions, HttpMessageHandler? siteTransport = null, DriveProfiles? profiles = null)
    {
        this.sessions = sessions;
        this.profiles = profiles ?? new DriveProfiles(sessions);
        api = new HttpClient(siteTransport ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        settingsRoot = Path.Combine(sessions.DirectoryPath, "folder-pairs");
        InitializeComponent();
        Opened += async (_, _) =>
        {
            if (!restoreAttempted)
            {
                await RunAsync("Restore session", async () =>
                {
                    restoreAttempted = true;
                    var saved = sessions.Load();
                    if (saved != null)
                    {
                        Bind(saved);
                        await LoadAsync(directory);
                    }
                });
            }
        };
        Endpoint.TextChanged += (_, _) => { UpdateControls(); };
        Files.SelectionChanged += (_, _) => { UpdateControls(); };
        ConnectButton.Click += async (_, _) => { await RunAsync("Connect drive", BeginAsync); };
        ReauthorizeButton.Click += async (_, _) => { await RunAsync("Reauthorize drive", BeginAsync); };
        CancelLogin.Click += (_, _) =>
        {
            pending = null;
            StatusText.Text = account == null ? UiText.Get("等待连接") : UiText.Get("已取消重新授权");
            UpdateControls();
        };
        RefreshButton.Click += async (_, _) => { await RefreshAsync(); };
        OpenFolderButton.Click += async (_, _) => { await RunAsync("Open directory", OpenSelectedAsync); };
        DownloadButton.Click += async (_, _) => { await RunAsync("Download file", DownloadSelectedAsync); };
        UploadButton.Click += async (_, _) => { await RunAsync("Upload file", UploadSelectedAsync); };
        NewFolderItem.Click += async (_, _) => { await RunAsync("Create folder", CreateCloudFolderAsync); };
        RenameItem.Click += async (_, _) => { await RunAsync("Rename item", RenameCloudItemAsync); };
        MoveItem.Click += async (_, _) => { await RunAsync("Move item", MoveCloudItemAsync); };
        TrashItem.Click += async (_, _) => { await RunAsync("Trash item", TrashCloudItemAsync); };
        IgnoreItem.Click += async (_, _) => { await RunAsync("Edit pair ignore rules", EditIgnoreRulesAsync); };
        ConflictsItem.Click += async (_, _) => { await RunAsync("Resolve file conflict", ResolveSelectedConflictAsync); };
        ActivityItem.Click += async (_, _) => { await RunAsync("Show sync activity", ShowActivityAsync); };
        PairButton.Click += async (_, _) => { await RunAsync("Configure folder pair", ConfigurePairAsync); };
        SyncNowButton.Click += (_, _) => { RequestSync(); };
        BackButton.Click += async (_, _) =>
        {
            await RunAsync("Return to directory", async () =>
            {
                if (history.TryPeek(out string? previous))
                {
                    await LoadAsync(previous);
                    history.Pop();
                }
            });
        };
        DisconnectButton.Click += async (_, _) =>
        {
            await RunAsync("Disconnect drive", async () =>
            {
                await UnpairAsync();
                lock (sessionGate)
                {
                    sessions.Forget();
                    activeBinding = null;
                }

                StopWatching();
                pending = null;
                drive = null;
                account = null;
                directory = "";
                history.Clear();
                Files.ItemsSource = null;
                DriveName.Text = null;
                LocationText.Text = null;
                StatusText.Text = UiText.Get("已移除本机授权");
            });
        };
        UpdateControls();
    }

    private async Task BeginAsync()
    {
        pending = null;
        var login = new DesktopLogin(api, account?.Site ?? Endpoint.Text ?? "");
        var url = await login.BeginAsync(shutdown.Token);
        pending = login;
        if (!await Launcher.LaunchUriAsync(url))
        {
            pending = null;
            throw new InvalidOperationException(UiText.Get("无法打开系统浏览器。"));
        }

        StatusText.Text = UiText.Get("正在等待浏览器授权…");
    }

    internal bool OwnsCallback(Uri callback)
    {
        return pending?.MatchesCallback(callback) == true;
    }

    internal void ReportUnmatchedCallback()
    {
        StatusText.Text = UiText.Get("没有与此回调匹配的授权，请在对应的云盘窗口重新连接。");
    }

    internal string ProfileLabel
    {
        get { return account == null ? UiText.Get("未连接") : $"{account.Name} ({new Uri(account.Site).Host})"; }
    }

    internal Task AcceptCallbackAsync(Uri callback)
    {
        return RunAsync("Complete authorization", async () =>
        {
            var login = pending ?? throw new InvalidOperationException(UiText.Get("没有等待中的授权，请先在应用中连接云盘。"));
            DriveSession candidate = await login.CompleteAsync(callback, shutdown.Token);
            pending = null;
            var verifying = new DriveClient(api, storage, candidate, updated => { candidate = updated; });
            string serverUser = await verifying.UserIdAsync(shutdown.Token);
            candidate = DesktopLogin.VerifyBinding(candidate, serverUser, account);
            await StopPairLoopAsync();
            Bind(candidate, save: true);
            await LoadAsync(directory);
        });
    }

    private void Bind(DriveSession saved, bool save = false)
    {
        StopWatching();
        var binding = new object();
        lock (sessionGate)
        {
            if (save)
            {
                sessions.Save(saved);
            }

            activeBinding = binding;
        }

        account = saved;
        drive = new DriveClient(api, storage, saved, updated =>
        {
            lock (sessionGate)
            {
                if (activeBinding == binding)
                {
                    sessions.Save(updated);
                }
            }
        });
        Endpoint.Text = saved.Site;
        directory = saved.Root;
        DriveName.Text = saved.Name;
        LocationText.Text = directory;
        history.Clear();
        Files.ItemsSource = null;
        RestorePair(saved);
        UpdateControls();
    }

    private object? activeBinding;

    private async Task LoadAsync(string target)
    {
        if (drive == null || account == null)
        {
            return;
        }

        StatusText.Text = UiText.Get("正在读取目录…");
        var entries = await drive.ListAsync(target, shutdown.Token);
        directory = target;
        Files.ItemsSource = entries;
        LocationText.Text = directory;
        StatusText.Text = UiText.Format($"{entries.Count} 个项目");
        StopWatching();
        watching = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
        _ = ObserveAsync(drive, directory, watching);
    }

    private async Task ObserveAsync(DriveClient client, string path, CancellationTokenSource lifetime)
    {
        try
        {
            await client.WatchAsync(path, signal =>
            {
                if (watching != lifetime)
                {
                    return;
                }

                Changes.Text = signal switch
                {
                    "event" => UiText.Get("远端目录有变更，刷新可查看。"),
                    "disconnected" => UiText.Get("变更通知已断开，正在重连…"),
                    "resumed" => UiText.Get("已恢复变更通知，刷新可查看断线期间的变更。"),
                    _ => UiText.Get("已订阅目录变更")
                };
            }, lifetime.Token);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception) when (watching != lifetime)
        {
        }
        catch (Exception ex)
        {
            Log.Logger.WriteFailure(LogEventLevel.Warning, "Watch directory", ex);
            Changes.Text = UiText.Get("无法订阅变更，可手动刷新或重新授权。");
        }
        finally
        {
            if (watching == lifetime)
            {
                watching = null;
            }

            lifetime.Dispose();
        }
    }

    private async Task OpenSelectedAsync()
    {
        if (Files.SelectedItem is DriveEntry { IsFolder: true } folder)
        {
            string previous = directory;
            await LoadAsync(folder.Uri);
            history.Push(previous);
        }
    }

    private async Task DownloadSelectedAsync()
    {
        if (drive != null && Files.SelectedItem is DriveEntry { IsFolder: false } file)
        {
            using (var target = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                       { Title = UiText.Get("保存文件"), SuggestedFileName = Path.GetFileName(file.Name), ShowOverwritePrompt = true }))
            {
                if (target == null)
                {
                    return;
                }

                string path = target.TryGetLocalPath() ?? throw new IOException(UiText.Get("请选择本机文件位置。"));
                StatusText.Text = UiText.Get("正在下载 ") + file.Name;
                await drive.DownloadAsync(file, path, shutdown.Token);
                StatusText.Text = UiText.Get("已下载到 ") + path;
            }
        }
    }

    internal Task RefreshAsync()
    {
        return RunAsync("Refresh directory", () => LoadAsync(directory));
    }

    private async Task RunAsync(string operation, Func<Task> action)
    {
        await operations.WaitAsync();
        try
        {
            if (shutdown.IsCancellationRequested)
            {
                return;
            }

            busy = true;
            UpdateControls();
            await action();
        }
        catch (HttpRequestException ex)
        {
            Log.Logger.WriteFailure(LogEventLevel.Warning, operation, ex);
            StatusText.Text = ex.StatusCode == null ? UiText.Get("无法连接服务器，请检查网络。") : UiText.Format($"请求失败（HTTP {(int)ex.StatusCode}），请检查站点及授权。");
        }
        catch (OperationCanceledException)
        {
            if (!shutdown.IsCancellationRequested)
            {
                Log.Information("{Operation} was cancelled or timed out", operation);
            }

            StatusText.Text = UiText.Get("请求已取消或超时。");
        }
        catch (Exception ex)
        {
            Log.Logger.WriteFailure(LogEventLevel.Error, operation, ex);
            StatusText.Text = ex.Message;
        }
        finally
        {
            busy = false;
            UpdateControls();
            operations.Release();
        }
    }

    private void UpdateControls()
    {
        bool waiting = pending != null;
        Setup.IsVisible = account == null || waiting;
        Workspace.IsVisible = !Setup.IsVisible;
        AddressField.IsVisible = !waiting;
        SetupTitle.Text = waiting ? UiText.Get("等待浏览器授权") : UiText.Get("连接云盘");
        SetupHint.Text = waiting ? UiText.Get("请在系统浏览器中完成站点授权。") : UiText.Get("输入 Cloudreve 站点地址，在浏览器中完成授权。");
        Endpoint.IsEnabled = !busy && account == null;
        ConnectButton.Content = waiting ? UiText.Get("重新发起授权") : UiText.Get("连接云盘");
        ConnectButton.IsEnabled = Setup.IsVisible && restoreAttempted && !busy && !string.IsNullOrWhiteSpace(Endpoint.Text);
        CancelLogin.IsVisible = waiting;
        CancelLogin.IsEnabled = !busy;
        bool ready = !busy && drive != null;
        ReauthorizeButton.IsEnabled = ready;
        DisconnectButton.IsEnabled = ready;
        EmptyFolder.IsVisible = Files.ItemsSource != null && Files.ItemCount == 0 && !busy;
        RefreshButton.IsEnabled = ready;
        BackButton.IsEnabled = ready && history.Count > 0;
        OpenFolderButton.IsEnabled = ready && Files.SelectedItem is DriveEntry { IsFolder: true };
        DownloadButton.IsEnabled = ready && Files.SelectedItem is DriveEntry { IsFolder: false };
        UploadButton.IsEnabled = ready && Files.ItemsSource != null;
        ManageButton.IsEnabled = ready && Files.ItemsSource != null;
        NewFolderItem.IsEnabled = ManageButton.IsEnabled;
        bool selected = ManageButton.IsEnabled && Files.SelectedItem is DriveEntry;
        RenameItem.IsEnabled = selected;
        MoveItem.IsEnabled = selected;
        TrashItem.IsEnabled = selected;
        IgnoreItem.IsEnabled = ready && pair != null && pairStore != null;
        ConflictsItem.IsEnabled = ready && pair != null && fileConflicts.Count != 0;
        ActivityItem.IsEnabled = ready && pair != null;
        PairButton.IsEnabled = ready && (pair != null || pairStore != null && Files.ItemsSource != null);
        PairButton.Content = pair == null ? UiText.Get("配对当前目录…") : UiText.Get("停止配对");
        SyncNowButton.IsVisible = pair != null;
        SyncNowButton.IsEnabled = ready && pairLifetime != null;
        StatusChanged?.Invoke(StatusText.Text ?? "Cloudreve", ready);
    }

    private void StopWatching()
    {
        var previous = watching;
        watching = null;
        previous?.Cancel();
        Changes.Text = "";
    }

    internal void Stop()
    {
        pairWatcher?.Dispose();
        pairWatcher = null;
        pending = null;
        lock (sessionGate)
        {
            activeBinding = null;
        }

        shutdown.Cancel();
        StopWatching();
        api.Dispose();
        storage.Dispose();
    }
}