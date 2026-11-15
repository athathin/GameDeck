using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using GameDeck.Models;
using GameDeck.Scanners;
using GameDeck.Views;
using Microsoft.Win32;

namespace GameDeck.Services;

public static class LaunchService
{
    /// <summary>Starts the game. Returns true if it was started.</summary>
    public static bool Launch(Game game)
    {
        try
        {
            // Riot games: start them through the Riot Client, telling it which product to open
            if (game.LaunchTarget.StartsWith(RiotScanner.LaunchPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var client = RiotScanner.FindClientExe()
                             ?? throw new FileNotFoundException("The Riot Client couldn't be found.");

                var parts = game.LaunchTarget[RiotScanner.LaunchPrefix.Length..].Split('/');
                var patchline = parts.Length > 1 ? parts[1] : "live";

                Process.Start(new ProcessStartInfo(client, $"--launch-product={parts[0]} --launch-patchline={patchline}")
                {
                    UseShellExecute = true
                });
                return true;
            }

            // Steam games: start Steam hidden in the tray (-silent) if it isn't running, then launch the game
            if (game.SteamAppId is { } appId && FindSteamExe() is { } steamExe)
            {
                // anything after the app id is handed to the game by Steam
                var extra = string.IsNullOrWhiteSpace(game.LaunchArguments) ? "" : " " + game.LaunchArguments.Trim();
                Process.Start(new ProcessStartInfo(steamExe, $"-silent -applaunch {appId}{extra}") { UseShellExecute = true });
                return true;
            }

            // Everything else: UseShellExecute = true lets Windows handle both .exe paths and launcher links
            var info = new ProcessStartInfo(game.LaunchTarget) { UseShellExecute = true };

            if (game.IsDirectExe)   // arguments and "run as administrator" only make sense for a real .exe
            {
                if (!string.IsNullOrWhiteSpace(game.LaunchArguments))
                    info.Arguments = game.LaunchArguments.Trim();
                if (game.RunAsAdmin)
                    info.Verb = "runas";
            }

            Process.Start(info);
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return false;   // the user cancelled the administrator prompt: nothing to report
        }
        catch (Exception ex)
        {
            MessageDialog.Show(null, ex.Message, $"Couldn't launch {game.DisplayName}");
            return false;
        }
    }

    private static string? FindSteamExe()
    {
        const string key = @"HKEY_CURRENT_USER\Software\Valve\Steam";

        if (Registry.GetValue(key, "SteamExe", null) is string exe && File.Exists(exe.Replace('/', '\\')))
            return exe.Replace('/', '\\');

        if (Registry.GetValue(key, "SteamPath", null) is string folder)
        {
            var path = Path.Combine(folder.Replace('/', '\\'), "steam.exe");
            if (File.Exists(path)) return path;
        }

        return null; // fall back to the steam:// link
    }
}