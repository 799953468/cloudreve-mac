using System.Runtime.InteropServices;

namespace Cloudreve.Sync;

internal enum NodeKind { File, Directory, Unsupported }

internal static partial class MacFile
{
    internal static string NormalizeSystemPath(string path)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        foreach (string alias in new[] { "/var", "/tmp", "/etc" })
        {
            if (full == alias || full.StartsWith(alias + "/", StringComparison.Ordinal))
            {
                string expected = "/private" + alias;
                if (Directory.ResolveLinkTarget(alias, true)?.FullName == expected)
                {
                    return expected + full[alias.Length..];
                }
            }
        }

        return full;
    }

    // Darwin's getattrlist returns the vnode type without opening a FIFO or following a link.
    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeList
    {
        public ushort Count, Reserved;
        public uint Common, Volume, Directory, File, Fork;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TypeBuffer
    {
        public uint Length, ObjectType;
    }

    [LibraryImport("libSystem.B.dylib", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int getattrlist(string path, ref AttributeList attributes, out TypeBuffer result, nuint size, nuint options);

    internal static NodeKind? Inspect(string path)
    {
        var attributes = new AttributeList { Count = 5, Common = 0x00000008 }; // ATTR_CMN_OBJTYPE
        if (getattrlist(path, ref attributes, out var result, 8, 1) != 0) // FSOPT_NOFOLLOW
        {
            int error = Marshal.GetLastPInvokeError();
            if (error == 2)
            {
                return null;
            }

            throw new IOException($"无法读取 {path} (errno {error})");
        }

        switch (result.ObjectType)
        {
            case 1: return NodeKind.File;
            case 2: return NodeKind.Directory;
            default: return NodeKind.Unsupported;
        }
    }
}
