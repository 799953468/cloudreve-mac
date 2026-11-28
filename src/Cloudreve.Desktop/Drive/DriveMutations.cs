namespace Cloudreve.Desktop;

internal sealed partial class DriveClient
{
    internal static string ChildUri(string parent, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(['/', '\\', '\0']) >= 0)
        {
            throw new InvalidDataException(UiText.Get("api.invalidFileName"));
        }

        var folder = new Uri(parent, UriKind.Absolute);
        if (folder.Scheme != "cloudreve" || folder.Fragment.Length != 0)
        {
            throw new InvalidDataException(UiText.Get("api.invalidFolder"));
        }

        return folder.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/" + Uri.EscapeDataString(name) + folder.Query;
    }

    internal async Task CreateFolderAsync(string parent, string name, CancellationToken cancel)
    {
        var request = new CreateFileRequest(ChildUri(parent, name), "folder");
        await JsonAsync(HttpMethod.Post, "file/create", request, DriveApiJson.Default.CreateFileRequest, cancel);
    }

    internal async Task RenameAsync(DriveEntry file, string name, CancellationToken cancel)
    {
        _ = ChildUri(file.Uri, name);
        var request = new RenameFileRequest(file.Uri, name);
        await JsonAsync(HttpMethod.Post, "file/rename", request, DriveApiJson.Default.RenameFileRequest, cancel);
    }

    internal async Task MoveAsync(DriveEntry file, string directory, CancellationToken cancel)
    {
        var request = new MoveFilesRequest([file.Uri], directory);
        await JsonAsync(HttpMethod.Post, "file/move", request, DriveApiJson.Default.MoveFilesRequest, cancel);
    }

    internal async Task TrashAsync(DriveEntry file, CancellationToken cancel)
    {
        var request = new TrashFilesRequest([file.Uri]);
        await JsonAsync(HttpMethod.Delete, "file", request, DriveApiJson.Default.TrashFilesRequest, cancel);
    }
}
