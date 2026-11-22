using System.Buffers;
using System.Security.Cryptography;
using Cloudreve.Sync;

namespace Cloudreve.Desktop;

// Flush the bytes before publishing their length and SHA-256. A crash can leave an uncommitted tail.
internal sealed class DownloadPartial : IDisposable
{
    private const long CheckpointInterval = 4 * 1024 * 1024;
    private readonly TransferFiles files;
    private long savedLength;
    internal FileStream Stream { get; }
    internal IncrementalHash Hash { get; }
    internal bool Complete { get; private set; }

    internal DownloadPartial(TransferFiles files)
    {
        this.files = files;
        if (MacFile.Inspect(files.PartPath) is not (null or NodeKind.File))
        {
            throw new IOException(UiText.Get("传输缓存不是普通文件。"));
        }

        var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None, Options = FileOptions.Asynchronous };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        Stream = new FileStream(files.PartPath, options);
        Hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    }

    internal async Task RestoreAsync(long size, CancellationToken cancel)
    {
        var state = TransferQueue.ReadCache(files.DownloadPath, DesktopJson.Default.DownloadCheckpoint);
        if (state != null && state.Length >= 0 && state.Length <= Stream.Length && state.Length <= size && state.Complete == (state.Length == size))
        {
            Stream.SetLength(state.Length);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(81920);
            try
            {
                int read;
                while ((read = await Stream.ReadAsync(buffer, cancel)) != 0)
                {
                    Hash.AppendData(buffer, 0, read);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }

            if (Convert.ToHexString(Hash.GetCurrentHash()) == state.Hash)
            {
                savedLength = state.Length;
                Complete = state.Complete;
                return;
            }
        }

        Reset();
    }

    internal void Reset()
    {
        File.Delete(files.DownloadPath);
        Stream.SetLength(0);
        Stream.Position = 0;
        Hash.GetHashAndReset();
        savedLength = 0;
        Complete = false;
    }

    internal void Save(bool complete = false, bool force = false)
    {
        long length = Stream.Position;
        if (!complete && (length == savedLength || !force && length - savedLength < CheckpointInterval))
        {
            return;
        }

        Stream.Flush(true);
        var checkpoint = new DownloadCheckpoint(length, Convert.ToHexString(Hash.GetCurrentHash()), complete);
        FolderPairStore.WriteJson(files.DownloadPath, checkpoint, DesktopJson.Default.DownloadCheckpoint);
        savedLength = length;
        Complete = complete;
    }

    public void Dispose()
    {
        Hash.Dispose();
        Stream.Dispose();
    }
}
