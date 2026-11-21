using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace Cloudreve.Desktop;

internal sealed class UploadCipher : IDisposable
{
    internal const string Algorithm = "aes-256-ctr";
    private const int BlockSize = 16;
    private readonly Aes aes;
    private readonly UInt128 initialCounter;

    private UploadCipher(byte[] key, byte[] iv)
    {
        initialCounter = BinaryPrimitives.ReadUInt128BigEndian(iv);
        aes = Aes.Create();
        aes.Key = key;
    }

    internal static UploadCipher? FromTicket(JsonElement ticket)
    {
        if (!ticket.TryGetProperty("encrypt_metadata", out var metadata) || metadata.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (metadata.ValueKind != JsonValueKind.Object || DesktopLogin.Required(metadata, "algorithm") != Algorithm)
        {
            throw new NotSupportedException(UiText.Get("站点要求的加密算法不受支持；未发送文件内容。"));
        }

        byte[] key = new byte[32];
        byte[] iv = new byte[BlockSize];
        try
        {
            if (!Convert.TryFromBase64String(DesktopLogin.Required(metadata, "key_plain_text"), key, out int keySize) || keySize != key.Length ||
                !Convert.TryFromBase64String(DesktopLogin.Required(metadata, "iv"), iv, out int ivSize) || ivSize != iv.Length)
            {
                throw new InvalidDataException(UiText.Get("上传加密参数无效；未发送文件内容。"));
            }

            return new UploadCipher(key, iv);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(iv);
        }
    }

    internal void Apply(Span<byte> data, long fileOffset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fileOffset);
        if (data.IsEmpty)
        {
            return;
        }

        // Every read, including a retried or unaligned slice, starts at its absolute file offset.
        int skip = (int)(fileOffset % BlockSize);
        int length = checked((skip + data.Length + BlockSize - 1) / BlockSize * BlockSize);
        byte[] counters = ArrayPool<byte>.Shared.Rent(length);
        byte[] stream = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            UInt128 counter = unchecked(initialCounter + (ulong)(fileOffset / BlockSize));
            for (int i = 0; i < length; i += BlockSize)
            {
                BinaryPrimitives.WriteUInt128BigEndian(counters.AsSpan(i, BlockSize), counter);
                counter = unchecked(counter + 1);
            }

            aes.EncryptEcb(counters.AsSpan(0, length), stream.AsSpan(0, length), PaddingMode.None);
            for (int i = 0; i < data.Length; i++)
            {
                data[i] ^= stream[skip + i];
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(counters, clearArray: true);
            ArrayPool<byte>.Shared.Return(stream, clearArray: true);
        }
    }

    public void Dispose()
    {
        aes.Dispose();
    }
}