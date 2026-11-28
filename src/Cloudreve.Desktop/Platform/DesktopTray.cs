using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Microsoft.Win32;

namespace Cloudreve.Desktop;

// Tray actions, in menu order. Values cross the native boundary on macOS.
internal enum TrayAction
{
    Show = 0,
    Add = 1,
    Settings = 2,
    Quit = 3
}

// macOS uses the native status item in Native/MacTray.m; Windows uses Avalonia's notification-area icon.
internal static class DesktopTray
{
    private static MacTray.Command? macCallback;
    private static WindowsTray? windows;

    internal static void Start(Application app, Action<TrayAction> action, string show, string add, string settings, string quit)
    {
        if (OperatingSystem.IsWindows())
        {
            windows = new WindowsTray(app, action, show, add, settings, quit);
            return;
        }

        // Keep the delegate rooted for as long as native code may call it.
        macCallback = code => action((TrayAction)code);
        MacTray.Start(macCallback, show, add, settings, quit);
    }

    internal static void Update(TrayState state, string description)
    {
        if (OperatingSystem.IsWindows())
        {
            windows?.Update(state, description);
        }
        else if (macCallback != null)
        {
            MacTray.Update(state, description);
        }
    }

    // Place the activity popup next to the tray icon.
    internal static void Position(Window window)
    {
        if (OperatingSystem.IsWindows())
        {
            WindowsTray.Position(window);
        }
        else if (macCallback != null && window.TryGetPlatformHandle() is { HandleDescriptor: "NSWindow" or "NSView" } handle)
        {
            MacTray.Position(handle.Handle);
        }
    }

    internal static void Stop()
    {
        if (OperatingSystem.IsWindows())
        {
            windows?.Dispose();
            windows = null;
        }
        else if (macCallback != null)
        {
            MacTray.Stop();
            macCallback = null;
        }
    }
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsTray : IDisposable
{
    private const double Size = 32;
    private readonly TrayIcon icon;
    private readonly Dictionary<(TrayState, bool), WindowIcon> icons = [];

    internal WindowsTray(Application app, Action<TrayAction> action, string show, string add, string settings, string quit)
    {
        var menu = new NativeMenu();
        void Item(string text, TrayAction code)
        {
            var item = new NativeMenuItem(text);
            item.Click += (_, _) => action(code);
            menu.Add(item);
        }

        Item(show, TrayAction.Show);
        Item(add, TrayAction.Add);
        Item(settings, TrayAction.Settings);
        menu.Add(new NativeMenuItemSeparator());
        Item(quit, TrayAction.Quit);
        icon = new TrayIcon { Menu = menu, Icon = Render(TrayState.Disconnected), ToolTipText = "Cloudreve", IsVisible = true };
        icon.Clicked += (_, _) => action(TrayAction.Show);
        TrayIcon.SetIcons(app, [icon]);
    }

    internal void Update(TrayState state, string description)
    {
        icon.Icon = Render(state);
        icon.ToolTipText = description;
    }

    public void Dispose()
    {
        icon.IsVisible = false;
        icon.Dispose();
    }

    internal static void Position(Window window)
    {
        // The notification area sits at the end of the taskbar; the working area already excludes the taskbar.
        if (window.Screens.Primary is not { } screen)
        {
            return;
        }

        var area = screen.WorkingArea;
        var size = PixelSize.FromSize(window.Bounds.Size, screen.Scaling);
        int margin = (int)(12 * screen.Scaling);
        window.Position = new PixelPoint(Math.Max(area.X, area.Right - size.Width - margin), Math.Max(area.Y, area.Bottom - size.Height - margin));
    }

    private WindowIcon Render(TrayState state)
    {
        bool light = LightTaskbar();
        if (icons.TryGetValue((state, light), out var cached))
        {
            return cached;
        }

        // Same outline cloud and badges as the macOS template image (22x20 design grid).
        var cloud = Geometry.Parse("M6.1,15.7 C0.4,15.7 0.1,7.6 5.8,7.1 C7.3,1.2 15.5,1.2 17,7.5 C22.5,8 22.2,15.7 16.8,15.7 Z");
        string? badgePath = state switch
        {
            TrayState.Scanning => "M10.7,12.3 A3.3,3.3 0 1 1 17.3,12.3 A3.3,3.3 0 1 1 10.7,12.3 Z M16.5,14.8 L20,18.3",
            TrayState.Syncing => "M12,18 L12,10.4 M9.7,12.7 L12,10.4 L14.3,12.7 M18,10.4 L18,18 M15.7,15.7 L18,18 L20.3,15.7",
            TrayState.Disconnected => "M4.1,2.7 L18.6,17.9",
            _ => null
        };
        Geometry Stroke(Geometry geometry, double width)
        {
            return geometry.GetWidenedGeometry(new Pen(Brushes.Black, width, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round))!;
        }

        Geometry glyph = Stroke(cloud, 1.6);
        if (badgePath != null)
        {
            // Leave a transparent gap wherever the badge crosses the cloud.
            var badge = Geometry.Parse(badgePath);
            glyph = new CombinedGeometry(GeometryCombineMode.Union,
                new CombinedGeometry(GeometryCombineMode.Exclude, glyph, Stroke(badge, 4.1)), Stroke(badge, 1.6));
        }

        var bitmap = new RenderTargetBitmap(new PixelSize((int)Size, (int)Size), new Vector(96, 96));
        using (var context = bitmap.CreateDrawingContext())
        {
            double scale = Size / 22;
            using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(0, (Size - 20 * scale) / 2)))
            {
                context.DrawGeometry(light ? Brushes.Black : Brushes.White, null, glyph);
            }
        }

        var result = new WindowIcon(bitmap);
        icons[(state, light)] = result;
        return result;
    }

    private static bool LightTaskbar()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
        {
            return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
        }
    }
}
