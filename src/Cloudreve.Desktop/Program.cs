using System.Diagnostics;
using System.Runtime.InteropServices;
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

    // Windows only: owns the single-instance lock and receives cloudreve:// callbacks from later launches.
    internal static WindowsInstance? Instance { get; private set; }

    [STAThread]
    private static int Main(string[] args)
    {
        if (OperatingSystem.IsWindows())
        {
            Instance = WindowsInstance.Claim(args);
            if (Instance == null)
            {
                return 0;
            }
        }
        else if (!OperatingSystem.IsMacOS())
        {
            Console.Error.WriteLine("Cloudreve requires macOS or Windows.");
            return 1;
        }

        var uptime = Stopwatch.StartNew();
        Exception? preferencesError = Preferences.Load();
        Log.Logger = DesktopLog.CreateLogger(DesktopFiles.LogDirectory, Preferences.LogLevel, Preferences.Current.LogToFile, Preferences.Current.LogMaxFiles);
        UiText.Language = UiText.Resolve(Preferences.Current.Language);
        try
        {
            Log.Information("Starting Cloudreve Client {Version} ({Architecture}, .NET {RuntimeVersion}, PID {ProcessId}, log level {LogLevel})",
                typeof(Program).Assembly.GetName().Version, RuntimeInformation.ProcessArchitecture, Environment.Version, Environment.ProcessId, Preferences.Current.LogLevel);
            if (preferencesError != null)
            {
                Log.Logger.WriteFailure(LogEventLevel.Warning, "Read desktop preferences", preferencesError);
            }
            int exitCode = AppBuilder.Configure<DesktopApp>()
                .UsePlatformDetect()
                .With(new MacOSPlatformOptions { ShowInDock = false })
                .StartWithClassicDesktopLifetime(args);
            Log.Information("Cloudreve Client stopped with exit code {ExitCode} after {UptimeMs} ms", exitCode, uptime.ElapsedMilliseconds);
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
            if (OperatingSystem.IsWindows())
            {
                Instance?.Dispose();
            }
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
            using (var icon = AssetLoader.Open(new Uri("avares://CloudreveClient/Assets/cloudreve.png")))
            {
                appIcon = new WindowIcon(icon);
            }

            var host = new WorkspaceHost(new DriveProfiles(SessionFileStore.OpenDefault()), appIcon);
            if (OperatingSystem.IsWindows())
            {
                Program.Instance?.Listen(message => Dispatcher.UIThread.Post(() =>
                {
                    if (Uri.TryCreate(message, UriKind.Absolute, out var uri) && uri.Scheme == "cloudreve")
                    {
                        host.Accept(uri);
                    }
                    else
                    {
                        host.OpenPopup(atTray: true);
                    }
                }));
            }
            else if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime lifetime)
            {
                lifetime.Activated += (_, activation) =>
                {
                    if (activation is ProtocolActivatedEventArgs protocol)
                    {
                        host.Accept(protocol.Uri);
                    }
                };
            }

            void OnTray(TrayAction action)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    switch (action)
                    {
                        case TrayAction.Show: host.OpenPopup(atTray: true); break;
                        case TrayAction.Add: host.OpenAdd(); break;
                        case TrayAction.Settings: host.OpenSettings(); break;
                        case TrayAction.Quit: desktop.TryShutdown(); break;
                    }
                });
            }

            bool en = UiText.Language == UiLanguage.English;
            DesktopTray.Start(this, OnTray, en ? "Show" : "显示", en ? "Add new drive" : "添加新云盘", DesktopText.Get("settings.title"), en ? "Quit" : "退出");
            TrayState? displayed = null;
            void RefreshTray()
            {
                var state = host.Drives.Count == 0 ? TrayState.Disconnected : host.Drives.Max(drive => drive.ReadTrayState());
                if (state != displayed)
                {
                    DesktopTray.Update(state, $"Cloudreve · {DesktopText.Get($"tray.{state}")}");
                    displayed = state;
                }
            }

            var trayTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            trayTimer.Tick += (_, _) => RefreshTray();
            RefreshTray();
            trayTimer.Start();
            desktop.Exit += (_, _) =>
            {
                trayTimer.Stop();
                DesktopTray.Stop();
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
