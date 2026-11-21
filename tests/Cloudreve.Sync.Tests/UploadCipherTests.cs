using System.Text.Json;
using Cloudreve.Desktop;
using Xunit;

namespace Cloudreve.Sync.Tests;

public sealed class UploadCipherTests
{
    // NIST SP 800-38A, F.5.5: a known answer independent of this implementation.
    private const string Key = "603deb1015ca71be2b73aef0857d77811f352c073b6108d72d9810a30914dff4";
    private const string Iv = "f0f1f2f3f4f5f6f7f8f9fafbfcfdfeff";
    private const string Plain = "6bc1bee22e409f96e93d7e117393172aae2d8a571e03ac9c9eb76fac45af8e51";
    private const string Encrypted = "601ec313775789a5b7a7f504bbf3d228f443e3ca4d62b59aca84e990cacaf5c5";

    [Theory]
    [InlineData(0, 32)]
    [InlineData(1, 14)]
    [InlineData(5, 23)]
    [InlineData(16, 16)]
    [InlineData(31, 1)]
    public void EncryptsAnySliceIndependently(int offset, int length)
    {
        var ticket = Ticket(UploadCipher.Algorithm, Convert.FromHexString(Key), Convert.FromHexString(Iv));
        using (var cipher = UploadCipher.FromTicket(ticket))
        {
            byte[] plain = Convert.FromHexString(Plain);
            byte[] expected = Convert.FromHexString(Encrypted);
            cipher!.Apply(plain.AsSpan(offset, length), offset);
            Assert.Equal(expected.AsSpan(offset, length).ToArray(), plain.AsSpan(offset, length).ToArray());
        }
    }

    [Fact]
    public void RejectsUnknownAlgorithmsAndInvalidKeyMaterial()
    {
        Assert.Throws<NotSupportedException>(() => UploadCipher.FromTicket(Ticket("unknown", new byte[32], new byte[16])));
        Assert.Throws<InvalidDataException>(() => UploadCipher.FromTicket(Ticket(UploadCipher.Algorithm, new byte[16], new byte[16])));
        Assert.Throws<InvalidDataException>(() => UploadCipher.FromTicket(Ticket(UploadCipher.Algorithm, new byte[32], new byte[15])));
        Assert.Null(UploadCipher.FromTicket(JsonSerializer.SerializeToElement(new { })));
    }

    private static JsonElement Ticket(string algorithm, byte[] key, byte[] iv)
    {
        return JsonSerializer.SerializeToElement(new { encrypt_metadata = new { algorithm, key_plain_text = Convert.ToBase64String(key), iv = Convert.ToBase64String(iv) } });
    }
}