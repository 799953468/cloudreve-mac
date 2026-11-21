using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Themes.Fluent;
using Serilog;
using Serilog.Events;

namespace Cloudreve.Desktop;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsMacOS())
        {
            Console.Error.WriteLine("Cloudreve Mac requires macOS.");
            return 1;
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Log.Logger = DesktopLog.CreateLogger(Path.Combine(home, "Library", "Logs", "Cloudreve"));
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
            var window = new WorkspaceWindow();
            if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime lifetime)
            {
                lifetime.Activated += async (_, activation) =>
                {
                    if (activation is ProtocolActivatedEventArgs protocol)
                    {
                        window.Show();
                        window.Activate();
                        await window.AcceptCallbackAsync(protocol.Uri);
                    }
                };
            }

            var menu = new NativeMenu();
            var show = new NativeMenuItem("打开工作区");
            var refresh = new NativeMenuItem("刷新目录");
            var quit = new NativeMenuItem("退出 Cloudreve");
            show.Click += (_, _) =>
            {
                window.Show();
                window.Activate();
            };
            refresh.Click += async (_, _) => { await window.RefreshAsync(); };
            quit.Click += (_, _) => { desktop.TryShutdown(); };
            menu.Items.Add(show);
            menu.Items.Add(refresh);
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(quit);

            using (var icon = AssetLoader.Open(new Uri("avares://CloudreveMac/Assets/cloudreve.png")))
            {
                var appIcon = new WindowIcon(icon);
                window.Icon = appIcon;
                var tray = new TrayIcon { Icon = appIcon, ToolTipText = "Cloudreve", Menu = menu, IsVisible = true };
                TrayIcon.SetIcons(this, new TrayIcons { tray });
                window.StatusChanged += (text, ready) =>
                {
                    tray.ToolTipText = text;
                    refresh.IsEnabled = ready;
                };
                refresh.IsEnabled = false;
                desktop.Exit += (_, _) =>
                {
                    window.Stop();
                    tray.Dispose();
                };
            }

            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            window.Closing += (_, e) =>
            {
                if (e.CloseReason == WindowCloseReason.WindowClosing)
                {
                    e.Cancel = true;
                    window.Hide();
                }
            };
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}