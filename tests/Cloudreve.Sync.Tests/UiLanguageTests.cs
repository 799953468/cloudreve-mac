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
                    var host = new WorkspaceHost(new DriveProfiles(new SessionFileStore(temp.FullName)));
                    using var api = new HttpClient();
                    var window = new AddDriveWindow(host, api);
                    try
                    {
                        window.Show();
                        Assert.Equal("Add Drive", window.Heading.Text);
                        Assert.Equal("Connect", window.Next.Content);
                        Assert.Equal("Site URL *", window.Endpoint.PlaceholderText);
                    }
                    finally
                    {
                        window.Close();
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