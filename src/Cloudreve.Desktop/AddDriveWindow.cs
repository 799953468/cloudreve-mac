using System.Web;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Serilog;
using Serilog.Events;

namespace Cloudreve.Desktop;

internal sealed partial class AddDriveWindow : Window
{
    private enum Step
    {
        Address,
        Waiting,
        Setup,
        Installing,
        Success
    }

    private readonly WorkspaceHost host;
    private readonly HttpClient api;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Func<Uri, Task<bool>> openBrowser;
    private DesktopLogin? login;
    private Uri? authorizeUrl;
    private Uri? callback;
    private DriveSession? verified;
    private Step step;
    private bool busy;
    private bool invalidFolder;
    internal MountedDrive? Existing { get; }

    private bool Reauthorizing { get; }

    internal AddDriveWindow(WorkspaceHost host, HttpClient api, MountedDrive? existing = null, Func<Uri, Task<bool>>? openBrowser = null)
    {
        this.host = host;
        this.api = api;
        Existing = existing;
        Reauthorizing = existing?.Pair != null;
        InitializeComponent();
        Endpoint.PlaceholderText = DesktopText.Get("addDrive.siteUrl") + " *";
        this.openBrowser = openBrowser ?? (uri => Launcher.LaunchUriAsync(uri));
        if (existing != null)
        {
            Endpoint.Text = existing.Account.Site;
            MountName.Text = existing.Account.Name;
            if (existing.Pair == null && !existing.Expired)
            {
                verified = existing.Account;
                step = Step.Setup;
            }
            else
            {
                Opened += async (_, _) => { await AdvanceAsync(); };
            }
        }

        Endpoint.TextChanged += (_, _) => { Render(); };
        MountName.TextChanged += (_, _) => { Render(); };
        LocalPath.TextChanged += (_, _) =>
        {
            invalidFolder = false;
            ErrorPanel.IsVisible = false;
            Render();
        };
        LocalPath.LostFocus += (_, _) => { ValidateFolder(); };
        Browse.Click += async (_, _) =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = DesktopText.Get("addDrive.selectFolder") });
            using (var folder = folders.FirstOrDefault())
            {
                if (folder != null)
                {
                    LocalPath.Text = folder.TryGetLocalPath();
                    ValidateFolder();
                }
            }
        };
        Next.Click += async (_, _) => { await AdvanceAsync(); };
        Back.Click += (_, _) =>
        {
            if (step == Step.Success)
            {
                Close();
                return;
            }

            login = null;
            callback = null;
            verified = null;
            authorizeUrl = null;
            LocalPath.Text = "";
            ErrorPanel.IsVisible = false;
            step = Step.Address;
            Render();
        };
        Closed += (_, _) => { lifetime.Cancel(); };
        Render();
    }

    private void ValidateFolder()
    {
        if (string.IsNullOrWhiteSpace(LocalPath.Text))
        {
            return;
        }

        try
        {
            WorkspaceHost.CheckLocalFolder(LocalPath.Text);
            invalidFolder = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Logger.WriteFailure(LogEventLevel.Debug, "Validate local sync folder", ex);
            invalidFolder = true;
            ShowError(ex.Message);
        }

        Render();
    }

    internal bool Accept(Uri uri)
    {
        if (step != Step.Waiting || login?.MatchesCallback(uri) != true)
        {
            return false;
        }

        callback = uri;
        MountName.Text = Existing?.Account.Name ?? HttpUtility.ParseQueryString(uri.Query)["name"] ?? authorizeUrl!.Host;
        step = Step.Setup;
        Render();
        return true;
    }

    internal async Task AdvanceAsync()
    {
        if (busy || lifetime.IsCancellationRequested)
        {
            return;
        }

        busy = true;
        ErrorPanel.IsVisible = false;
        Render();
        try
        {
            switch (step)
            {
                case Step.Address:
                    if (string.IsNullOrWhiteSpace(Endpoint.Text))
                    {
                        Endpoint.Focus();
                        return;
                    }
                    login = new DesktopLogin(api, Existing?.Account.Site ?? Endpoint.Text ?? "");
                    authorizeUrl = await login.BeginAsync(lifetime.Token);
                    step = Step.Waiting;
                    goto case Step.Waiting;
                case Step.Waiting:
                    if (!await openBrowser(authorizeUrl!))
                    {
                        throw new IOException(UiText.Get("无法打开系统浏览器。"));
                    }

                    break;
                case Step.Setup:
                    if (!Reauthorizing)
                    {
                        WorkspaceHost.CheckLocalFolder(LocalPath.Text ?? "");
                    }

                    step = Step.Installing;
                    Render();
                    if (verified == null)
                    {
                        var candidate = await login!.CompleteAsync(callback!, lifetime.Token);
                        var client = host.VerifyClient(candidate, updated => { candidate = updated; });
                        var user = await client.UserIdAsync(lifetime.Token);
                        verified = DesktopLogin.VerifyBinding(candidate, user, Existing?.Account);
                    }

                    await host.InstallAsync(verified with { Name = MountName.Text!.Trim() }, LocalPath.Text, Existing, lifetime.Token);
                    step = Step.Success;
                    break;
                case Step.Success:
                    if (!Reauthorizing && !string.IsNullOrEmpty(LocalPath.Text))
                    {
                        await DesktopFiles.OpenFolderAsync(this, LocalPath.Text);
                    }

                    Close();
                    break;
            }
        }
        catch (Exception ex) when (!lifetime.IsCancellationRequested)
        {
            Log.Logger.ForContext("Stage", step).ForContext("Reauthorizing", Reauthorizing)
                .WriteFailure(LogEventLevel.Warning, "Configure drive", ex);
            if (step == Step.Installing)
            {
                step = verified == null ? Step.Address : Step.Setup;
            }

            ShowError(ex.Message);
        }
        catch (Exception) when (lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            busy = false;
            Render();
        }
    }

    private void ShowError(string text)
    {
        ErrorText.Text = text;
        ErrorPanel.IsVisible = true;
    }

    private void Render()
    {
        Heading.Text = DesktopText.Get("addDrive." + (step switch
        {
            Step.Address => "title", Step.Waiting => "waitingTitle", Step.Setup => Reauthorizing ? "reauthorizeTitle" : "finalSetupTitle",
            Step.Installing => "settingUp", _ => Reauthorizing ? "reauthorizeSuccessTitle" : "successTitle"
        }));
        Hint.IsVisible = step is Step.Address or Step.Waiting or Step.Setup;
        Hint.Text = DesktopText.Get("addDrive." +
                                    (step switch { Step.Waiting => "waitingDescription", Step.Setup => Reauthorizing ? "reauthorizeDescription" : "finalSetupDescription", _ => "description" }));
        Logo.IsVisible = step is not (Step.Installing or Step.Success);
        Form.IsVisible = step != Step.Installing;
        Endpoint.IsVisible = step == Step.Address;
        Endpoint.IsReadOnly = busy || Existing != null;
        MountFields.IsVisible = step == Step.Setup && !Reauthorizing;
        Next.Content = DesktopText
            .Get("addDrive." + (step switch
            {
                Step.Address => "connect", Step.Waiting => "reopenBrowser", Step.Setup => Reauthorizing ? "reauthorizeConfirm" : "finish", _ => Reauthorizing ? "close" : "openDrive"
            })).Replace("{{name}}", MountName.Text ?? "");
        Next.IsEnabled = !busy &&
                         (step != Step.Setup || Reauthorizing || !invalidFolder && !string.IsNullOrWhiteSpace(MountName.Text) && !string.IsNullOrWhiteSpace(LocalPath.Text));
        Back.IsVisible = step is Step.Setup or Step.Waiting || step == Step.Success && !Reauthorizing;
        Back.IsEnabled = !busy;
        Back.Content = DesktopText.Get(step == Step.Success ? "addDrive.close" : "addDrive.back");
    }
}
