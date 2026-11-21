using System.Text.Json;
using Cloudreve.Sync;

namespace Cloudreve.Desktop;

internal sealed class SessionFileStore
{
    private const UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode PrivateDirectory = PrivateFile | UnixFileMode.UserExecute;
    private readonly string path;

    internal SessionFileStore(string directory)
    {
        DirectoryPath = MacFile.NormalizeSystemPath(directory);
        path = Path.Combine(DirectoryPath, "drives.json");
    }

    internal string DirectoryPath { get; }

    internal static SessionFileStore OpenDefault()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new SessionFileStore(Path.Combine(home, "Library", "Application Support", "Cloudreve"));
    }

    internal DriveSession? Load()
    {
        if (!PrepareDirectory(false) || !HasRegularFile())
        {
            return null;
        }

        RestrictPermissions(path, PrivateFile);
        using (var stream = File.OpenRead(path))
        {
            try
            {
                return Validate(JsonSerializer.Deserialize<DriveSession>(stream));
            }
            catch (JsonException)
            {
                throw new InvalidDataException(UiText.Get("本机登录文件格式无效，请重新连接云盘。"));
            }
        }
    }

    internal void Save(DriveSession session)
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException();
        }

        Validate(session);
        PrepareDirectory(true);
        HasRegularFile();
        string temporary = Path.Combine(DirectoryPath, $".drives-{Guid.NewGuid():N}.tmp");
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, UnixCreateMode = PrivateFile
        };
        try
        {
            using (var output = new FileStream(temporary, options))
            {
                JsonSerializer.Serialize(output, session);
                output.Flush(true);
            }

            File.Move(temporary, path, true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    internal void Forget()
    {
        if (PrepareDirectory(false) && HasRegularFile())
        {
            File.Delete(path);
        }
    }

    private bool PrepareDirectory(bool create)
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException();
        }

        for (var entry = new DirectoryInfo(DirectoryPath); entry != null; entry = entry.Parent)
        {
            if (MacFile.Inspect(entry.FullName) is not (null or NodeKind.Directory))
            {
                throw new IOException(UiText.Get("登录配置目录不能是链接或普通文件。"));
            }
        }

        if (!Directory.Exists(DirectoryPath))
        {
            if (!create)
            {
                return false;
            }

            Directory.CreateDirectory(DirectoryPath, PrivateDirectory);
        }

        RestrictPermissions(DirectoryPath, PrivateDirectory);
        return true;
    }

    private bool HasRegularFile()
    {
        var kind = MacFile.Inspect(path);
        if (kind is not (null or NodeKind.File))
        {
            throw new IOException(UiText.Get("登录配置必须是普通文件，不能是链接。"));
        }

        return kind == NodeKind.File;
    }

    private static void RestrictPermissions(string target, UnixFileMode mode)
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException();
        }

        if (File.GetUnixFileMode(target) != mode)
        {
            File.SetUnixFileMode(target, mode);
        }
    }

    private static DriveSession Validate(DriveSession? session)
    {
        if (session == null || !Uri.TryCreate(session.Site, UriKind.Absolute, out var site) ||
            site.Scheme is not ("https" or "http") || site.Host.Length == 0 || site.UserInfo.Length > 0 ||
            !Uri.TryCreate(session.Root, UriKind.Absolute, out var root) || root.Scheme != "cloudreve" ||
            string.IsNullOrWhiteSpace(session.DriveId) || string.IsNullOrWhiteSpace(session.UserId) ||
            string.IsNullOrWhiteSpace(session.AccessToken) || string.IsNullOrWhiteSpace(session.RefreshToken) ||
            session.AccessExpires == default || session.RefreshExpires == default)
        {
            throw new InvalidDataException(UiText.Get("本机登录记录无效，请重新连接云盘。"));
        }

        return session;
    }
}