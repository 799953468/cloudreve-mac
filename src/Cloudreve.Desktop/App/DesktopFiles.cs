using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Cloudreve.Desktop;

internal static class DesktopFiles
{
    // Windows: %LOCALAPPDATA%\CloudreveClient (tokens and caches must not roam). macOS: ~/Library/Application Support/Cloudreve.
    internal static string DataDirectory
    {
        get
        {
            return OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CloudreveClient")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "Cloudreve");
        }
    }

    internal static string LogDirectory
    {
        get
        {
            return OperatingSystem.IsWindows()
                ? Path.Combine(DataDirectory, "Logs")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Logs", "Cloudreve");
        }
    }

    internal static async Task OpenFolderAsync(Window window, string path)
    {
        using (var folder = await window.StorageProvider.TryGetFolderFromPathAsync(path))
        {
            if (folder == null || !await window.Launcher.LaunchFileAsync(folder))
            {
                throw new IOException(UiText.Get("app.chooseLocalFolder"));
            }
        }
    }

    internal static void Reveal(string path)
    {
        var start = OperatingSystem.IsWindows()
            // Explorer parses "/select," itself, so the path cannot go through ArgumentList quoting. Windows paths never contain quotes.
            ? new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), $"/select,\"{Path.GetFullPath(path)}\"")
            : new ProcessStartInfo("/usr/bin/open") { ArgumentList = { "-R", path } };
        start.UseShellExecute = false;
        using (Process.Start(start))
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