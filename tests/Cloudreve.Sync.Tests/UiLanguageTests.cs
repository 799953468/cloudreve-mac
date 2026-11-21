using System.Net;
using System.Net.Http.Json;
using Avalonia.Headless;
using Cloudreve.Desktop;
using Xunit;

namespace Cloudreve.Sync.Tests;

[CollectionDefinition("Desktop language", DisableParallelization = true)]
public sealed class DesktopLanguageCollection
{
}

[Collection("Desktop language")]
public sealed class UiLanguageTests
{
    [Fact]
    public async Task EnglishLocalizesTheWindowButNotFileNamesOrSiteMessages()
    {
        var temp = Directory.CreateTempSubdirectory();
        var previous = UiText.Language;
        try
        {
            UiText.Language = UiLanguage.English;
            await using (var ui = HeadlessUnitTestSession.StartNew(typeof(TestApplication)))
            {
                await ui.Dispatch(() =>
                {
                    var window = new WorkspaceWindow(new SessionFileStore(temp.FullName));
                    try
                    {
                        window.Show();
                        Assert.Equal("Connect drive", window.ConnectButton.Content);
                        Assert.Equal("Download…", window.DownloadButton.Content);
                        Assert.Equal("Resolve conflicts…", window.ConflictsItem.Header);
                    }
                    finally
                    {
                        window.Close();
                        window.Stop();
                    }
                }, TestContext.Current.CancellationToken);
            }

            string fileName = "连接云盘";
            Assert.Equal("Move “连接云盘”", UiText.Format($"移动「{fileName}」"));
            var response = System.Text.Json.JsonSerializer.SerializeToElement(new { code = 403, msg = "连接云盘" });
            var failure = Assert.Throws<IOException>(() => DesktopLogin.Envelope(response));
            Assert.Equal("Cloudreve request failed (403): 连接云盘", failure.Message);
        }
        finally
        {
            UiText.Language = previous;
            temp.Delete(true);
        }
    }
}