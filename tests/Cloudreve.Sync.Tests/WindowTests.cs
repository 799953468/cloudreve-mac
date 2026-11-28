using System.Net;
using System.Net.Http.Json;
using System.Web;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
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
                settings.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
                settings.UpdateLayout();
                var selects = settings.GeneralPage.GetVisualDescendants().OfType<ComboBox>().ToArray();
                Assert.Equal(3, selects.Length);
                var language = selects[0];
                var initialLanguage = language.SelectedItem;
                Assert.True(language.Focus(NavigationMethod.Tab));
                settings.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
                settings.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
                Assert.True(language.IsDropDownOpen);
                var option = Assert.IsType<ComboBoxItem>(language.ContainerFromIndex(language.SelectedIndex));
                Assert.True(option.IsSelected);
                var menu = TopLevel.GetTopLevel(option)!;
                Capture(settings, dark ? "general-dark" : "general");
                Capture(menu, dark ? "language-menu-dark" : "language-menu");
                menu.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                menu.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                Assert.False(language.IsDropDownOpen);
                Assert.Equal(initialLanguage, language.SelectedItem);
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
    public async Task BrowserCallbackRequiresExplicitFinishAndAcceptsPopulatedFolder()
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
                wizard.LocalPath.Text = path;
                await wizard.AdvanceAsync();
                Assert.True(wizard.ErrorPanel.IsVisible);
                Assert.Null(sessions.Load());
                Assert.Equal(0, transport.Exchanges);
                wizard.LocalPath.Text = folder.FullName;
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
                await host.RemoveAsync(drive);
                Assert.Equal("original", File.ReadAllText(path));
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoredMountsRunWithoutWindowsAndFilterRealActivity(bool dark)
    {
        await using var ui = HeadlessUnitTestSession.StartNew(typeof(TestApplication));
        await ui.Dispatch(async () =>
        {
            var profiles = new DriveProfiles(sessions);
            foreach (var (store, name) in new[] { (sessions, "Work"), (profiles.Create(), "Personal") })
            {
                var account = new DriveSession("https://site.example/", name, name, "cloudreve://my", "u", "access", "refresh", DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
                store.Save(account);
                var pairStore = new FolderPairStore(Path.Combine(store.DirectoryPath, "folder-pairs"), account);
                var pair = pairStore.Create(temp.CreateSubdirectory(name).FullName, account.Root, "id");
                if (name == "Personal")
                {
                    string cache = pairStore.TransferDirectory(pair);
                    var remote = new DriveEntry("Budget.xlsx", account.Root + "/Finance/2026/Budget.xlsx", false, 100, "v1");
                    TransferQueue.Open(cache).Reconcile([
                        new FileTransfer("Finance/2026/Budget.xlsx", SyncAction.Download, null, remote, null),
                        new FileTransfer("Notes.txt", SyncAction.Upload, null, null, null),
                        new FileTransfer("Archive/Reports/Quarterly review and supporting documentation.pdf", SyncAction.CompareContents, null, null, null)
                    ]);
                    var activity = new SyncActivity(cache);
                    activity.Start("下载", "second.txt");
                    activity.Finish("完成");
                    await activity.FlushHistoryAsync();
                    transport.HoldListingFor = account.DriveId;
                }
            }

            var host = new WorkspaceHost(profiles, transport: transport);
            try
            {
                host.Start();
                Assert.Null(host.Wizard);
                Assert.Null(host.Popup);
                Assert.Null(host.Settings);
                await transport.RootScanned.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await transport.ListingHeld.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal("rotated", sessions.Load()!.AccessToken);
                var work = host.Drives[0];
                work.Activity.Start("上传", "first.txt");
                work.Activity.BeginTransfer("上传", 400);
                work.Activity.ReportBytes(160);
                host.OpenPopup();
                var popup = host.Popup!;
                var theme = dark ? ThemeVariant.Dark : ThemeVariant.Light;
                popup.RequestedThemeVariant = theme;
                popup.Refresh(true);
                Assert.Equal("正在同步 1 个文件...", popup.Footer.Text);
                Assert.False(popup.Empty.IsVisible);
                Assert.Contains("first.txt", Labels(popup.Tasks));
                Assert.Contains("second.txt", Labels(popup.Tasks));
                popup.UpdateLayout();
                var waitingLabels = Labels(popup.WaitingItems).ToArray();
                Assert.Equal(new[] { "Budget.xlsx", "Personal · Finance/2026", "下载", "Notes.txt", "Personal", "上传",
                    "Quarterly review and supporting documentation.pdf", "Personal · Archive/Reports", "核对内容" }, waitingLabels);
                Assert.Equal("等待传输（3）", popup.WaitingTitle.Text);
                Assert.False(popup.WaitingMore.IsVisible);
                var rows = popup.WaitingItems.ItemsSource;
                var activeRow = Assert.Single(popup.ActiveRows.Children);
                var recentRow = Assert.Single(popup.RecentRows.Children);
                work.Activity.ReportBytes(200);
                popup.Refresh();
                Assert.Same(rows, popup.WaitingItems.ItemsSource);
                Assert.Same(activeRow, Assert.Single(popup.ActiveRows.Children));
                Assert.Same(recentRow, Assert.Single(popup.RecentRows.Children));
                Assert.Equal(200, Assert.Single(activeRow.GetVisualDescendants().OfType<ProgressBar>()).Value);
                Capture(popup, dark ? "activity-dark" : "activity");
                Click(Assert.IsType<Button>(popup.Chips.Children[1]));
                Assert.False(popup.WaitingSection.IsVisible);
                Assert.DoesNotContain(popup.ScanItems.Items.OfType<ScanRow>(), row => row.Drive == "Personal");
                Click(Assert.IsType<Button>(popup.Chips.Children[2]));
                popup.UpdateLayout();
                Assert.DoesNotContain("first.txt", Labels(popup.Tasks));
                Assert.Contains("second.txt", Labels(popup.Tasks));
                Assert.Equal(waitingLabels, Labels(popup.WaitingItems));
                Assert.True(popup.ScanSection.IsVisible);
                Assert.Contains("正在全量扫描云端文件夹", Labels(popup.ScanItems));
                Assert.Equal("Personal", Assert.Single(popup.ScanItems.Items.OfType<ScanRow>()).Drive);
                Assert.Equal("正在扫描…", popup.Footer.Text);
                var backlog = Enumerable.Range(0, SyncActivity.Capacity + 2).Select(i =>
                    new QueuedFile(Guid.NewGuid(), new FileTransfer($"{i}.txt", SyncAction.Upload, null, null, null))).ToArray();
                host.Drives[1].Activity.SetPending(backlog);
                popup.Refresh();
                Assert.Equal(SyncActivity.Capacity, popup.WaitingItems.ItemCount);
                Assert.Equal("等待传输（102）", popup.WaitingTitle.Text);
                Assert.True(popup.WaitingMore.IsVisible);
                Assert.Equal("另有 2 项等待传输", popup.WaitingMore.Text);
                host.OpenSettings();
                Assert.Equal(2, host.Settings!.Cards.Children.Count);
                host.Settings.RequestedThemeVariant = theme;
                Capture(host.Settings, dark ? "drives-dark" : "drives");
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

    [Fact]
    public async Task ScanWithoutTransfersReplacesEmptyStateUntilIdle()
    {
        await using var ui = HeadlessUnitTestSession.StartNew(typeof(TestApplication));
        await ui.Dispatch(async () =>
        {
            sessions.Save(new DriveSession("https://site.example/", "work", "Work", "cloudreve://my", "u", "access", "refresh",
                DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow.AddDays(1)));
            var host = new WorkspaceHost(new DriveProfiles(sessions), transport: transport);
            try
            {
                host.Start();
                var activity = Assert.Single(host.Drives).Activity;
                activity.SetScan(ScanPhase.Local, 42);
                host.OpenPopup();
                var popup = host.Popup!;
                popup.UpdateLayout();
                Assert.False(popup.Empty.IsVisible);
                Assert.True(popup.ScanProgress.IsVisible);
                Assert.Equal("正在扫描…", popup.Footer.Text);
                Assert.Contains("正在全量扫描本地文件夹", Labels(popup.ScanItems));
                activity.SetScan(ScanPhase.Local, 42, isScoped: true);
                popup.Refresh();
                popup.UpdateLayout();
                Assert.Contains("正在检查本地变更路径", Labels(popup.ScanItems));
                Assert.Contains("已发现 42 项", Labels(popup.ScanItems));

                activity.SetScan(ScanPhase.Failed);
                popup.Refresh();
                popup.UpdateLayout();
                Assert.False(popup.Empty.IsVisible);
                Assert.False(popup.ScanProgress.IsVisible);
                Assert.Equal("扫描失败", popup.Footer.Text);
                Assert.Contains("扫描失败，将自动重试", Labels(popup.ScanItems));

                activity.SetScan(ScanPhase.Idle);
                popup.Refresh();
                Assert.False(popup.ScanSection.IsVisible);
                Assert.True(popup.Empty.IsVisible);
                Assert.Equal("文件已是最新。", popup.Footer.Text);
            }
            finally
            {
                host.Popup?.Close();
                await host.StopAsync();
            }

            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IgnoreRulesDialogKeepsDraftUntilSaved(bool dark)
    {
        await using var ui = HeadlessUnitTestSession.StartNew(typeof(TestApplication));
        await ui.Dispatch(async () =>
        {
            string saved = "*.log";
            void SaveRules(string text)
            {
                _ = new PairIgnoreRules(text);
                saved = text;
            }

            var owner = new Window();
            owner.Show();
            try
            {
                var dialog = new IgnoreRulesWindow("Work", saved, SaveRules)
                {
                    RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light
                };
                var closed = dialog.ShowDialog(owner);
                Assert.Equal(saved, dialog.RulesEditor.Text);
                Assert.True(dialog.RulesEditor.IsFocused);
                dialog.RulesEditor.CaretIndex = saved.Length;
                dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                dialog.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                Assert.Equal("*.log\n", dialog.RulesEditor.Text);
                Assert.False(closed.IsCompleted);
                dialog.RulesEditor.Text = "*.log\nnode_modules/\n*.tmp";
                Capture(dialog, dark ? "ignore-rules-dark" : "ignore-rules");

                dialog.Width = dialog.MinWidth;
                dialog.Height = dialog.MinHeight;
                string invalid = string.Join('\n', Enumerable.Repeat("*.tmp", 80)) + "\n[abc]";
                dialog.RulesEditor.Text = invalid;
                Click(dialog.SaveButton);
                Assert.True(dialog.SaveError.IsVisible);
                Assert.False(closed.IsCompleted);
                Assert.Equal(invalid, dialog.RulesEditor.Text);
                Assert.Equal("*.log", saved);
                dialog.UpdateLayout();
                var errorBottom = dialog.SaveError.TranslatePoint(new Point(0, dialog.SaveError.Bounds.Height), dialog)!.Value.Y;
                var saveTop = dialog.SaveButton.TranslatePoint(default, dialog)!.Value.Y;
                Assert.True(errorBottom <= saveTop && saveTop + dialog.SaveButton.Bounds.Height <= dialog.ClientSize.Height);

                dialog.RulesEditor.Text = "*.tmp\n!important.tmp";
                Click(dialog.SaveButton);
                await closed;
                Assert.Equal("*.tmp\n!important.tmp", saved);

                var reopened = new IgnoreRulesWindow("Work", saved, SaveRules);
                var cancelled = reopened.ShowDialog(owner);
                reopened.RulesEditor.Text = string.Empty;
                reopened.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                Assert.True(cancelled.IsCompleted);
                Assert.Equal("*.tmp\n!important.tmp", saved);
            }
            finally
            {
                owner.Close();
            }
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StartupSwitchTracksTheSystemLoginItem()
    {
        await using var ui = HeadlessUnitTestSession.StartNew(typeof(TestApplication));
        await ui.Dispatch(async () =>
        {
            var service = new LoginItemStub { Current = LoginItemStatus.NotFound };
            var host = new WorkspaceHost(new DriveProfiles(sessions), transport: transport);
            var settings = new SettingsWindow(host, service);
            settings.Show();
            try
            {
                settings.Navigation.SelectedIndex = 1;
                var toggle = settings.GeneralPage.GetVisualDescendants().OfType<ToggleSwitch>().First();
                Assert.True(toggle.IsEnabled);
                Assert.False(toggle.IsChecked);
                Assert.Empty(service.Writes);

                toggle.IsChecked = true;
                Assert.True(toggle.IsChecked);
                Assert.Equal(LoginItemStatus.Enabled, service.Current);
                toggle.IsChecked = false;
                Assert.False(toggle.IsChecked);
                Assert.Equal(LoginItemStatus.NotRegistered, service.Current);
                Assert.Equal([true, false], service.Writes);

                service.OnRegister = LoginItemStatus.RequiresApproval;
                toggle.IsChecked = true;
                Assert.False(toggle.IsChecked);
                Assert.Equal(1, service.SettingsOpened);
                Assert.Contains(UiText.Get("settings.autoStartApproval"), Labels(settings.GeneralPage));
                toggle.IsChecked = true;
                Assert.Equal(2, service.SettingsOpened);
                Assert.Equal([true, false, true], service.Writes);

                settings.Hide();
                service.Current = LoginItemStatus.Enabled;
                settings.Show();
                settings.Activate();
                Assert.True(toggle.IsChecked);
                Assert.Equal([true, false, true], service.Writes);

                service.Error = "Login item change denied";
                toggle.IsChecked = false;
                Assert.True(toggle.IsChecked);
                Assert.Contains(service.Error, Labels(settings.GeneralPage));
                Assert.Equal([true, false, true, false], service.Writes);

                service.Error = null;
                service.Current = LoginItemStatus.RequiresApproval;
                toggle.IsChecked = false;
                Assert.Equal(LoginItemStatus.NotRegistered, service.Current);
                Assert.False(toggle.IsChecked);
                Assert.Equal(2, service.SettingsOpened);

                service.Current = LoginItemStatus.Unavailable;
                settings.Navigation.SelectedIndex = 0;
                settings.Navigation.SelectedIndex = 1;
                var unavailable = settings.GeneralPage.GetVisualDescendants().OfType<ToggleSwitch>().First();
                Assert.False(unavailable.IsEnabled);
                Assert.False(unavailable.IsChecked);
            }
            finally
            {
                settings.Close();
                await host.StopAsync();
            }
        }, TestContext.Current.CancellationToken);
    }

    private sealed class LoginItemStub : ILoginItemService
    {
        internal LoginItemStatus Current { get; set; }
        internal LoginItemStatus OnRegister { get; set; } = LoginItemStatus.Enabled;
        internal string? Error { get; set; }
        internal List<bool> Writes { get; } = [];
        internal int SettingsOpened { get; private set; }

        public LoginItemStatus Status()
        {
            return Current;
        }

        public string? SetRegistered(bool registered)
        {
            Writes.Add(registered);
            if (Error == null)
            {
                Current = registered ? OnRegister : LoginItemStatus.NotRegistered;
            }

            return Error;
        }

        public void OpenSettings()
        {
            SettingsOpened++;
        }
    }

    private static void Capture(TopLevel window, string name)
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
        internal string? HoldListingFor { get; set; }
        internal TaskCompletionSource ListingHeld { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
                    if (HoldListingFor != null && request.Headers.GetValues("X-Cr-Client-Id").Contains(HoldListingFor))
                    {
                        ListingHeld.TrySetResult();
                        await Task.Delay(Timeout.Infinite, cancellationToken);
                    }

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
