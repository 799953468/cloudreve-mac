using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Serilog;
using Serilog.Events;

namespace Cloudreve.Desktop;

internal static class Program
{
    internal static DesktopPreferences Preferences { get; } = new(SessionFileStore.OpenDefault().DirectoryPath);

    [STAThread]
    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsMacOS())
        {
            Console.Error.WriteLine("Cloudreve Mac requires macOS.");
            return 1;
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Log.Logger = DesktopLog.CreateLogger(Path.Combine(home, "Library", "Logs", "Cloudreve"), Preferences.LogLevel);
        Preferences.Load();
        UiText.Language = Preferences.Current.Language;
        try
        {
            Log.Information("Starting Cloudreve Desktop");
            int exitCode = AppBuilder.Configure<DesktopApp>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
            Log.Information("Cloudreve Desktop stopped with exit code {ExitCode}", exitCode);
            return exitCode;
        }
        catch (Exception ex)
        {
            Log.Logger.WriteFailure(LogEventLevel.Fatal, "Desktop lifetime", ex);
            throw;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
}

internal sealed class DesktopApp : Application
{
    public override void Initialize()
    {
        Name = "Cloudreve";
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            WindowIcon appIcon;
            using (var icon = AssetLoader.Open(new Uri("avares://CloudreveMac/Assets/cloudreve.png")))
            {
                appIcon = new WindowIcon(icon);
            }

            var host = new WorkspaceHost(new DriveProfiles(SessionFileStore.OpenDefault()), appIcon);
            var window = host.OpenPrimary();
            bool restored = false;
            window.Opened += (_, _) =>
            {
                if (!restored)
                {
                    restored = true;
                    host.RestoreOthers();
                }
            };
            if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime lifetime)
            {
                lifetime.Activated += async (_, activation) =>
                {
                    if (activation is ProtocolActivatedEventArgs protocol)
                    {
                        await host.AcceptAsync(protocol.Uri);
                    }
                };
            }

            var menu = new NativeMenu();
            var show = new NativeMenuItem(UiText.Get("打开工作区"));
            var drives = new NativeMenuItem(UiText.Get("云盘")) { Menu = host.Drives };
            var add = new NativeMenuItem(UiText.Get("添加云盘…"));
            var refresh = new NativeMenuItem(UiText.Get("刷新目录"));
            var quit = new NativeMenuItem(UiText.Get("退出 Cloudreve"));
            show.Click += (_, _) => { host.ShowActive(); };
            add.Click += (_, _) => { host.OpenNew(); };
            refresh.Click += async (_, _) => { await host.RefreshActiveAsync(); };
            quit.Click += (_, _) => { desktop.TryShutdown(); };
            menu.Items.Add(show);
            menu.Items.Add(drives);
            menu.Items.Add(add);
            menu.Items.Add(refresh);
            menu.Items.Add(PreferencesMenu.Create(Program.Preferences, host.ReportFailure));
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(quit);

            var tray = new TrayIcon { Icon = appIcon, ToolTipText = "Cloudreve", Menu = menu, IsVisible = true };
            TrayIcon.SetIcons(this, new TrayIcons { tray });
            host.ActiveStatusChanged += (text, ready) =>
            {
                tray.ToolTipText = text;
                refresh.IsEnabled = ready;
            };
            refresh.IsEnabled = false;
            desktop.Exit += (_, _) =>
            {
                host.Stop();
                tray.Dispose();
            };

            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            bool stopping = false;
            bool readyToQuit = false;
            desktop.ShutdownRequested += async (_, e) =>
            {
                if (readyToQuit)
                {
                    return;
                }

                e.Cancel = true;
                if (!stopping)
                {
                    stopping = true;
                    await host.PrepareToQuitAsync();
                    readyToQuit = true;
                    Dispatcher.UIThread.Post(() => { desktop.TryShutdown(); });
                }
            };
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}