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

        Preferences.Load();
        Log.Logger = DesktopLog.CreateLogger(DesktopFiles.LogDirectory, Preferences.LogLevel, Preferences.Current.LogToFile, Preferences.Current.LogMaxFiles);
        UiText.Language = UiText.Resolve(Preferences.Current.Language);
        try
        {
            Log.Information("Starting Cloudreve Desktop");
            int exitCode = AppBuilder.Configure<DesktopApp>()
                .UsePlatformDetect()
                .With(new MacOSPlatformOptions { ShowInDock = false })
                .StartWithClassicDesktopLifetime(args);
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
            if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime lifetime)
            {
                lifetime.Activated += (_, activation) =>
                {
                    if (activation is ProtocolActivatedEventArgs protocol)
                    {
                        host.Accept(protocol.Uri);
                    }
                };
            }

            MacTray.Command trayAction = action => Dispatcher.UIThread.Post(() =>
            {
                switch (action)
                {
                    case 0: host.OpenPopup(atTray: true); break;
                    case 1: host.OpenAdd(); break;
                    case 2: host.OpenSettings(); break;
                    case 3: desktop.TryShutdown(); break;
                }
            });
            bool en = UiText.Language == UiLanguage.English;
            MacTray.Start(trayAction, en ? "Show" : "显示", en ? "Add new drive" : "添加新云盘", DesktopText.Get("settings.title"), en ? "Quit" : "退出");
            desktop.Exit += (_, _) =>
            {
                MacTray.Stop();
                GC.KeepAlive(trayAction);
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
                    await host.StopAsync();
                    readyToQuit = true;
                    Dispatcher.UIThread.Post(() => { desktop.TryShutdown(); });
                }
            };
            Dispatcher.UIThread.Post(host.Start);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
