using System.IO;
using GameDeck.Models;

namespace GameDeck.Services;

/// <summary>
/// Decides which programs a launch routine may never close: Windows itself, game launchers (the game needs them)
/// and GameDeck. Used both when choosing apps and again right before closing, so a hand-edited file can't bypass it.
/// </summary>
public static class ProcessGuard
{
    private static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // Windows
        "explorer", "dwm", "winlogon", "csrss", "services", "lsass", "svchost", "smss", "wininit", "sihost",
        "ctfmon", "fontdrvhost", "RuntimeBroker", "audiodg", "ApplicationFrameHost", "TextInputHost",
        "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "taskmgr",
        // GameDeck
        "GameDeck", "GameDeckBeta",
        // game launchers: closing them before the game starts would break it
        "steam", "steamwebhelper", "steamservice", "EpicGamesLauncher", "GalaxyClient", "upc", "UbisoftConnect",
        "RiotClientServices", "RiotClientUx", "vgc", "Battle.net", "EADesktop", "EALauncher", "Origin",
        "RockstarLauncher", "Launcher", "XboxPcApp", "GamingServices"
    };

    /// <summary>True for system programs, game launchers and GameDeck.</summary>
    public static bool IsProtected(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return true;

        var name = Path.GetFileNameWithoutExtension(exePath);
        if (ProtectedNames.Contains(name)) return true;

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";
        if (exePath.StartsWith(windows, StringComparison.OrdinalIgnoreCase)) return true;

        return string.Equals(exePath, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when the program is the game itself (or lives in its folder): it must never be closed by its own routine.</summary>
    public static bool IsTheGame(Game game, string exePath)
    {
        if (string.Equals(exePath, game.LaunchTarget, StringComparison.OrdinalIgnoreCase)) return true;

        var folder = game.TrackingFolder;
        return folder is not null &&
               exePath.StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
    }
}
