using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Cloudreve.Desktop;
using Serilog;
using Serilog.Events;
using Xunit;

namespace Cloudreve.Sync.Tests;

public sealed class DesktopLogTests
{
    [Fact]
    public void OlderPreferencesKeepDefaultsForMissingFields()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            string directory = Cloudreve.Sync.MacFile.NormalizeSystemPath(root.FullName);
            File.WriteAllText(Path.Combine(directory, "preferences.json"), """{"ConflictAlerts":false}""");
            var settings = new DesktopPreferences(directory);
            Assert.Null(settings.Load());
            Assert.Equal(new DisplayPreferences(ConflictAlerts: false), settings.Current);
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public void PreferencesPersistAndChangeTheActiveLogThreshold()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            string directory = Cloudreve.Sync.MacFile.NormalizeSystemPath(root.FullName);
            var settings = new DesktopPreferences(directory);
            using (var logger = DesktopLog.CreateLogger(directory, settings.LogLevel))
            {
                logger.Debug("Not visible");
                settings.Save(new DisplayPreferences(LogEventLevel.Debug, false, UiLanguage.English));
                logger.Debug("Visible after change");
            }

            var restored = new DesktopPreferences(directory);
            restored.Load();
            Assert.Equal(settings.Current, restored.Current);
            string log = File.ReadAllText(Assert.Single(Directory.GetFiles(directory, "desktop-*.log")));
            Assert.Contains("Visible after change", log);
            Assert.DoesNotContain("Not visible", log);
            File.WriteAllText(Path.Combine(directory, "preferences.json"), "invalid");
            var defaults = new DesktopPreferences(directory);
            Assert.IsType<JsonException>(defaults.Load());
            Assert.Equal(new DisplayPreferences(), defaults.Current);
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public void LogsTypedNetworkCausesWithoutLeakingMessagesOrArbitraryData()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var cause = new IOException("/private/file", new SocketException((int)SocketError.ConnectionRefused));
            var request = new HttpRequestException(HttpRequestError.ConnectionError, "https://private/?token=secret", cause);
            request.Data["CloudreveCode"] = "private-code";
            using (var log = DesktopLog.CreateLogger(root.FullName))
            {
                log.WriteFailure(LogEventLevel.Warning, "Synchronize folder pair", request);
                log.WriteFailure(LogEventLevel.Warning, "Read remote metadata", new IOException("private") { Data = { ["CloudreveCode"] = 40011 } });
            }

            var lines = File.ReadAllLines(Assert.Single(Directory.GetFiles(root.FullName, "desktop-*.log")));
            using (var json = JsonDocument.Parse(lines[0][lines[0].IndexOf('{')..]))
            {
                var fields = json.RootElement;
                Assert.Equal("ConnectionError", fields.GetProperty("HttpRequestError").GetString());
                Assert.Equal(JsonValueKind.Null, fields.GetProperty("HttpStatus").ValueKind);
                Assert.Equal("ConnectionRefused", fields.GetProperty("SocketError").GetString());
                Assert.Equal(new[] { "IOException", "SocketException" }, fields.GetProperty("Causes").EnumerateArray().Select(item => item.GetString()));
                Assert.False(fields.TryGetProperty("CloudreveCode", out _));
            }

            Assert.Contains("40011", lines[1]);
            Assert.All(lines, line =>
            {
                Assert.DoesNotContain("private", line);
                Assert.DoesNotContain("secret", line);
            });
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public void WritesFailureDiagnosticsWithoutExceptionMessages()
    {
        var root = Directory.CreateTempSubdirectory("cloudreve-log-tests-");
        try
        {
            string directory = Path.Combine(root.FullName, "Logs");
            using (var log = DesktopLog.CreateLogger(directory))
            {
                var requestError = new HttpRequestException("https://example.invalid/download?token=private", null, HttpStatusCode.Forbidden);
                log.WriteFailure(LogEventLevel.Warning, "Download file", requestError);
                log.WriteFailure(LogEventLevel.Error, "Restore session", new IOException("/private/user/document.txt"));
                var file = new DriveEntry("private", "cloudreve://my/private", false, 10);
                var missing = Assert.Throws<InvalidDataException>(() => PairInventory.RequireVersion(file, file.Uri));
                var mismatch = Assert.Throws<InvalidDataException>(() => PairInventory.RequireVersion(file with { Version = "private-version" }, "cloudreve://my/elsewhere"));
                var unsupported = Assert.Throws<InvalidDataException>(() => PairInventory.RequireVersion(file with { SkipSync = true }, file.Uri));
                log.WriteFailure(LogEventLevel.Warning, "Synchronize folder pair", missing);
                log.WriteFailure(LogEventLevel.Warning, "Synchronize folder pair", mismatch);
                log.WriteFailure(LogEventLevel.Warning, "Synchronize folder pair", unsupported);
                var untrusted = new InvalidDataException("private-message") { Data = { [nameof(RemoteVersionFailure)] = "private-data" } };
                log.WriteFailure(LogEventLevel.Warning, "Synchronize folder pair", untrusted);
                var missingDirectory = Assert.Throws<IOException>(() => FolderPairStore.CheckDirectory(Path.Combine(root.FullName, "private-directory")));
                log.WriteFailure(LogEventLevel.Warning, "Synchronize folder pair", missingDirectory);
                var untrustedDirectory = new IOException { Data = { [nameof(DirectoryCheckFailure)] = "private-directory" } };
                log.WriteFailure(LogEventLevel.Warning, "Synchronize folder pair", untrustedDirectory);
            }

            string contents = File.ReadAllText(Assert.Single(Directory.GetFiles(directory, "desktop-*.log")));
            Assert.Contains("Download file failed: HttpRequestException (HTTP 403)", contents);
            Assert.Contains("Restore session failed: IOException", contents);
            Assert.Contains("(remote version: MissingVersion)", contents);
            Assert.Contains("(remote version: UriMismatch)", contents);
            Assert.Contains("(remote version: UnsupportedEntry)", contents);
            Assert.Contains("\"DirectoryCheckFailure\":\"Missing\"", contents);
            Assert.DoesNotContain("example.invalid", contents);
            Assert.DoesNotContain("private", contents);
        }
        finally
        {
            root.Delete(true);
        }
    }
}
