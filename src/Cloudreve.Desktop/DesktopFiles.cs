using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Cloudreve.Desktop;

internal static class DesktopFiles
{
    internal static string LogDirectory
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Logs", "Cloudreve"); }
    }

    internal static async Task OpenFolderAsync(Window window, string path)
    {
        using (var folder = await window.StorageProvider.TryGetFolderFromPathAsync(path))
        {
            if (folder == null || !await window.Launcher.LaunchFileAsync(folder))
            {
                throw new IOException(UiText.Get("请选择本机文件夹。"));
            }
        }
    }

    internal static void Reveal(string path)
    {
        using (Process.Start(new ProcessStartInfo("/usr/bin/open") { ArgumentList = { "-R", path }, UseShellExecute = false }))
        {
        }
    }

    internal static string Bytes(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int i = 0;
        while (bytes >= 1024 && i < units.Length - 1)
        {
            bytes /= 1024;
            i++;
        }

        return $"{bytes:0.#} {units[i]}";
    }
}