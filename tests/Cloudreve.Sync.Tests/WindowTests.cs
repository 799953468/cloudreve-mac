using System.Net;
using System.Net.Http.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
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

public sealed class WindowTests
{
    [Fact]
    public async Task ConnectionFormCentersTextAtWindowsControlHeights()
    {
        await using (var ui = HeadlessUnitTestSession.StartNew(typeof(TestApplication)))
        {
            await ui.Dispatch(CheckConnectionForm, TestContext.Current.CancellationToken);
        }
    }

    private static void CheckConnectionForm()
    {
        var window = new WorkspaceWindow(() => null);
        window.Show();
        try
        {
            window.UpdateLayout();
            Assert.Equal(630, window.ClientSize.Height);
            Assert.Equal(56, window.Endpoint.Bounds.Height);
            Assert.Equal(42, window.ConnectButton.Bounds.Height);
            var watermark = window.Endpoint.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "PART_Placeholder");
            AssertVerticallyCentered(watermark, window.Endpoint);
            Assert.Equal(Color.Parse("#99000000"), Assert.IsAssignableFrom<ISolidColorBrush>(watermark.Foreground).Color);
            var buttonFace = GetContentPresenter(window.ConnectButton);
            Assert.Equal(Color.Parse("#1F000000"), Assert.IsAssignableFrom<ISolidColorBrush>(buttonFace.Background).Color);

            window.Endpoint.Text = "https://cloud.example.com";
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            AssertVerticallyCentered(window.Endpoint.GetVisualDescendants().OfType<TextPresenter>().Single(), window.Endpoint);
            var caption = window.ConnectButton.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == "连接云盘");
            AssertVerticallyCentered(caption, window.ConnectButton);
            MovePointerTo(window, window.ConnectButton);
            Assert.Equal(Color.Parse("#1565C0"), Assert.IsAssignableFrom<ISolidColorBrush>(buttonFace.Background).Color);
        }
        finally
        {
            window.Close();
            window.Stop();
        }
    }

    private static void AssertVerticallyCentered(Control text, Control container)
    {
        var center = text.TranslatePoint(new Point(0, text.Bounds.Height / 2), container);
        Assert.NotNull(center);
        Assert.InRange(Math.Abs(center.Value.Y - container.Bounds.Height / 2), 0, 1);
    }

    private static ContentPresenter GetContentPresenter(Control control)
    {
        return control.GetVisualDescendants().OfType<ContentPresenter>().Single(part => part.Name == "PART_ContentPresenter");
    }

    private static void MovePointerTo(Window window, Control control)
    {
        var center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window);
        Assert.NotNull(center);
        window.MouseMove(center.Value);
        Dispatcher.UIThread.RunJobs();
    }

    [Fact]
    public async Task DisconnectedDriveCannotBrowseOrDownload()
    {
        await using (var ui = HeadlessUnitTestSession.StartNew(typeof(TestApplication)))
        {
            await ui.Dispatch(CheckDisconnectedDrive, TestContext.Current.CancellationToken);
        }
    }

    private static void CheckDisconnectedDrive()
    {
        var window = new WorkspaceWindow(() => null);
        window.Show();
        try
        {
            Assert.True(window.Setup.IsVisible);
            Assert.False(window.Workspace.IsVisible);
            Assert.False(window.ConnectButton.IsEnabled);
            window.Endpoint.Text = "https://cloud.example.com";
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.ConnectButton.IsEnabled);
            Assert.Equal("刷新目录", AutomationProperties.GetName(window.RefreshButton));
            Assert.False(window.RefreshButton.IsEnabled);
            Assert.False(window.DownloadButton.IsEnabled);
            Assert.False(window.UploadButton.IsEnabled);
            Assert.False(window.PairButton.IsEnabled);
            Assert.False(window.SyncNowButton.IsVisible);
            Assert.Equal(Color.Parse("#1976D2"), Assert.IsAssignableFrom<ISolidColorBrush>(window.ConnectButton.Background).Color);
        }
        finally
        {
            window.Close();
            window.Stop();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoredDriveShowsEntriesAndEmptyState(bool dark)
    {
        await using (var ui = HeadlessUnitTestSession.StartNew(typeof(TestApplication)))
        {
            await ui.Dispatch(async () =>
            {
                await CheckRestoredDriveAsync(dark);
                return true;
            }, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task RestoredPairScansWhileHiddenAndQuitsCleanly()
    {
        var temp = Directory.CreateTempSubdirectory("cloudreve-window-");
        try
        {
            string settings = Path.Combine(temp.FullName, "settings");
            var session = new DriveSession("https://site.example/", "drive", "Work", "cloudreve://my/", "user", "access", "refresh",
                DateTimeOffset.UtcNow.AddMinutes(10), DateTimeOffset.UtcNow.AddHours(1));
            var store = new FolderPairStore(settings, session);
            var pair = store.Create(temp.CreateSubdirectory("local").FullName, session.Root, "root-id");
            var transport = new DirectoryHandler { Empty = true };
            await using (var ui = HeadlessUnitTestSession.StartNew(typeof(TestApplication)))
            {
                var cancel = TestContext.Current.CancellationToken;
                await ui.Dispatch(async () =>
                {
                    var window = new WorkspaceWindow(() => session, transport, settings);
                    try
                    {
                        window.Show();
                        await window.RefreshAsync();
                        Assert.Contains(pair.Remote, window.PairLocation.Text);
                        Assert.True(window.SyncNowButton.IsVisible);
                        window.Hide();
                        await transport.RootChecked.Task.WaitAsync(TimeSpan.FromSeconds(5), cancel);
                        await window.PrepareToQuitAsync();
                        Assert.Equal(pair, store.Load());
                        using (store.Lock(pair))
                        {
                            FolderPairStore.Verify(pair);
                        }
                    }
                    finally
                    {
                        window.Close();
                        window.Stop();
                    }

                    return true;
                }, cancel);
            }
        }
        finally
        {
            temp.Delete(true);
        }
    }

    private static async Task CheckRestoredDriveAsync(bool dark)
    {
        var session = new DriveSession("https://site.example/", "drive", "工作云盘", "cloudreve://my/", "user", "access", "refresh",
            DateTimeOffset.UtcNow.AddMinutes(10), DateTimeOffset.UtcNow.AddHours(1));
        var transport = new DirectoryHandler();
        var window = new WorkspaceWindow(() => session, transport)
        {
            Width = 520, Height = 420, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light
        };
        window.Show();
        try
        {
            await window.RefreshAsync();
            Assert.False(window.Setup.IsVisible);
            Assert.True(window.Workspace.IsVisible);
            Assert.Equal(session.Name, window.DriveName.Text);
            Assert.Equal(session.Root, window.LocationText.Text);
            Assert.True(window.PairButton.IsEnabled);
            Assert.True(window.UploadButton.IsEnabled);
            Assert.Equal(Color.Parse(dark ? "#121212" : "#FFFFFF"), Assert.IsAssignableFrom<ISolidColorBrush>(window.Background).Color);
            Assert.False(window.EmptyFolder.IsVisible);
            Assert.Equal(2, window.Files.ItemCount);
            window.Files.SelectedIndex = 0;
            Assert.True(window.OpenFolderButton.IsEnabled);
            Assert.False(window.DownloadButton.IsEnabled);
            window.Files.SelectedIndex = 1;
            Assert.False(window.OpenFolderButton.IsEnabled);
            Assert.True(window.DownloadButton.IsEnabled);
            window.UpdateLayout();
            var selected = Assert.IsType<ListBoxItem>(window.Files.ContainerFromIndex(1));
            var row = GetContentPresenter(selected);
            Assert.Equal(Color.Parse(dark ? "#29FFFFFF" : "#14000000"), Assert.IsAssignableFrom<ISolidColorBrush>(row.Background).Color);
            Assert.True(window.Files.Bounds.Height > 0);
            MovePointerTo(window, window.RefreshButton);
            var refreshFace = GetContentPresenter(window.RefreshButton);
            Assert.Equal(Color.Parse(dark ? "#14FFFFFF" : "#0A000000"), Assert.IsAssignableFrom<ISolidColorBrush>(refreshFace.Background).Color);
            Assert.Equal(window.RefreshButton.Foreground, refreshFace.Foreground);

            transport.Empty = true;
            await window.RefreshAsync();
            Assert.True(window.EmptyFolder.IsVisible);
            Assert.False(window.DownloadButton.IsEnabled);
        }
        finally
        {
            window.Close();
            window.Stop();
        }
    }

    private sealed class DirectoryHandler : HttpMessageHandler
    {
        internal bool Empty { get; set; }
        internal TaskCompletionSource RootChecked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/api/v4/file/events")
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            if (request.RequestUri.AbsolutePath == "/api/v4/file/info")
            {
                RootChecked.TrySetResult();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { code = 0, data = new { type = 1, id = "root-id", path = "cloudreve://my" } })
                };
            }

            Assert.Equal("/api/v4/file", request.RequestUri.AbsolutePath);
            object[] entries = Empty
                ? []
                :
                [
                    new { name = "设计文稿", path = "cloudreve://my/docs", type = 1, size = 0 },
                    new { name = "项目说明.pdf", path = "cloudreve://my/notes.pdf", type = 0, size = 12548 }
                ];
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { code = 0, data = new { files = entries, pagination = new { } } })
            };
        }
    }
}
