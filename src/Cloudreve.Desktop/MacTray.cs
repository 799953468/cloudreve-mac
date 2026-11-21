using System.Runtime.InteropServices;

namespace Cloudreve.Desktop;

internal static partial class MacTray
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void Command(int action);

    [LibraryImport("CloudreveTray", EntryPoint = "cloudreve_tray_start", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void Start(Command callback, string show, string add, string settings, string quit);

    [LibraryImport("CloudreveTray", EntryPoint = "cloudreve_tray_position")]
    internal static partial void Position(nint window);

    [LibraryImport("CloudreveTray", EntryPoint = "cloudreve_tray_stop")]
    internal static partial void Stop();
}