using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace Cloudreve.Sync;

internal static partial class LocalFile
{
    // Win32 implementation. Handles are opened with FILE_FLAG_OPEN_REPARSE_POINT so symbolic links and junctions
    // are inspected rather than followed, and FILE_READ_ATTRIBUTES so files locked by other programs stay readable.
    [SupportedOSPlatform("windows")]
    private static partial class Win32
    {
        private const uint InvalidAttributes = uint.MaxValue;
        private const uint AttributeDirectory = 0x10;
        private const uint AttributeReparsePoint = 0x400;
        private const uint NameSurrogate = 0x20000000; // IsReparseTagNameSurrogate: symlinks, junctions, mount points
        private const uint ReadAttributes = 0x80;
        private const uint ShareAll = 0x1 | 0x2 | 0x4;
        private const uint OpenExisting = 3;
        private const uint OpenFlags = 0x02000000 | 0x00200000; // FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT
        private const int FileBasicInfo = 0;
        private const int FileStandardInfo = 1;
        private const int FileAttributeTagInfo = 9;
        private const int FileIdInfo = 18;
        private const int ErrorFileNotFound = 2;
        private const int ErrorPathNotFound = 3;
        private const int ErrorFileExists = 80;
        private const int ErrorInvalidName = 123;
        private const int ErrorDirNotEmpty = 145;
        private const int ErrorBadPathName = 161;
        private const int ErrorAlreadyExists = 183;
        private const int ErrorDirectory = 267;

        private static readonly SearchValues<char> InvalidNameChars = SearchValues.Create("<>:\"/\\|?*");

        [StructLayout(LayoutKind.Sequential)]
        private struct AttributeTagInfo
        {
            public uint FileAttributes, ReparseTag;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicInfo
        {
            public long CreationTime, LastAccessTime, LastWriteTime, ChangeTime;
            public uint FileAttributes;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct StandardInfo
        {
            public long AllocationSize, EndOfFile;
            public uint NumberOfLinks;
            public byte DeletePending, Directory;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IdInfo
        {
            public ulong VolumeSerialNumber, IdLow, IdHigh;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct ByHandleInfo
        {
            public uint FileAttributes;
            public long CreationTime, LastAccessTime, LastWriteTime;
            public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
        }

        [LibraryImport("kernel32.dll", EntryPoint = "GetFileAttributesW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        private static partial uint GetFileAttributes(string path);

        [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        private static partial SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static unsafe partial bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, void* info, uint size);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleInfo info);

        [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeInformationByHandleW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static unsafe partial bool GetVolumeInformationByHandle(SafeFileHandle file, char* volumeName, uint volumeNameSize, uint* serial,
            uint* maxComponentLength, uint* flags, char* fileSystemName, uint fileSystemNameSize);

        [LibraryImport("kernel32.dll", EntryPoint = "CreateDirectoryW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CreateDirectory(string path, nint security);

        [LibraryImport("kernel32.dll", EntryPoint = "RemoveDirectoryW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool RemoveDirectory(string path);

        // Raw Win32 calls do not get .NET's long-path handling, so pass extended-length paths.
        private static string Native(string path)
        {
            string full = Path.GetFullPath(path);
            if (full.StartsWith(@"\\?\", StringComparison.Ordinal) || full.StartsWith(@"\\.\", StringComparison.Ordinal))
            {
                return full;
            }

            return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
        }

        private static SafeFileHandle Open(string nativePath)
        {
            return CreateFile(nativePath, ReadAttributes, ShareAll, 0, OpenExisting, OpenFlags, 0);
        }

        private static unsafe bool Query<T>(SafeFileHandle handle, int infoClass, out T info) where T : unmanaged
        {
            T value = default;
            bool ok = GetFileInformationByHandleEx(handle, infoClass, &value, (uint)sizeof(T));
            info = value;
            return ok;
        }

        private static bool Missing(int error)
        {
            // A name this volume cannot store cannot exist on it either.
            return error is ErrorFileNotFound or ErrorPathNotFound or ErrorInvalidName or ErrorBadPathName;
        }

        private static IOException Failure(string message, int error)
        {
            return new IOException($"{message} ({Marshal.GetPInvokeErrorMessage(error)})", unchecked((int)0x80070000) | error);
        }

        internal static NodeKind? Inspect(string path)
        {
            string native = Native(path);
            uint attributes = GetFileAttributes(native);
            if (attributes == InvalidAttributes)
            {
                int error = Marshal.GetLastPInvokeError();
                return Missing(error) ? null : throw Failure($"无法读取 {path}", error);
            }

            if ((attributes & AttributeReparsePoint) != 0)
            {
                // Cloud placeholders and deduplicated files are reparse points too; only links redirect the name.
                using (var handle = Open(native))
                {
                    if (handle.IsInvalid)
                    {
                        int error = Marshal.GetLastPInvokeError();
                        return Missing(error) ? null : throw Failure($"无法读取 {path}", error);
                    }

                    if (!Query(handle, FileAttributeTagInfo, out AttributeTagInfo tag))
                    {
                        throw Failure($"无法读取 {path}", Marshal.GetLastPInvokeError());
                    }

                    if ((tag.FileAttributes & AttributeReparsePoint) != 0 && (tag.ReparseTag & NameSurrogate) != 0)
                    {
                        return NodeKind.Unsupported;
                    }

                    attributes = tag.FileAttributes;
                }
            }

            return (attributes & AttributeDirectory) != 0 ? NodeKind.Directory : NodeKind.File;
        }

        internal static string? Identity(string path)
        {
            using (var handle = Open(Native(path)))
            {
                if (handle.IsInvalid || !Query(handle, FileBasicInfo, out BasicInfo basic))
                {
                    return null;
                }

                return FormatIdentity(handle, basic);
            }
        }

        private static string? FormatIdentity(SafeFileHandle handle, in BasicInfo basic)
        {
            // The 32-digit volume field keeps these IDs distinct from legacy 16-digit macOS mount IDs.
            string? id = null;
            if (Query(handle, FileIdInfo, out IdInfo info) && (info.IdLow | info.IdHigh) != 0)
            {
                id = $"{info.VolumeSerialNumber:X32}:{info.IdHigh:X16}{info.IdLow:X16}";
            }
            else if (GetFileInformationByHandle(handle, out var legacy) && (legacy.FileIndexHigh | legacy.FileIndexLow) != 0)
            {
                id = $"{legacy.VolumeSerialNumber:X32}:{legacy.FileIndexHigh:X8}{legacy.FileIndexLow:X8}";
            }

            return id == null ? null : $"{id}:{basic.CreationTime:X16}:{0:X8}";
        }

        internal static ContentVersion? GetContentVersion(string path)
        {
            using (var handle = Open(Native(path)))
            {
                if (handle.IsInvalid || !Query(handle, FileBasicInfo, out BasicInfo basic) || !Query(handle, FileStandardInfo, out StandardInfo standard) ||
                    basic.ChangeTime == 0 || !TracksChangeTime(handle))
                {
                    return null;
                }

                string? id = FormatIdentity(handle, basic);
                if (id == null)
                {
                    return null;
                }

                // Windows has no content generation counter. NTFS and ReFS update ChangeTime (100 ns) on every data or
                // metadata change, and ordinary programs cannot set it, so it stands in for one together with size and mtime.
                Span<byte> state = stackalloc byte[24];
                BinaryPrimitives.WriteInt64LittleEndian(state, basic.ChangeTime);
                BinaryPrimitives.WriteInt64LittleEndian(state[8..], basic.LastWriteTime);
                BinaryPrimitives.WriteInt64LittleEndian(state[16..], standard.EndOfFile);
                Span<byte> digest = stackalloc byte[32];
                SHA256.HashData(state, digest);
                uint generation = BinaryPrimitives.ReadUInt32LittleEndian(digest);
                return new ContentVersion(id, generation == 0 ? 1 : generation);
            }
        }

        private static unsafe bool TracksChangeTime(SafeFileHandle handle)
        {
            // FAT and exFAT have no change time and coarse write times; always hash files there.
            char* name = stackalloc char[32];
            if (!GetVolumeInformationByHandle(handle, null, 0, null, null, null, name, 32))
            {
                return false;
            }

            var fileSystem = new ReadOnlySpan<char>(name, 32);
            fileSystem = fileSystem[..Math.Max(0, fileSystem.IndexOf('\0'))];
            return fileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase) || fileSystem.Equals("ReFS", StringComparison.OrdinalIgnoreCase);
        }

        internal static DirectoryChangeResult ChangeDirectoryEntry(string path, bool create)
        {
            // Mirror O_NOFOLLOW_ANY: every ancestor must be a real directory. CreateDirectoryW never creates missing
            // parents or adopts an existing leaf; RemoveDirectoryW only removes an empty directory, but it would also
            // remove a directory link, so the leaf is checked first.
            string full = Path.GetFullPath(path);
            for (string? parent = Path.GetDirectoryName(full); parent != null; parent = Path.GetDirectoryName(parent))
            {
                if (Inspect(parent) != NodeKind.Directory)
                {
                    return DirectoryChangeResult.PathChanged;
                }
            }

            if (!create && Inspect(full) != NodeKind.Directory)
            {
                return DirectoryChangeResult.PathChanged;
            }

            string native = Native(full);
            if (create ? CreateDirectory(native, 0) : RemoveDirectory(native))
            {
                return DirectoryChangeResult.Applied;
            }

            int error = Marshal.GetLastPInvokeError();
            if (Missing(error) || error == ErrorDirectory || create && error is ErrorAlreadyExists or ErrorFileExists)
            {
                return DirectoryChangeResult.PathChanged;
            }

            if (!create && error == ErrorDirNotEmpty)
            {
                return DirectoryChangeResult.NotEmpty;
            }

            throw Failure(create ? "无法创建目录" : "无法删除目录", error);
        }

        internal static bool IsPortableName(string name)
        {
            if (name.Length == 0 || name.EndsWith(' ') || name.EndsWith('.') || name.AsSpan().IndexOfAny(InvalidNameChars) >= 0 ||
                name.Any(c => c < ' '))
            {
                return false;
            }

            // Device names stay reserved with any extension, e.g. "con.txt" or "COM1 .log".
            string stem = name.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
            return stem switch
            {
                "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" => false,
                _ => !(stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
                       (stem[3] is >= '0' and <= '9' or '¹' or '²' or '³'))
            };
        }
    }
}
