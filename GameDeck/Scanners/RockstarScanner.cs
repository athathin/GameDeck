using System.IO;
using GameDeck.Models;
using GameDeck.Services;
using Microsoft.Win32;

namespace GameDeck.Scanners;

/// <summary>
/// Finds Rockstar Games Launcher games (GTA V, Red Dead Redemption 2, ...). They are registered in Windows'
/// "installed programs" list with the publisher "Rockstar Games"; Rockstar's own registry key
/// (HKLM\SOFTWARE\WOW6432Node\Rockstar Games\&lt;game&gt;\InstallFolder) fills in any game missing there.
/// </summary>
public class RockstarScanner : IGameScanner
{
    public List<Game> Scan()
    {
        var games = new UninstallRegistryScanner("Rockstar", IsGame, FindExe).Scan();

        // second source: Rockstar's own key (only for folders the first source didn't already find)
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32).OpenSubKey(@"SOFTWARE\Rockstar Games");
        if (root is null) return games;

        foreach (var name in root.GetSubKeyNames())
        {
            if (!IsGame(name, "Rockstar Games")) continue;

            using var key = root.OpenSubKey(name);
            var dir = (key?.GetValue("InstallFolder") as string)?.Trim().Trim('"').TrimEnd('\\');
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;
            if (games.Any(g => string.Equals(g.InstallDir?.TrimEnd('\\'), dir, StringComparison.OrdinalIgnoreCase))) continue;

            var exe = FindExe(dir);
            if (exe is null) continue;

            games.Add(new Game { Name = name, Source = "Rockstar", LaunchTarget = exe, InstallDir = dir });
        }

        return games;
    }

    /// <summary>The Rockstar Games Launcher and Social Club are programs, not games.</summary>
    private static bool IsGame(string name, string publisher) =>
        publisher.Contains("Rockstar Games", StringComparison.OrdinalIgnoreCase) &&
        !name.Contains("Launcher", StringComparison.OrdinalIgnoreCase) &&
        !name.Contains("Social Club", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Rockstar games ship a "Play&lt;Game&gt;.exe" (PlayGTAV.exe, ...) that starts the Rockstar Games Launcher first.
    /// Starting the big game .exe directly doesn't work, so prefer the Play exe.
    /// </summary>
    private static string? FindExe(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "Play*.exe").FirstOrDefault() ?? ExeFinder.FindMainExe(dir);
        }
        catch
        {
            return null;
        }
    }
}
