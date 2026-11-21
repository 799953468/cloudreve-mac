namespace Cloudreve.Desktop;

internal sealed partial class DriveClient
{
    internal static string ChildUri(string parent, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(['/', '\\', '\0']) >= 0)
        {
            throw new InvalidDataException("文件名无效。");
        }

        var folder = new Uri(parent, UriKind.Absolute);
        if (folder.Scheme != "cloudreve" || folder.Fragment.Length != 0)
        {
            throw new InvalidDataException("无效的云端目录。");
        }

        return folder.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/" + Uri.EscapeDataString(name) + folder.Query;
    }

    internal async Task CreateFolderAsync(string parent, string name, CancellationToken cancel)
    {
        await JsonAsync(HttpMethod.Post, "file/create", new { uri = ChildUri(parent, name), type = "folder", err_on_conflict = true }, cancel);
    }

    internal async Task RenameAsync(DriveEntry file, string name, CancellationToken cancel)
    {
        _ = ChildUri(file.Uri, name);
        await JsonAsync(HttpMethod.Post, "file/rename", new { uri = file.Uri, new_name = name }, cancel);
    }

    internal async Task MoveAsync(DriveEntry file, string directory, CancellationToken cancel)
    {
        await JsonAsync(HttpMethod.Post, "file/move", new { uris = new[] { file.Uri }, dst = directory, copy = false }, cancel);
    }

    internal async Task TrashAsync(DriveEntry file, CancellationToken cancel)
    {
        await JsonAsync(HttpMethod.Delete, "file", new { uris = new[] { file.Uri }, skip_soft_delete = false }, cancel);
    }
}
