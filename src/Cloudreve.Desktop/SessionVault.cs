using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cloudreve.Desktop;

internal static partial class SessionVault
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string Service = "io.github.cloudreve-mac.client.oauth";
    private const string Account = "active-drive";
    private const int NotFound = -25300;

    [LibraryImport(Security, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int SecKeychainFindGenericPassword(nint chain, uint slen, string service, uint alen, string account, out uint size, out nint bytes, out nint item);

    [LibraryImport(Security, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int SecKeychainAddGenericPassword(nint chain, uint slen, string service, uint alen, string account, uint size, byte[] bytes, nint item);

    [LibraryImport(Security)]
    private static partial int SecKeychainItemModifyAttributesAndData(nint item, nint attributes, uint size, byte[] bytes);

    [LibraryImport(Security)]
    private static partial int SecKeychainItemFreeContent(nint attributes, nint bytes);

    [LibraryImport(Security)]
    private static partial int SecKeychainItemDelete(nint item);

    [LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static partial void CFRelease(nint item);

    private static T WithItem<T>(Func<nint, nint, int, T> action)
    {
        int status = SecKeychainFindGenericPassword(0, (uint)Service.Length, Service, (uint)Account.Length, Account, out uint size, out nint bytes, out nint item);
        if (status == NotFound)
        {
            return action(0, 0, 0);
        }

        Check(status);
        try
        {
            return action(item, bytes, checked((int)size));
        }
        finally
        {
            SecKeychainItemFreeContent(0, bytes);
            CFRelease(item);
        }
    }

    internal static DriveSession? Load()
    {
        return WithItem<DriveSession?>((item, bytes, size) =>
        {
            if (item == 0)
            {
                return null;
            }

            var copy = new byte[size];
            try
            {
                Marshal.Copy(bytes, copy, 0, size);
                return JsonSerializer.Deserialize<DriveSession>(copy) ?? throw new InvalidDataException("钥匙串中的会话无效。");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(copy);
            }
        });
    }

    internal static void Save(DriveSession session)
    {
        byte[] copy = JsonSerializer.SerializeToUtf8Bytes(session);
        try
        {
            Check(WithItem((item, _, _) => item == 0
                ? SecKeychainAddGenericPassword(0, (uint)Service.Length, Service, (uint)Account.Length, Account, (uint)copy.Length, copy, 0)
                : SecKeychainItemModifyAttributesAndData(item, 0, (uint)copy.Length, copy)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(copy);
        }
    }

    internal static void Forget()
    {
        Check(WithItem((item, _, _) => item == 0 ? 0 : SecKeychainItemDelete(item)));
    }

    private static void Check(int status)
    {
        if (status != 0)
        {
            throw new IOException($"无法访问 macOS 钥匙串（{status}）。");
        }
    }
}