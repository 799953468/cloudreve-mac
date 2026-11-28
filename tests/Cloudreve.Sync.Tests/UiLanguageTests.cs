using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
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
            Assert.Equal("Open 连接云盘", UiText.Get("addDrive.openDrive", ("name", fileName)));
            Assert.Equal("Done", UiText.Get("activity.done"));
            Assert.Equal("Finish", UiText.Get("addDrive.finish"));
            Assert.Equal("no.such.key", UiText.Get("no.such.key"));
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

    [Fact]
    public void ChineseAndEnglishDefineTheSameKeysAndPlaceholders()
    {
        var chinese = Flatten(UiText.Load("zh"));
        var english = Flatten(UiText.Load("en"));
        Assert.Equal(chinese.Keys.Order(), english.Keys.Order());
        foreach (var (key, text) in chinese)
        {
            Assert.False(string.IsNullOrWhiteSpace(text), key);
            Assert.False(string.IsNullOrWhiteSpace(english[key]), key);
            Assert.Equal(Placeholders(text), Placeholders(english[key]));
        }
    }

    [Fact]
    public void EveryLiteralKeyInTheSourceExists()
    {
        var keys = Flatten(UiText.Load("zh")).Keys.ToHashSet();
        string source = Path.Combine(RepositoryRoot(), "src", "Cloudreve.Desktop");
        var pattern = new Regex(@"UiText\.Get\(\s*""([A-Za-z]+(?:\.[A-Za-z]+)+)""|\{d:Tr ([A-Za-z.]+)\}|\?\s*""([a-z]+\.[A-Za-z.]+)""\s*:\s*""([a-z]+\.[A-Za-z.]+)""");
        var missing = new List<string>();
        foreach (string file in Directory.EnumerateFiles(source, "*.*", SearchOption.AllDirectories)
                     .Where(file => (file.EndsWith(".cs") || file.EndsWith(".axaml")) && !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
        {
            foreach (Match match in pattern.Matches(File.ReadAllText(file)))
            {
                foreach (Group group in match.Groups.Values.Skip(1).Where(group => group.Success))
                {
                    if (!keys.Contains(group.Value))
                    {
                        missing.Add($"{Path.GetFileName(file)}: {group.Value}");
                    }
                }
            }
        }

        Assert.Empty(missing);
    }

    private static Dictionary<string, string> Flatten(JsonElement element, string prefix = "")
    {
        var result = new Dictionary<string, string>();
        foreach (var property in element.EnumerateObject())
        {
            string key = prefix + property.Name;
            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var (child, text) in Flatten(property.Value, key + "."))
                {
                    result[child] = text;
                }
            }
            else
            {
                result[key] = property.Value.GetString()!;
            }
        }

        return result;
    }

    private static string[] Placeholders(string text)
    {
        return Regex.Matches(text, @"\{\{(\w+)\}\}").Select(match => match.Groups[1].Value).Order().ToArray();
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CloudreveClient.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
