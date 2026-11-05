using System.Runtime.InteropServices;
using System.Text;
using Spotlight.Interop;

namespace Spotlight.Services;

/// <summary>
/// Tells whether a game (or any other fullscreen app) is in front, so the shortcut can stay out of its way.
/// Combines the two checks other launchers use:
///  - PowerToys: SHQueryUserNotificationState == QUNS_RUNNING_D3D_FULL_SCREEN (exclusive fullscreen games).
///  - Fluent Search / Flow Launcher: the foreground window covers its whole monitor (borderless fullscreen,
///    which is what most modern games use and which the first check misses).
/// </summary>
internal static class GameDetector
{
    // Desktop, taskbar and other shell windows can cover a whole screen but are never games.
    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
    };

    public static bool IsFullscreenAppInFront(IntPtr ownWindow)
    {
        if (Native.SHQueryUserNotificationState(out int state) == 0
            && state is Native.QUNS_RUNNING_D3D_FULL_SCREEN or Native.QUNS_PRESENTATION_MODE)
        {
            return true;
        }

        var hwnd = Native.GetForegroundWindow();
        if (hwnd == IntPtr.Zero || hwnd == ownWindow) return false;

        var name = new StringBuilder(64);
        Native.GetClassName(hwnd, name, name.Capacity);
        if (ShellClasses.Contains(name.ToString())) return false;

        // Ordinary windows with a title bar aren't fullscreen, even when maximized over an auto-hidden taskbar.
        long style = Native.GetWindowLongPtr(hwnd, Native.GWL_STYLE).ToInt64();
        if ((style & Native.WS_CAPTION) == Native.WS_CAPTION) return false;

        if (!Native.GetWindowRect(hwnd, out var window)) return false;
        var info = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetMonitorInfo(Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST), ref info)) return false;

        var screen = info.rcMonitor;
        return window.Left <= screen.Left && window.Top <= screen.Top
            && window.Right >= screen.Right && window.Bottom >= screen.Bottom;
    }
}
