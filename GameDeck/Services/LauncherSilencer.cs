using System.Diagnostics;
using System.Runtime.InteropServices;
using GameDeck.Models;

namespace GameDeck.Services;

/// <summary>
/// After a game is launched, minimizes the launcher window that pops up because of it
/// (Steam, Epic, Ubisoft Connect, EA app, Battle.net). Only windows that were not already
/// open before the launch are touched, so a launcher you opened yourself is left alone.
/// </summary>
public static class LauncherSilencer
{
    private const int ShowMinNoActive = 7; // minimize without taking focus away from the game

    private static readonly Dictionary<string, string[]> LauncherProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Steam"] = ["steam", "steamwebhelper"],
        ["Epic"] = ["EpicGamesLauncher"],
        ["Ubisoft"] = ["UbisoftConnect", "upc"],
        ["EA"] = ["EADesktop"],
        ["Battle.net"] = ["Battle.net"],
        // the Riot Client window is "Riot Client.exe" (in RiotClientElectron); older versions called it RiotClientUx
        ["Riot"] = ["Riot Client", "RiotClientUx"]
    };

    /// <summary>Call right BEFORE launching: remembers which launcher windows are already open.</summary>
    public static HashSet<IntPtr> Snapshot(Game game)
    {
        try
        {
            return LauncherProcesses.TryGetValue(game.Source, out var names)
                ? FindWindows(names)
                : new HashSet<IntPtr>();
        }
        catch
        {
            return new HashSet<IntPtr>();
        }
    }

    /// <summary>
    /// Call right AFTER launching. Checks a few times a second for 45 seconds and minimizes any
    /// launcher window that wasn't open before. Nothing runs after those 45 seconds.
    /// </summary>
    public static async Task MinimizeNewWindowsAsync(Game game, HashSet<IntPtr> alreadyOpen)
    {
        if (!LauncherProcesses.TryGetValue(game.Source, out var names)) return;

        try
        {
            var handled = new HashSet<IntPtr>(alreadyOpen);
            var until = DateTime.UtcNow.AddSeconds(45);

            while (DateTime.UtcNow < until)
            {
                await Task.Delay(400);

                foreach (var window in await Task.Run(() => FindWindows(names)))
                {
                    if (handled.Add(window))   // a window we haven't seen before
                        ShowWindow(window, ShowMinNoActive);
                }
            }
        }
        catch
        {
            // never let this break launching a game
        }
    }

    private static HashSet<IntPtr> FindWindows(string[] processNames)
    {
        var ids = new HashSet<uint>();
        foreach (var name in processNames)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                ids.Add((uint)process.Id);
                process.Dispose();
            }
        }

        var result = new HashSet<IntPtr>();
        if (ids.Count == 0) return result;

        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var processId);
            if (ids.Contains(processId) && IsWindowVisible(window) && !IsIconic(window) && GetWindowTextLength(window) > 0)
                result.Add(window);
            return true;
        }, IntPtr.Zero);

        return result;
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);
}