using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace GameDeck.Services;

/// <summary>
/// Brings a window to the front even when GameDeck isn't the program being used. Windows normally refuses that
/// (the taskbar button just flashes), which matters when a controller button opens Big Picture mode.
/// </summary>
public static class WindowFocus
{
    public static void BringToFront(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero)
            BringToFront(handle, () => window.Activate());
    }

    /// <summary>Brings any window to the front (another program's too: a running game's), restoring it if it was minimized.</summary>
    public static void BringToFront(IntPtr window, Action? whileAttached = null)
    {
        if (IsIconic(window))
            ShowWindow(window, 9 /* SW_RESTORE */);

        var foreground = GetForegroundWindow();
        var foregroundThread = foreground == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foreground, out _);
        var thisThread = GetCurrentThreadId();

        // sharing the input queue of the program in front makes Windows accept the focus change
        var attached = foregroundThread != 0 && foregroundThread != thisThread &&
                       AttachThreadInput(thisThread, foregroundThread, true);
        try
        {
            SetForegroundWindow(window);
            whileAttached?.Invoke();
        }
        finally
        {
            if (attached)
                AttachThreadInput(thisThread, foregroundThread, false);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, bool attachFlag);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);
}
