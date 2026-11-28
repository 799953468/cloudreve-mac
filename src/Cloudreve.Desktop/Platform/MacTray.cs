using System.Runtime.InteropServices;

namespace Cloudreve.Desktop;

// Values cross the native boundary and also order concurrent drive states.
internal enum TrayState
{
    Idle = 0,
    Disconnected = 1,
    Scanning = 2,
    Syncing = 3
}

internal static partial class MacTray
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void Command(int action);

    [LibraryImport("CloudreveTray", EntryPoint = "cloudreve_tray_start", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void Start(Command callback, string show, string add, string settings, string quit);

    [LibraryImport("CloudreveTray", EntryPoint = "cloudreve_tray_update", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void Update(TrayState state, string description);

    [LibraryImport("CloudreveTray", EntryPoint = "cloudreve_tray_position")]
    internal static partial void Position(nint window);

    [LibraryImport("CloudreveTray", EntryPoint = "cloudreve_tray_stop")]
    internal static partial void Stop();
}
