using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace GameDeck.Services;

/// <summary>Puts a window on the same screen as another one (Big Picture opens where the desktop window is).</summary>
public static class ScreenPlacement
{
    /// <summary>
    /// Before <paramref name="window"/> is first shown: moves it to the top-left of the work area of the screen
    /// <paramref name="other"/> is on, so a maximized window fills that screen. Works in the screens' own pixels, so screens
    /// with different scaling are no problem. Does nothing when that screen can't be found.
    /// </summary>
    public static void OpenOnScreenOf(Window window, Window other)
    {
        var otherHandle = new WindowInteropHelper(other).Handle;
        if (otherHandle == IntPtr.Zero) return;

        window.SourceInitialized += (_, _) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(MonitorFromWindow(otherHandle, MonitorDefaultToNearest), ref info)) return;

            // (the window isn't on screen yet: WPF shows it maximized afterwards, on the screen it now sits on)
            SetWindowPos(new WindowInteropHelper(window).Handle, IntPtr.Zero, info.Work.Left, info.Work.Top, 0, 0,
                         SwpNoSize | SwpNoZOrder | SwpNoActivate);
        };
    }

    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoSize = 0x0001, SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
