using System.Net;
using Cloudreve.Desktop;
using Serilog;
using Serilog.Events;
using Xunit;

namespace Cloudreve.Sync.Tests;

public sealed class DesktopLogTests
{
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
            defaults.Load();
            Assert.Equal(new DisplayPreferences(), defaults.Current);
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
            }

            string contents = File.ReadAllText(Assert.Single(Directory.GetFiles(directory, "desktop-*.log")));
            Assert.Contains("Download file failed: HttpRequestException (HTTP 403)", contents);
            Assert.Contains("Restore session failed: IOException", contents);
            Assert.Contains("(remote version: MissingVersion)", contents);
            Assert.Contains("(remote version: UriMismatch)", contents);
            Assert.Contains("(remote version: UnsupportedEntry)", contents);
            Assert.DoesNotContain("example.invalid", contents);
            Assert.DoesNotContain("private", contents);
        }
        finally
        {
            root.Delete(true);
        }
    }
}
