using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cloudreve.Desktop;

internal sealed record RefreshTokenRequest(string RefreshToken);
internal sealed record CreateFileRequest(string Uri, string Type, bool ErrOnConflict = true);
internal sealed record RenameFileRequest(string Uri, string NewName);
internal sealed record MoveFilesRequest(string[] Uris, string Dst, bool Copy = false);
internal sealed record TrashFilesRequest(string[] Uris, bool SkipSoftDelete = false);
internal sealed record DownloadUrlRequest(string[] Uris, bool Download, string? Entity);
internal sealed record CreateUploadRequest(
    string Uri, long Size, string PolicyId, string[] EncryptionSupported, long LastModified, string? EntityType, string? Previous);
internal sealed record CancelUploadRequest(string Id, string Uri);
internal sealed record QiniuPart(string Etag, [property: JsonPropertyName("partNumber")] int PartNumber);
internal sealed record QiniuCompleteRequest(QiniuPart[] Parts);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RefreshTokenRequest))]
[JsonSerializable(typeof(CreateFileRequest))]
[JsonSerializable(typeof(RenameFileRequest))]
[JsonSerializable(typeof(MoveFilesRequest))]
[JsonSerializable(typeof(TrashFilesRequest))]
[JsonSerializable(typeof(DownloadUrlRequest))]
[JsonSerializable(typeof(CreateUploadRequest))]
[JsonSerializable(typeof(CancelUploadRequest))]
[JsonSerializable(typeof(QiniuCompleteRequest))]
[JsonSerializable(typeof(JsonElement))]
internal partial class DriveApiJson : JsonSerializerContext
{
}
