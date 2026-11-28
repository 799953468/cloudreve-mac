using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using Serilog;
using Serilog.Events;

namespace Cloudreve.Desktop;

// macOS delivers cloudreve:// callbacks to the running app through Launch Services. On Windows the browser starts a
// new process with the URL as its argument, so the first process owns a per-session mutex and receives the
// arguments of later launches over a pipe restricted to the current user.
[SupportedOSPlatform("windows")]
internal sealed class WindowsInstance : IDisposable
{
    private readonly Mutex mutex;
    private readonly string pipe;
    private readonly CancellationTokenSource stop = new();

    private WindowsInstance(Mutex mutex, string pipe)
    {
        this.mutex = mutex;
        this.pipe = pipe;
    }

    // Returns null when another instance already runs in this session; the arguments were handed to it.
    internal static WindowsInstance? Claim(string[] args)
    {
        string identity = $"{WindowsIdentity.GetCurrent().User?.Value}|{Process.GetCurrentProcess().SessionId}";
        string name = "CloudreveClient-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..32];
        var mutex = new Mutex(true, @"Local\" + name, out bool created);
        if (created)
        {
            return new WindowsInstance(mutex, name);
        }

        mutex.Dispose();
        Forward(name, args.FirstOrDefault() ?? "");
        return null;
    }

    private static void Forward(string pipe, string message)
    {
        try
        {
            using (var client = new NamedPipeClientStream(".", pipe, PipeDirection.Out, PipeOptions.CurrentUserOnly))
            {
                client.Connect(5000);
                using (var writer = new StreamWriter(client, new UTF8Encoding(false)))
                {
                    writer.WriteLine(message.ReplaceLineEndings(""));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            // The running instance is starting up or shutting down; the user can retry from the browser.
        }
    }

    // An empty message means "show the app"; otherwise it is the first command-line argument of the later launch.
    internal void Listen(Action<string> received)
    {
        _ = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    using (var server = new NamedPipeServerStream(pipe, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                               PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
                    {
                        await server.WaitForConnectionAsync(stop.Token);
                        using (var reader = new StreamReader(server, Encoding.UTF8))
                        {
                            string? line = await reader.ReadLineAsync(stop.Token);
                            if (line != null && line.Length <= 16 * 1024)
                            {
                                received(line);
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Logger.WriteFailure(LogEventLevel.Warning, "Receive launch request", ex);
                    await Task.Delay(TimeSpan.FromSeconds(1));
                }
            }
        });
    }

    public void Dispose()
    {
        stop.Cancel();
        try
        {
            mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not owned by this thread; the process exit releases it.
        }

        mutex.Dispose();
    }
}

[SupportedOSPlatform("windows")]
internal static class WindowsShell
{
    internal const string AppName = "CloudreveClient";

    // The installed executable, or null under a dotnet or test host that Windows should not launch.
    internal static string? Executable
    {
        get
        {
            string? path = Environment.ProcessPath;
            return path != null && Path.GetFileNameWithoutExtension(path).Equals(AppName, StringComparison.OrdinalIgnoreCase) ? path : null;
        }
    }

    // Point cloudreve:// at this executable for the current user. Called only when the user starts a sign-in,
    // so merely running the app never takes the scheme over from another Cloudreve client.
    internal static void RegisterUrlProtocol()
    {
        if (Executable is not { } exe)
        {
            return;
        }

        using (var scheme = Registry.CurrentUser.CreateSubKey(@"Software\Classes\cloudreve"))
        {
            scheme.SetValue("", "URL:Cloudreve");
            scheme.SetValue("URL Protocol", "");
            using (var icon = scheme.CreateSubKey("DefaultIcon"))
            {
                icon.SetValue("", $"\"{exe}\",0");
            }

            using (var command = scheme.CreateSubKey(@"shell\open\command"))
            {
                command.SetValue("", $"\"{exe}\" \"%1\"");
            }
        }
    }
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsLoginItem : ILoginItemService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    private static string? Command
    {
        get { return WindowsShell.Executable is { } exe ? $"\"{exe}\"" : null; }
    }

    public LoginItemStatus Status()
    {
        if (Command is not { } command)
        {
            return LoginItemStatus.Unavailable;
        }

        using (var run = Registry.CurrentUser.OpenSubKey(RunKey))
        {
            if (run?.GetValue(WindowsShell.AppName) is not string value || !value.Equals(command, StringComparison.OrdinalIgnoreCase))
            {
                return LoginItemStatus.NotRegistered;
            }
        }

        // Task Manager and Settings > Apps > Startup record a disabled entry with an odd first byte.
        using (var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey))
        {
            return approved?.GetValue(WindowsShell.AppName) is byte[] { Length: > 0 } state && (state[0] & 1) != 0
                ? LoginItemStatus.RequiresApproval
                : LoginItemStatus.Enabled;
        }
    }

    public string? SetRegistered(bool registered)
    {
        try
        {
            using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (registered)
                {
                    run.SetValue(WindowsShell.AppName, Command ?? throw new InvalidOperationException());
                    return null;
                }

                run.DeleteValue(WindowsShell.AppName, false);
            }

            using (var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, true))
            {
                approved?.DeleteValue(WindowsShell.AppName, false);
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return ex.Message;
        }
    }

    public void OpenSettings()
    {
        using (Process.Start(new ProcessStartInfo("ms-settings:startupapps") { UseShellExecute = true }))
        {
        }
    }
}
