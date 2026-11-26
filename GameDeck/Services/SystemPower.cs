using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GameDeck.Services;

/// <summary>Puts the PC to sleep, turns it off or restarts it (the Power menu of Big Picture mode).</summary>
public static class SystemPower
{
    /// <summary>Sleep. Runs on a background thread because the call only returns once the PC wakes up again.</summary>
    public static void Suspend() => Task.Run(() => SetSuspendState(false, false, false));

    /// <summary>
    /// Turns the PC off now. Programs are asked to close normally (no forcing), so one with unsaved work can still
    /// stop the shutdown and let you save.
    /// </summary>
    public static void TurnOff() => RunShutdown("/s /t 0");

    public static void Restart() => RunShutdown("/r /t 0");

    private static void RunShutdown(string arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo("shutdown", arguments) { UseShellExecute = false, CreateNoWindow = true });
        }
        catch
        {
            // nothing sensible to do: the PC simply stays on
        }
    }

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);
}
