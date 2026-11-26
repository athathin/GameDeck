using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GameDeck.Services;

/// <summary>
/// The game that is running (for Big Picture's "Now playing"): bringing its window back to the front, and asking it to close.
/// A game's programs are the ones running from its install folder (the same rule the playtime tracker uses).
/// Closing is the normal way, like clicking the window's X: the game may ask to save first. Only when the user then chooses
/// "Close anyway" (the game didn't close when asked) are its programs ended (<see cref="ForceCloseAsync"/>).
/// </summary>
public static class RunningGame
{
    // big games take a while to shut down (Rocket League: well over 8 seconds); only after this long is it "still running"
    private static readonly TimeSpan CloseWait = TimeSpan.FromSeconds(45);

    /// <summary>
    /// "Close anyway": ends the game's programs (only the ones running from its folder) without asking them. Unsaved progress
    /// is lost, so this only ever runs when the user chose it after the game didn't close when asked.
    /// </summary>
    /// <returns>True when they have all ended.</returns>
    public static async Task<bool> ForceCloseAsync(string folder)
    {
        var processes = await Task.Run(() => ProcessesIn(folder));
        try
        {
            foreach (var process in processes)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            }

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTime.UtcNow < deadline && processes.Any(p => !HasExited(p)))
                await Task.Delay(200);

            return processes.All(HasExited);
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }

    /// <summary>Brings the game's main window to the front (restoring it if it was minimized).</summary>
    /// <returns>False when no window of the game was found.</returns>
    public static bool BringToFront(string folder)
    {
        var window = FindMainWindow(folder);
        if (window == IntPtr.Zero) return false;

        WindowFocus.BringToFront(window);
        return true;
    }

    /// <summary>Asks every program of the game that has a window to close, and waits a few seconds.</summary>
    /// <returns>True when all of the game's programs have closed.</returns>
    public static async Task<bool> CloseAsync(string folder)
    {
        var processes = await Task.Run(() => ProcessesIn(folder));
        if (processes.Count == 0) return true;   // it had already closed

        try
        {
            // (helper programs without a window, such as a crash reporter, can't be asked: they end with the game, or not)
            var asked = new List<Process>();
            foreach (var process in processes)
            {
                try
                {
                    if (process.CloseMainWindow())   // the window's X: the game may ask to save first
                        asked.Add(process);
                }
                catch (InvalidOperationException) { }   // it has already gone
            }

            var deadline = DateTime.UtcNow + CloseWait;
            while (DateTime.UtcNow < deadline && asked.Any(p => !HasExited(p)))
                await Task.Delay(250);

            return asked.Count > 0 && asked.All(HasExited);
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }

    private static bool HasExited(Process process)
    {
        try { return process.HasExited; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    /// <summary>The running programs whose file is inside the folder.</summary>
    private static List<Process> ProcessesIn(string folder)
    {
        var prefix = folder.TrimEnd('\\') + "\\";
        var found = new List<Process>();

        foreach (var process in Process.GetProcesses())
        {
            if (PlaytimeTracker.GetImagePath(process.Id) is { } path && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                found.Add(process);
            else
                process.Dispose();
        }

        return found;
    }

    /// <summary>The biggest visible top-level window that belongs to a program inside the folder.</summary>
    private static IntPtr FindMainWindow(string folder)
    {
        var prefix = folder.TrimEnd('\\') + "\\";
        var paths = new Dictionary<uint, bool>();   // process id -> is it the game's (asked once per process)
        var best = IntPtr.Zero;
        long bestArea = -1;

        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window) || GetWindow(window, 4 /* GW_OWNER */) != IntPtr.Zero) return true;

            GetWindowThreadProcessId(window, out var pid);
            if (!paths.TryGetValue(pid, out var isGame))
                paths[pid] = isGame = PlaytimeTracker.GetImagePath((int)pid) is { } path &&
                                      path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

            if (!isGame || !GetWindowRect(window, out var rect)) return true;

            // a minimized window reports a tiny rectangle: it still counts, just after a real one
            long area = IsIconic(window) ? 0 : (long)(rect.Right - rect.Left) * (rect.Bottom - rect.Top);
            if (area > bestArea)
            {
                bestArea = area;
                best = window;
            }

            return true;
        }, IntPtr.Zero);

        return best;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
