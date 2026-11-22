using System.Net.Sockets;
using System.Text.Json;
using Serilog;

namespace Cloudreve.Sync;

internal static class FailureLog
{
    internal const string OutputTemplate = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}";

    internal static ILogger WithDiagnostics(ILogger logger, Exception exception)
    {
        // Allowlist diagnostic fields. Messages and Exception.Data may contain credentials or paths.
        logger = logger.ForContext("HResult", $"0x{exception.HResult:X8}");
        if (exception.TargetSite is { } site)
        {
            logger = logger.ForContext("ThrowSite", $"{site.DeclaringType?.Name}.{site.Name}");
        }

        var causes = new List<string>();
        for (Exception? error = exception; error != null; error = error.InnerException)
        {
            if (error != exception)
            {
                causes.Add(error.GetType().Name);
            }

            if (error is HttpRequestException request)
            {
                logger = logger.ForContext("HttpRequestError", request.HttpRequestError.ToString())
                    .ForContext("HttpStatus", (int?)request.StatusCode);
            }
            else if (error is HttpIOException stream)
            {
                logger = logger.ForContext("HttpStreamError", stream.HttpRequestError.ToString());
            }
            else if (error is SocketException socket)
            {
                logger = logger.ForContext("SocketError", socket.SocketErrorCode.ToString())
                    .ForContext("NativeErrorCode", socket.NativeErrorCode);
            }
            else if (error is JsonException json)
            {
                logger = logger.ForContext("JsonLine", json.LineNumber).ForContext("JsonBytePosition", json.BytePositionInLine);
            }

            if (error.Data["CloudreveCode"] is int code)
            {
                logger = logger.ForContext("CloudreveCode", code);
            }
        }

        if (causes.Count > 0)
        {
            logger = logger.ForContext("Causes", causes);
        }

        return logger;
    }
}
