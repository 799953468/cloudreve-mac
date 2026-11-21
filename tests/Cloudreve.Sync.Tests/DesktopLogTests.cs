using System.Net;
using Cloudreve.Desktop;
using Serilog.Events;
using Xunit;

namespace Cloudreve.Sync.Tests;

public sealed class DesktopLogTests
{
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
            }

            string contents = File.ReadAllText(Assert.Single(Directory.GetFiles(directory, "desktop-*.log")));
            Assert.Contains("Download file failed: HttpRequestException (HTTP 403)", contents);
            Assert.Contains("Restore session failed: IOException", contents);
            Assert.DoesNotContain("example.invalid", contents);
            Assert.DoesNotContain("private", contents);
        }
        finally
        {
            root.Delete(true);
        }
    }
}