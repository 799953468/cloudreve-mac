using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Serilog;
using Serilog.Events;

namespace Cloudreve.Desktop;

internal sealed class WorkspaceHost(DriveProfiles profiles, WindowIcon? icon = null, HttpMessageHandler? transport = null)
{
    private readonly List<MountedDrive> drives = [];
    private readonly HttpClient api = new(transport ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
    private readonly HttpClient storage = new(new HttpClientHandler { UseCookies = false });
    private bool stopping;

    internal IReadOnlyList<MountedDrive> Drives
    {
        get { return drives; }
    }

    internal AddDriveWindow? Wizard { get; private set; }
    internal SettingsWindow? Settings { get; private set; }
    internal ActivityWindow? Popup { get; private set; }
    internal event Action? Changed;

    internal void Start()
    {
        foreach (var store in profiles.OtherStores().Prepend(profiles.Primary))
        {
            try
            {
                if (store.Load() is { } account)
                {
                    Add(store, account);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                Log.Logger.WriteFailure(LogEventLevel.Warning, "Restore drive", ex);
            }
        }

        if (drives.Count == 0)
        {
            OpenAdd();
        }
        else if (drives.FirstOrDefault(d => d.Pair == null) is { } legacy)
        {
            OpenAdd(legacy);
        }
    }

    private MountedDrive Add(SessionFileStore store, DriveSession account)
    {
        var drive = new MountedDrive(profiles, store, api, storage);
        drive.Notice += text =>
        {
            Window? visible = Settings?.IsVisible == true ? Settings : Popup?.IsVisible == true ? Popup : null;
            if (visible != null)
            {
                new WindowNotificationManager(visible) { MaxItems = 1 }.Show(new Notification(account.Name, text));
            }
        };
        drive.Bind(account);
        drives.Add(drive);
        return drive;
    }

    internal void OpenAdd(MountedDrive? existing = null)
    {
        if (stopping)
        {
            return;
        }

        if (Wizard == null)
        {
            Wizard = new AddDriveWindow(this, api, existing) { Icon = icon };
            Wizard.Closed += (_, _) => { Wizard = null; };
        }

        Show(Wizard);
    }

    internal void OpenSettings()
    {
        if (stopping)
        {
            return;
        }

        if (Settings == null)
        {
            Settings = new SettingsWindow(this) { Icon = icon };
            Settings.Closed += (_, _) => { Settings = null; };
        }

        Show(Settings);
    }

    internal void OpenPopup(bool atTray = false)
    {
        if (stopping)
        {
            return;
        }

        if (Popup == null)
        {
            Popup = new ActivityWindow(this) { Icon = icon };
            Popup.Closed += (_, _) => { Popup = null; };
        }

        Show(Popup);
        if (atTray && Popup.TryGetPlatformHandle() is { HandleDescriptor: "NSWindow" or "NSView" } handle)
        {
            MacTray.Position(handle.Handle);
        }
    }

    private static void Show(Window window)
    {
        window.Show();
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    }

    internal DriveClient VerifyClient(DriveSession account, Action<DriveSession> refreshed)
    {
        return new DriveClient(api, storage, account, refreshed);
    }

    internal async Task InstallAsync(DriveSession account, string? local, MountedDrive? existing, CancellationToken cancel)
    {
        var store = existing?.Store ?? (drives.All(d => d.Store != profiles.Primary) ? profiles.Primary : profiles.Create());
        var pairStore = new FolderPairStore(Path.Combine(store.DirectoryPath, "folder-pairs"), account);
        FolderPair? created = null;
        if (existing?.Pair == null)
        {
            CheckLocalFolder(local!);
            var client = VerifyClient(account, updated => { account = updated; });
            string id = await client.FolderIdAsync(account.Root, cancel);
            cancel.ThrowIfCancellationRequested();
            CheckLocalFolder(local!);
            profiles.Reserve(store, local!);
            try
            {
                created = pairStore.Create(local!, account.Root, id);
            }
            catch
            {
                profiles.Release(store);
                throw;
            }
        }

        try
        {
            if (existing != null)
            {
                await existing.StopAsync();
            }

            cancel.ThrowIfCancellationRequested();
            store.Save(account);
        }
        catch
        {
            if (created != null)
            {
                pairStore.Disconnect(created);
                profiles.Release(store);
            }

            if (existing != null && !stopping)
            {
                existing.Bind(existing.Account);
            }

            throw;
        }

        if (existing == null)
        {
            Add(store, account);
        }
        else
        {
            existing.Bind(account);
        }

        Changed?.Invoke();
    }

    internal static void CheckLocalFolder(string local)
    {
        if (!Path.IsPathFullyQualified(local))
        {
            throw new IOException(UiText.Get("请选择本机文件夹。"));
        }

        FolderPairStore.CheckDirectory(Cloudreve.Sync.MacFile.NormalizeSystemPath(local));
    }

    internal async Task RemoveAsync(MountedDrive drive)
    {
        if (Wizard?.Existing == drive)
        {
            Wizard.Close();
        }

        await drive.StopAsync();
        try
        {
            if (drive.Pair != null)
            {
                drive.PairStore.Disconnect(drive.Pair);
            }

            drive.Store.Forget();
        }
        catch
        {
            drive.Bind(drive.Account);
            throw;
        }

        drives.Remove(drive);
        Changed?.Invoke();
        if (drives.Count == 0)
        {
            OpenAdd();
        }
    }

    internal void Accept(Uri callback)
    {
        if (!stopping && Wizard != null && Wizard.Accept(callback))
        {
            Show(Wizard);
        }
    }

    internal async Task StopAsync()
    {
        stopping = true;
        Wizard?.Close();
        await Task.WhenAll(drives.Select(drive => drive.StopAsync()));
        api.Dispose();
        storage.Dispose();
    }
}
