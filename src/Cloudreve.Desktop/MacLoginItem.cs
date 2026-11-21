using System.Runtime.InteropServices;

namespace Cloudreve.Desktop;

internal enum LoginItemStatus : long
{
    NotRegistered,
    Enabled,
    RequiresApproval,
    NotFound
}

internal static partial class MacLoginItem
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private static readonly Lazy<nint> ServiceClass = new(LoadClass);

    [LibraryImport(ObjC, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint GetClass(string name);

    [LibraryImport(ObjC, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint Selector(string name);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint Send(nint receiver, nint selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial byte SetRegistration(nint receiver, nint selector, out nint error);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial void SendVoid(nint receiver, nint selector);

    private static nint LoadClass()
    {
        string? executable = Environment.ProcessPath;
        if (!OperatingSystem.IsMacOSVersionAtLeast(13) || executable == null || !executable.EndsWith(".app/Contents/MacOS/CloudreveMac", StringComparison.Ordinal) ||
            !NativeLibrary.TryLoad("/System/Library/Frameworks/ServiceManagement.framework/ServiceManagement", out _))
        {
            return 0;
        }

        return GetClass("SMAppService");
    }

    internal static LoginItemStatus Status()
    {
        nint service = ServiceClass.Value == 0 ? 0 : Send(ServiceClass.Value, Selector("mainAppService"));
        if (service == 0)
        {
            return LoginItemStatus.NotFound;
        }

        return (LoginItemStatus)(long)Send(service, Selector("status"));
    }

    internal static void Toggle()
    {
        var status = Status();
        if (status == LoginItemStatus.RequiresApproval)
        {
            SendVoid(ServiceClass.Value, Selector("openSystemSettingsLoginItems"));
            return;
        }

        if (status is not (LoginItemStatus.Enabled or LoginItemStatus.NotRegistered))
        {
            throw new IOException(UiText.Get("请在 macOS 13 或更高版本从已安装的 Cloudreve.app 管理登录项。"));
        }

        nint service = Send(ServiceClass.Value, Selector("mainAppService"));
        string action = status == LoginItemStatus.Enabled ? "unregisterAndReturnError:" : "registerAndReturnError:";
        if (SetRegistration(service, Selector(action), out _) == 0 && Status() != LoginItemStatus.RequiresApproval)
        {
            throw new IOException(UiText.Get("macOS 未更改登录项，请在系统设置的登录项中检查。"));
        }
    }
}