using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Cloudreve.Sync;

internal static partial class LocalFile
{
    // Darwin implementation. getattrlist, mkdirat and unlinkat give the no-follow semantics the sync engine relies on.
    [SupportedOSPlatform("macos")]
    private static partial class Darwin
    {
        [LibraryImport("libSystem.B.dylib", EntryPoint = "fclonefileat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
        private static partial int CloneFile(SafeFileHandle source, int destinationDirectory, string destination, uint flags);

        internal static bool TryClone(FileStream source, string destination)
        {
            const int currentDirectory = -2; // AT_FDCWD on Darwin
            // Learn the ordinary creation mode on this volume without changing the process-wide umask.
            UnixFileMode mode;
            using (var probe = File.OpenHandle(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, FileOptions.DeleteOnClose))
            {
                mode = File.GetUnixFileMode(probe);
            }
            if (CloneFile(source.SafeFileHandle, currentDirectory, destination, 0) == 0)
            {
                using (var clone = File.OpenHandle(destination, FileMode.Open, FileAccess.Write, FileShare.None))
                {
                    File.SetUnixFileMode(clone, mode);
                    RandomAccess.FlushToDisk(clone);
                }
                return true;
            }

            int error = Marshal.GetLastPInvokeError();
            if (error is 18 or 45 or 78 or 102) // EXDEV, ENOTSUP, ENOSYS, EOPNOTSUPP
            {
                return false;
            }
            throw new IOException(Marshal.GetPInvokeErrorMessage(error), error);
        }

        [LibraryImport("libSystem.B.dylib", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
        private static partial SafeFileHandle OpenDirectory(string path, int flags);

        [LibraryImport("libSystem.B.dylib", EntryPoint = "mkdirat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
        private static partial int MakeChildDirectory(SafeFileHandle parent, string name, ushort mode);

        [LibraryImport("libSystem.B.dylib", EntryPoint = "unlinkat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
        private static partial int RemoveChildDirectory(SafeFileHandle parent, string name, int flags);

        internal static DirectoryChangeResult ChangeDirectoryEntry(string path, bool create)
        {
            // Pin the parent without traversing symlinks. mkdirat never recreates missing parents
            // or adopts an existing leaf; unlinkat with AT_REMOVEDIR only removes an empty directory.
            const int openFlags = 0x00100000 | 0x01000000 | 0x20000000; // O_DIRECTORY | O_CLOEXEC | O_NOFOLLOW_ANY
            const int removeDirectory = 0x0080; // AT_REMOVEDIR
            using (var parent = OpenDirectory(Path.GetDirectoryName(path)!, openFlags))
            {
                int result = -1;
                if (!parent.IsInvalid)
                {
                    string name = Path.GetFileName(path);
                    result = create ? MakeChildDirectory(parent, name, 0x1FF) : RemoveChildDirectory(parent, name, removeDirectory);
                }

                if (result == 0)
                {
                    return DirectoryChangeResult.Applied;
                }

                int error = Marshal.GetLastPInvokeError();
                if (error is 2 or 20 or 62 || create && error == 17) // ENOENT, ENOTDIR, ELOOP, EEXIST
                {
                    return DirectoryChangeResult.PathChanged;
                }

                if (!create && error == 66) // ENOTEMPTY
                {
                    return DirectoryChangeResult.NotEmpty;
                }

                throw new IOException(Marshal.GetPInvokeErrorMessage(error), error);
            }
        }

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

        // Darwin packs getattrlist result attributes on four-byte boundaries, including timespec and file ID.
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct IdentityBuffer
        {
            public uint Length, VolumeLow, VolumeHigh;
            public long CreatedSeconds, CreatedNanoseconds;
            public ulong FileId;
        }

        [InlineArray(16)]
        private struct UuidBytes
        {
            private byte first;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct VolumeBuffer
        {
            public uint Length, ReturnedCommon, ReturnedVolume, ReturnedDirectory, ReturnedFile, ReturnedFork;
            public uint VolumeLow, VolumeHigh;
            public UuidBytes Uuid;
        }

        [LibraryImport("libSystem.B.dylib", EntryPoint = "getattrlist", StringMarshalling = StringMarshalling.Utf8)]
        private static partial int ReadVolume(string path, ref AttributeList attributes, out VolumeBuffer result, nuint size, nuint options);

        internal static VolumeIdentity? ReadVolumeIdentity(string path)
        {
            const uint common = 0x80000000 | 0x00000004; // RETURNED_ATTRS | FSID
            const uint uuid = 0x00040000; // ATTR_VOL_UUID (cannot be requested together with ATTR_CMN_FILEID)
            var attributes = new AttributeList { Count = 5, Common = common, Volume = 0x80000000 | uuid };
            nuint size = (nuint)Marshal.SizeOf<VolumeBuffer>();
            if (ReadVolume(path, ref attributes, out var result, size, 1) != 0 || result.Length != size ||
                result.ReturnedCommon != common || (result.ReturnedVolume & uuid) == 0)
            {
                return null;
            }

            var volumeId = new Guid(result.Uuid, bigEndian: true);
            ulong mountId = ((ulong)result.VolumeHigh << 32) | result.VolumeLow;
            return volumeId == Guid.Empty ? null : new VolumeIdentity(mountId, volumeId);
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct ContentVersionBuffer
        {
            public uint Length, ReturnedCommon, ReturnedVolume, ReturnedDirectory, ReturnedFile, ReturnedFork;
            public uint VolumeLow, VolumeHigh;
            public long CreatedSeconds, CreatedNanoseconds;
            public uint Generation;
            public ulong FileId;
        }

        [LibraryImport("libSystem.B.dylib", EntryPoint = "getattrlist", StringMarshalling = StringMarshalling.Utf8)]
        private static partial int ReadContentVersion(string path, ref AttributeList attributes, out ContentVersionBuffer result, nuint size, nuint options);

        internal static ContentVersion? GetContentVersion(string path)
        {
            // Request FSID, CRTIME, GEN_COUNT and FILEID together; a generation is meaningful only for the same object.
            const uint common = 0x80000000 | 0x00000004 | 0x00000200 | 0x00080000 | 0x02000000;
            const uint options = 0x01 | 0x20; // FSOPT_NOFOLLOW | FSOPT_ATTR_CMN_EXTENDED
            var attributes = new AttributeList { Count = 5, Common = common };
            nuint size = (nuint)Marshal.SizeOf<ContentVersionBuffer>();
            if (ReadContentVersion(path, ref attributes, out var result, size, options) != 0 || result.Length != size ||
                result.ReturnedCommon != common || result.FileId == 0 || result.Generation == 0)
            {
                return null;
            }

            string? id = FormatIdentity(path, result.VolumeHigh, result.VolumeLow, result.FileId, result.CreatedSeconds, result.CreatedNanoseconds);
            return id == null ? null : new ContentVersion(id, result.Generation);
        }

        private static string? FormatIdentity(string path, uint volumeHigh, uint volumeLow, ulong fileId, long createdSeconds, long createdNanoseconds)
        {
            ulong mountId = ((ulong)volumeHigh << 32) | volumeLow;
            var volume = ReadVolumeIdentity(path);
            if (volume.HasValue && volume.Value.MountId != mountId)
            {
                return null;
            }

            // FSID identifies a mount, not a disk. Only fall back to it on volumes without a UUID.
            string volumeId = volume?.Uuid.ToString("N") ?? mountId.ToString("X16");
            return $"{volumeId}:{fileId:X16}:{createdSeconds:X16}:{createdNanoseconds:X8}";
        }

        [LibraryImport("libSystem.B.dylib", EntryPoint = "getattrlist", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
        private static partial int ReadIdentity(string path, ref AttributeList attributes, out IdentityBuffer result, nuint size, nuint options);

        internal static string? Identity(string path)
        {
            var attributes = new AttributeList { Count = 5, Common = 0x00000004 | 0x00000200 | 0x02000000 };
            nuint size = (nuint)Marshal.SizeOf<IdentityBuffer>();
            if (ReadIdentity(path, ref attributes, out var result, size, 1) != 0 || result.Length != size || result.FileId == 0)
            {
                return null;
            }

            return FormatIdentity(path, result.VolumeHigh, result.VolumeLow, result.FileId, result.CreatedSeconds, result.CreatedNanoseconds);
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
}
