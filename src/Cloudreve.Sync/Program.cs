using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;
using Serilog.Events;

namespace Cloudreve.Sync;

public sealed class SyncResult
{
    public int Uploaded { get; set; }
    public int Downloaded { get; set; }
    public int Unchanged { get; set; }
    public List<string> Issues { get; set; } = [];
    public string? Error { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SyncResult))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(LocalSyncState))]
internal partial class SyncJson : JsonSerializerContext
{
}

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length != 4 || args[0] != "sync")
        {
            Console.Error.WriteLine("Usage: Cloudreve.Sync sync <local-directory> <mounted-directory> <state-file>");
            return 2;
        }

        SyncResult result;
        using (var log = new LoggerConfiguration().WriteTo.Console(outputTemplate: FailureLog.OutputTemplate,
                   standardErrorFromLevel: LogEventLevel.Verbose).CreateLogger())
        {
            var timer = Stopwatch.StartNew();
            try
            {
                result = new SyncEngine(args[1], args[2], args[3]).Run();
                var level = result.Issues.Count == 0 ? LogEventLevel.Information : LogEventLevel.Warning;
                log.Write(level, "Sync finished in {ElapsedMs} ms: {Uploaded} uploaded, {Downloaded} downloaded, {Unchanged} unchanged, {IssueCount} issues",
                    timer.ElapsedMilliseconds, result.Uploaded, result.Downloaded, result.Unchanged, result.Issues.Count);
            }
            catch (Exception exception)
            {
                FailureLog.WithDiagnostics(log, exception).Error("Sync failed after {ElapsedMs} ms: {ErrorType}",
                    timer.ElapsedMilliseconds, exception.GetType().Name);
                result = new SyncResult { Error = exception.Message };
            }
        }

        Console.WriteLine(JsonSerializer.Serialize(result, SyncJson.Default.SyncResult));
        return result.Error is null ? 0 : 1;
    }
}
