using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Win32.SafeHandles;

namespace Cloudreve.Desktop;

// Read by offset so an authentication retry can send the same slice again.
internal sealed class FileSliceContent : HttpContent
{
    private readonly SafeFileHandle file;
    private readonly long offset;
    private readonly long length;
    private readonly UploadCipher? cipher;
    private readonly SyncActivity? activity;

    internal FileSliceContent(SafeFileHandle file, long offset, long length, UploadCipher? cipher = null, SyncActivity? activity = null)
    {
        this.file = file;
        this.offset = offset;
        this.length = length;
        this.cipher = cipher;
        this.activity = activity;
        Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        Headers.ContentLength = length;
    }

    protected override bool TryComputeLength(out long result)
    {
        result = length;
        return true;
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        return SerializeToStreamAsync(stream, context, CancellationToken.None);
    }

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancel)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            long sent = 0;
            while (sent < length)
            {
                if (activity != null)
                {
                    await activity.WaitForResumeAsync(cancel);
                }

                int wanted = (int)Math.Min(buffer.Length, length - sent);
                int read = await RandomAccess.ReadAsync(file, buffer.AsMemory(0, wanted), offset + sent, cancel);
                if (read == 0)
                {
                    throw new EndOfStreamException(UiText.Get("上传期间源文件被截短。"));
                }

                cipher?.Apply(buffer.AsSpan(0, read), offset + sent);
                await stream.WriteAsync(buffer.AsMemory(0, read), cancel);
                sent += read;
                activity?.ReportBytes(offset + sent);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }
}