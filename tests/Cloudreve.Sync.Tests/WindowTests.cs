using System.Net;
using System.Net.Http.Json;
using System.Web;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.VisualTree;
using Cloudreve.Desktop;
using Xunit;

namespace Cloudreve.Sync.Tests;

public sealed class TestApplication : Application
{
    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<TestApplication>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    }

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
    }
}

[Collection("Desktop language")]
public sealed class WindowTests : IDisposable
{
    private readonly DirectoryInfo temp = Directory.CreateTempSubdirectory("cloudreve-desktop-");
    private readonly SessionFileStore sessions;
    private readonly Transport transport = new();

    public WindowTests()
    {
        sessions = new SessionFileStore(Path.Combine(temp.FullName, "settings"));
    }

    public void Dispose()
    {
        temp.Delete(true);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ShellUsesOnlyTheWindowsEntryPoints(bool dark)
    {
        await using var ui = HeadlessUnitTestSession.StartNew(typeof(TestApplication));
        await ui.Dispatch(async () =>
        {
            var host = new WorkspaceHost(new DriveProfiles(sessions), transport: transport);
            try
            {
                host.Start();
                var wizard = Assert.IsType<AddDriveWindow>(host.Wizard);
                Assert.Equal(new Size(470, 630), wizard.ClientSize);
                Assert.Equal("添加网盘", wizard.Heading.Text);
                Assert.True(wizard.Next.IsEnabled);
                await wizard.AdvanceAsync();
                Assert.Equal("添加网盘", wizard.Heading.Text);
                host.OpenAdd();
                Assert.Same(wizard, host.Wizard);
                wizard.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
                wizard.UpdateLayout();
                Assert.Equal(56, wizard.Endpoint.Bounds.Height);
                Assert.Equal(42, wizard.Next.Bounds.Height);
                Capture(wizard, dark ? "add-dark" : "add");
                wizard.Close();
                host.OpenPopup();
                var popup = host.Popup!;
                Assert.Equal(new Size(370, 530), popup.ClientSize);
                Assert.True(popup.Empty.IsVisible);
                Assert.Equal(2, popup.Chips.Children.Count);
                Assert.Equal("文件已是最新。", popup.Footer.Text);
                Click(popup.SettingsButton);
                var settings = host.Settings!;
                Assert.Equal(new Size(700, 500), settings.ClientSize);
                Assert.Equal(3, settings.Navigation.ItemCount);
                Assert.True(settings.DrivePage.IsVisible);
                settings.Navigation.SelectedIndex = 1;
                Assert.True(settings.GeneralPage.IsVisible);
                Assert.Equal(5, settings.GeneralPage.GetVisualDescendants().OfType<ToggleSwitch>().Count());
                settings.Navigation.SelectedIndex = 2;
                Assert.True(settings.AboutPage.IsVisible);
                settings.Navigation.SelectedIndex = 0;
                Click(settings.AddButton);
                Assert.NotNull(host.Wizard);
                Assert.Empty(host.Drives);
            }
            finally
            {
                host.Settings?.Close();
                host.Popup?.Close();
                await host.StopAsync();
            }

            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task BrowserCallbackRequiresExplicitFinishAndEmptyFolder()
    {
        await using var ui = HeadlessUnitTestSession.StartNew(typeof(TestApplication));
        await ui.Dispatch(async () =>
        {
            var host = new WorkspaceHost(new DriveProfiles(sessions), transport: transport);
            using var api = new HttpClient(transport, false);
            var urls = new List<Uri>();
            var wizard = new AddDriveWindow(host, api, openBrowser: uri =>
            {
                urls.Add(uri);
                return Task.FromResult(true);
            });
            try
            {
                wizard.Show();
                wizard.Endpoint.Text = "https://site.example";
                await wizard.AdvanceAsync();
                Assert.Equal("登录以继续", wizard.Heading.Text);
                await wizard.AdvanceAsync();
                Assert.Equal(urls[0], urls[1]);
                string oldState = HttpUtility.ParseQueryString(urls[0].Query)["state"]!;
                Click(wizard.Back);
                Assert.False(wizard.Accept(Callback(oldState)));
                await wizard.AdvanceAsync();
                var state = HttpUtility.ParseQueryString(urls[^1].Query)["state"]!;
                Assert.False(wizard.Accept(Callback(oldState)));
                Assert.True(wizard.Accept(Callback(state)));
                Assert.Equal("最终设置", wizard.Heading.Text);
                Assert.True(wizard.MountFields.IsVisible);
                Assert.Equal("云盘", wizard.MountName.Text);
                Assert.Null(sessions.Load());
                Assert.Equal(0, transport.Exchanges);
                var folder = temp.CreateSubdirectory("mount");
                string path = Path.Combine(folder.FullName, "existing.txt");
                File.WriteAllText(path, "original");
                wizard.LocalPath.Text = folder.FullName;
                await wizard.AdvanceAsync();
                Assert.True(wizard.ErrorPanel.IsVisible);
                Assert.Null(sessions.Load());
                File.Delete(path);
                wizard.MountName.Text = "Work";
                await wizard.AdvanceAsync();
                Assert.Equal("设置完成！", wizard.Heading.Text);
                Assert.Equal("打开 Work", wizard.Next.Content);
                Assert.Equal(1, transport.Exchanges);
                Assert.Equal("Work", sessions.Load()!.Name);
                var drive = Assert.Single(host.Drives);
                Assert.NotNull(drive.Pair);
                await transport.RootScanned.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(wizard.Accept(Callback(state)));
                wizard.Close();
                File.WriteAllText(path, "keep");
                await host.RemoveAsync(drive);
                Assert.Equal("keep", File.ReadAllText(path));
                Assert.Null(sessions.Load());
            }
            finally
            {
                if (wizard.IsVisible)
                {
                    wizard.Close();
                }

                await host.StopAsync();
            }

            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RestoredMountsRunWithoutWindowsAndFilterRealActivity()
    {
        await using var ui = HeadlessUnitTestSession.StartNew(typeof(TestApplication));
        await ui.Dispatch(async () =>
        {
            var profiles = new DriveProfiles(sessions);
            foreach (var (store, name) in new[] { (sessions, "Work"), (profiles.Create(), "Personal") })
            {
                var account = new DriveSession("https://site.example/", name, name, "cloudreve://my", "u", "access", "refresh", DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
                store.Save(account);
                new FolderPairStore(Path.Combine(store.DirectoryPath, "folder-pairs"), account).Create(temp.CreateSubdirectory(name).FullName, account.Root, "id");
            }

            var host = new WorkspaceHost(profiles, transport: transport);
            try
            {
                host.Start();
                Assert.Null(host.Wizard);
                Assert.Null(host.Popup);
                Assert.Null(host.Settings);
                await transport.RootScanned.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal("rotated", sessions.Load()!.AccessToken);
                var work = host.Drives[0];
                var personal = host.Drives[1];
                work.Activity.Start("上传", "first.txt");
                work.Activity.BeginTransfer("上传", 400);
                work.Activity.ReportBytes(160);
                personal.Activity.Start("下载", "second.txt");
                personal.Activity.Finish("完成");
                host.OpenPopup();
                var popup = host.Popup!;
                popup.Refresh(true);
                Assert.Equal("正在同步 1 个文件...", popup.Footer.Text);
                Assert.False(popup.Empty.IsVisible);
                Assert.Contains("first.txt", Labels(popup.Tasks));
                Assert.Contains("second.txt", Labels(popup.Tasks));
                Capture(popup, "activity");
                Click(Assert.IsType<Button>(popup.Chips.Children[2]));
                Assert.DoesNotContain("first.txt", Labels(popup.Tasks));
                Assert.Contains("second.txt", Labels(popup.Tasks));
                host.OpenSettings();
                Assert.Equal(2, host.Settings!.Cards.Children.Count);
                Capture(host.Settings, "drives");
                host.Settings.Close();
                popup.Close();
                Assert.Equal(2, host.Drives.Count);
            }
            finally
            {
                host.Settings?.Close();
                host.Popup?.Close();
                await host.StopAsync();
            }

            return true;
        }, TestContext.Current.CancellationToken);
    }

    private static void Capture(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("CLOUDREVE_UI_CAPTURES") is not { Length: > 0 } dir)
        {
            return;
        }

        window.UpdateLayout();
        using var bitmap = window.CaptureRenderedFrame();
        Assert.NotNull(bitmap);
        bitmap.Save(Path.Combine(dir, name + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }

    private static Uri Callback(string state)
    {
        return new Uri($"cloudreve://mount?state={state}&code=code&name=%E4%BA%91%E7%9B%98&path=cloudreve%3A%2F%2Fmy&user_id=u");
    }

    private static IEnumerable<string?> Labels(Control root)
    {
        return root.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text);
    }

    private static void Click(Button button)
    {
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private sealed class Transport : HttpMessageHandler
    {
        internal int Exchanges { get; private set; }
        internal TaskCompletionSource RootScanned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            object body;
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/api/v4/site/ping": body = new { code = 0, data = "4.12.0" }; break;
                case "/api/v4/session/oauth/token":
                    Exchanges++;
                    body = new { access_token = "access", refresh_token = "refresh", expires_in = 3600, refresh_token_expires_in = 7200 };
                    break;
                case "/api/v4/session/token/refresh":
                    body = new
                    {
                        code = 0,
                        data = new { access_token = "rotated", refresh_token = "refresh", access_expires = DateTimeOffset.UtcNow.AddMinutes(30), refresh_expires = DateTimeOffset.UtcNow.AddDays(1) }
                    }; break;
                case "/api/v4/user/me": body = new { code = 0, data = new { id = "u" } }; break;
                case "/api/v4/user/capacity": body = new { code = 0, data = new { used = 100, total = 1000 } }; break;
                case "/api/v4/file/info": body = new { code = 0, data = new { id = "id", type = 1, path = "cloudreve://my" } }; break;
                case "/api/v4/file":
                    RootScanned.TrySetResult();
                    body = new { code = 0, data = new { files = Array.Empty<object>(), pagination = new { } } };
                    break;
                case "/api/v4/file/events":
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                    throw new InvalidOperationException();
                default: throw new InvalidOperationException(request.RequestUri.AbsolutePath);
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
        }
    }
}