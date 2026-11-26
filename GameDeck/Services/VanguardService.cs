using System.ComponentModel;
using System.Diagnostics;
using GameDeck.Models;
using GameDeck.Scanners;

namespace GameDeck.Services;

/// <summary>
/// Riot Vanguard (anti-cheat) only has to run while a Riot game is being played, so GameDeck starts its
/// background service (vgc) right before launching a Riot game instead of asking the user to do it.
/// </summary>
public static class VanguardService
{
    private const string ServiceName = "vgc";

    // sc.exe exit codes
    private const int AlreadyRunning = 1056;
    private const int AccessDenied = 5;
    private const int UserDeclinedPrompt = 1223;   // Win32 error when the admin (UAC) prompt is cancelled

    /// <summary>Legends of Runeterra has no anti-cheat, so it never needs Vanguard.</summary>
    public static bool IsNeededBy(Game game) =>
        game.Source == "Riot" &&
        !game.LaunchTarget.StartsWith(RiotScanner.LaunchPrefix + "bacon/", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when Vanguard's background service (vgc.exe) is running.</summary>
    public static bool IsRunning()
    {
        try
        {
            var processes = Process.GetProcessesByName(ServiceName);
            var running = processes.Length > 0;

            foreach (var process in processes)
                process.Dispose();

            return running;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Starts Vanguard if it isn't running and waits (up to ~10 s) until it is. Tries without admin rights first
    /// and only shows Windows' admin prompt if the service can't be started otherwise.
    /// Returns false only when the user cancelled that admin prompt (so the launch should be cancelled too).
    /// If Vanguard isn't installed or can't be started it returns true: the Riot Client then deals with it itself.
    /// </summary>
    public static async Task<bool> EnsureRunningAsync()
    {
        if (IsRunning()) return true;

        try
        {
            var code = await Task.Run(() => StartService(elevated: false));
            if (code == AccessDenied)
                code = await Task.Run(() => StartService(elevated: true));

            if (code is 0 or AlreadyRunning)
            {
                for (var i = 0; i < 20 && !IsRunning(); i++)
                    await Task.Delay(500);
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == UserDeclinedPrompt)
        {
            return false;
        }
        catch
        {
            // couldn't start it: carry on and let the Riot Client sort it out
        }

        return true;
    }

    /// <summary>Runs "sc start vgc" and returns its exit code (0 = started, 1060 = Vanguard isn't installed...).</summary>
    private static int StartService(bool elevated)
    {
        var info = new ProcessStartInfo("sc.exe", $"start {ServiceName}")
        {
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        if (elevated)
        {
            info.UseShellExecute = true;
            info.Verb = "runas";
        }
        else
        {
            info.UseShellExecute = false;
            info.RedirectStandardOutput = true;
        }

        using var process = Process.Start(info)!;
        if (!process.WaitForExit(15_000)) return -1;
        return process.ExitCode;
    }
}
