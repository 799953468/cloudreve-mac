using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cloudreve.Desktop;

[JsonSerializable(typeof(DisplayPreferences))]
[JsonSerializable(typeof(DriveSession))]
[JsonSerializable(typeof(FolderPair))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(Dictionary<string, FileBaseline>))]
[JsonSerializable(typeof(FileBaseline))]
[JsonSerializable(typeof(QueuedFile[]))]
[JsonSerializable(typeof(ActivityEntry[]))]
[JsonSerializable(typeof(DownloadCheckpoint))]
[JsonSerializable(typeof(TransferFiles.Receipt))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(JsonDocument))]
internal partial class DesktopJson : JsonSerializerContext
{
}
