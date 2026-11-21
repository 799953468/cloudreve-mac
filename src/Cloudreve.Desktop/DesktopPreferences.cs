using System.Text.Json;
using Cloudreve.Sync;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Cloudreve.Desktop;

internal sealed record DisplayPreferences(LogEventLevel LogLevel = LogEventLevel.Information, bool ConflictAlerts = true, UiLanguage Language = UiLanguage.Chinese);

internal sealed class DesktopPreferences(string directory)
{
    private readonly string path = Path.Combine(directory, "preferences.json");
    internal DisplayPreferences Current { get; private set; } = new();
    internal LoggingLevelSwitch LogLevel { get; } = new();

    internal void Load()
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            FolderPairStore.CheckDirectory(directory);
            if (MacFile.Inspect(path) != NodeKind.File)
            {
                throw new IOException(UiText.Get("设置必须是普通文件。"));
            }

            var value = JsonSerializer.Deserialize<DisplayPreferences>(File.ReadAllText(path));
            if (value != null && Enum.IsDefined(value.LogLevel) && Enum.IsDefined(value.Language))
            {
                Apply(value);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Logger.WriteFailure(LogEventLevel.Warning, "Read desktop preferences", ex);
        }
    }

    internal void Save(DisplayPreferences value)
    {
        if (!Enum.IsDefined(value.LogLevel) || !Enum.IsDefined(value.Language))
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        Directory.CreateDirectory(directory);
        FolderPairStore.CheckDirectory(directory);
        string temp = Path.Combine(directory, $".preferences-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(output, value);
                output.Flush(true);
            }

            File.Move(temp, path, true);
            Apply(value);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    private void Apply(DisplayPreferences value)
    {
        Current = value;
        LogLevel.MinimumLevel = value.LogLevel;
    }
}