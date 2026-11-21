using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Cloudreve.Desktop;

internal static class DesktopLog
{
    internal static Logger CreateLogger(string directory)
    {
        return new LoggerConfiguration()
            .WriteTo.File(Path.Combine(directory, "desktop-.log"),
                rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: 5 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: 7)
            .CreateLogger();
    }

    internal static void WriteFailure(this ILogger logger, LogEventLevel level, string operation, Exception exception)
    {
        // Exception messages may contain callback URLs, signed download URLs or local paths.
        string errorType = exception.GetType().Name;
        if (exception is HttpRequestException { StatusCode: { } statusCode })
        {
            logger.Write(level, "{Operation} failed: {ErrorType} (HTTP {StatusCode})", operation, errorType, (int)statusCode);
        }
        else
        {
            logger.Write(level, "{Operation} failed: {ErrorType}", operation, errorType);
        }
    }
}