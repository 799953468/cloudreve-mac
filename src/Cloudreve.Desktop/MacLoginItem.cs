using System.Runtime.InteropServices;

namespace Cloudreve.Desktop;

internal enum LoginItemStatus : long
{
    Unavailable = -1,
    NotRegistered,
    Enabled,
    RequiresApproval,
    NotFound
}

internal interface ILoginItemService
{
    LoginItemStatus Status();
    string? SetRegistered(bool registered);
    void OpenSettings();
}

internal sealed partial class MacLoginItem : ILoginItemService
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
        if (!OperatingSystem.IsMacOSVersionAtLeast(13) ||
            !NativeLibrary.TryLoad("/System/Library/Frameworks/ServiceManagement.framework/ServiceManagement", out _))
        {
            return 0;
        }

        // A dotnet or test host is not the application we want macOS to launch.
        nint bundle = Send(GetClass("NSBundle"), Selector("mainBundle"));
        nint identifier = Send(bundle, Selector("bundleIdentifier"));
        return ReadString(identifier) == "io.github.cloudreve-mac.client" ? GetClass("SMAppService") : 0;
    }

    private static string? ReadString(nint value)
    {
        return Marshal.PtrToStringUTF8(Send(value, Selector("UTF8String")));
    }

    public LoginItemStatus Status()
    {
        nint service = ServiceClass.Value == 0 ? 0 : Send(ServiceClass.Value, Selector("mainAppService"));
        if (service == 0)
        {
            return LoginItemStatus.Unavailable;
        }

        return (LoginItemStatus)(long)Send(service, Selector("status"));
    }

    public string? SetRegistered(bool registered)
    {
        nint service = Send(ServiceClass.Value, Selector("mainAppService"));
        string action = registered ? "registerAndReturnError:" : "unregisterAndReturnError:";
        if (SetRegistration(service, Selector(action), out nint error) != 0)
        {
            return null;
        }

        return ReadString(Send(error, Selector("localizedDescription")));
    }

    public void OpenSettings()
    {
        SendVoid(ServiceClass.Value, Selector("openSystemSettingsLoginItems"));
    }
}
