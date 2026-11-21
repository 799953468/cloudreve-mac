using Avalonia.Controls;
using Serilog;
using Serilog.Events;

namespace Cloudreve.Desktop;

internal sealed class WorkspaceHost(DriveProfiles profiles, WindowIcon icon)
{
    private readonly List<WorkspaceWindow> windows = [];
    private WorkspaceWindow? active;
    private bool stopping;

    internal NativeMenu Drives { get; } = new();
    internal event Action<string, bool>? ActiveStatusChanged;

    internal WorkspaceWindow OpenPrimary()
    {
        return Add(profiles.Primary);
    }

    internal void RestoreOthers()
    {
        try
        {
            foreach (var store in profiles.OtherStores())
            {
                var window = Add(store);
                window.Show();
                window.Hide();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Logger.WriteFailure(LogEventLevel.Warning, "Discover drive profiles", ex);
        }

        active = windows[0];
    }

    internal void OpenNew()
    {
        if (!stopping)
        {
            Show(Add(profiles.Create()));
        }
    }

    private WorkspaceWindow Add(SessionFileStore store)
    {
        var window = new WorkspaceWindow(store, profiles: profiles) { Icon = icon };
        windows.Add(window);
        active ??= window;
        int ordinal = windows.Count;
        var entry = new NativeMenuItem($"{ordinal}. {window.ProfileLabel}");
        entry.Click += (_, _) => { Show(window); };
        Drives.Items.Add(entry);
        window.StatusChanged += (text, ready) =>
        {
            entry.Header = $"{ordinal}. {window.ProfileLabel}";
            if (active == window)
            {
                ActiveStatusChanged?.Invoke(text, ready);
            }
        };
        window.Activated += (_, _) =>
        {
            active = window;
            ActiveStatusChanged?.Invoke(window.StatusText.Text ?? "Cloudreve", window.RefreshButton.IsEnabled);
        };
        window.Closing += (_, args) =>
        {
            if (!stopping && args.CloseReason == WindowCloseReason.WindowClosing)
            {
                args.Cancel = true;
                window.Hide();
            }
        };
        return window;
    }

    private void Show(WorkspaceWindow window)
    {
        if (!stopping)
        {
            active = window;
            window.Show();
            window.Activate();
        }
    }

    internal void ShowActive()
    {
        if (active != null)
        {
            Show(active);
        }
    }

    internal async Task RefreshActiveAsync()
    {
        if (!stopping && active != null)
        {
            await active.RefreshAsync();
        }
    }

    internal void ReportFailure(string message)
    {
        ShowActive();
        if (active != null)
        {
            active.StatusText.Text = message;
        }
    }

    internal async Task AcceptAsync(Uri uri)
    {
        if (stopping)
        {
            return;
        }

        var owner = windows.FirstOrDefault(window => window.OwnsCallback(uri));
        if (owner == null)
        {
            ShowActive();
            active?.ReportUnmatchedCallback();
            return;
        }

        Show(owner);
        await owner.AcceptCallbackAsync(uri);
    }

    internal async Task PrepareToQuitAsync()
    {
        stopping = true;
        await Task.WhenAll(windows.Select(window => window.PrepareToQuitAsync()));
    }

    internal void Stop()
    {
        foreach (var window in windows)
        {
            window.Stop();
        }
    }
}