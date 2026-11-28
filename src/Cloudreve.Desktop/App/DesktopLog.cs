using Cloudreve.Sync;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Cloudreve.Desktop;

internal static class DesktopLog
{
    internal static Logger CreateLogger(string directory, LoggingLevelSwitch? level = null, bool logToFile = true, int retainedFiles = 7)
    {
        var config = new LoggerConfiguration().MinimumLevel.ControlledBy(level ?? new LoggingLevelSwitch());
        if (logToFile)
        {
            config.WriteTo.File(Path.Combine(directory, "desktop-.log"),
                outputTemplate: FailureLog.OutputTemplate,
                rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: 5 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: retainedFiles);
        }

        return config.CreateLogger();
    }

    internal static void WriteFailure(this ILogger logger, LogEventLevel level, string operation, Exception exception)
    {
        if (!logger.IsEnabled(level))
        {
            return;
        }

        logger = FailureLog.WithDiagnostics(logger, exception);
        if (exception.Data[nameof(DirectoryCheckFailure)] is DirectoryCheckFailure directoryCheck)
        {
            logger = logger.ForContext(nameof(DirectoryCheckFailure), directoryCheck.ToString());
        }

        string errorType = exception.GetType().Name;
        if (exception is HttpRequestException { StatusCode: { } statusCode })
        {
            logger.Write(level, "{Operation} failed: {ErrorType} (HTTP {StatusCode})", operation, errorType, (int)statusCode);
        }
        else if (exception is InvalidDataException && exception.Data[nameof(RemoteVersionFailure)] is RemoteVersionFailure reason)
        {
            logger.Write(level, "{Operation} failed: {ErrorType} (remote version: {Reason:l})", operation, errorType, reason.ToString());
        }
        else
        {
            logger.Write(level, "{Operation} failed: {ErrorType}", operation, errorType);
        }
    }
}
