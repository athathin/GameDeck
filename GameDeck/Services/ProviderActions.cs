using System.ComponentModel;
using System.Diagnostics;
using GameDeck.Models;
using GameDeck.Views;

namespace GameDeck.Services;

/// <summary>Uninstall actions that hand the job over to the game's own launcher or uninstaller.</summary>
public static class ProviderActions
{
    public static void Uninstall(Game game)
    {
        // Steam: Steam shows its own confirmation
        if (game.SteamAppId is { } id)
        {
            Run($"steam://uninstall/{id}");
            return;
        }

        // Epic has no way to do this from outside its launcher
        if (game.Source == "Epic")
        {
            MessageDialog.Show(null,
                "Epic doesn't offer a way to uninstall from outside its launcher.\n" +
                "Open the Epic Games Launcher, uninstall the game from your library, " +
                "then press \"Scan for games\".", "Uninstall");
            return;
        }

        // Xbox / Microsoft Store games are Windows apps: Windows' Installed apps list is where they get uninstalled
        if (game.Source == "Xbox")
        {
            MessageDialog.Show(null,
                "Xbox games are uninstalled from Windows' Installed apps list (or the Xbox app), which will open now.\n" +
                "Afterwards press \"Scan for games\".", "Uninstall");
            Run("ms-settings:appsfeatures");
            return;
        }

        // GOG (its own unins000.exe) and Windows-registered games (their registered uninstall command)
        if (!string.IsNullOrWhiteSpace(game.UninstallCommand))
        {
            RunCommand(game.UninstallCommand);
            return;
        }

        // Anything else (manually added games, or a library saved before this feature existed)
        MessageDialog.Show(null,
            $"GameDeck doesn't know how to uninstall \"{game.Name}\" directly.\n" +
            "Windows' Installed apps list will open: find the game there and uninstall it, " +
            "then press \"Scan for games\".\n\n" +
            "(If this game came from a scan, pressing \"Scan for games\" first may fix this.)", "Uninstall");
        Run("ms-settings:appsfeatures");
    }

    /// <summary>Runs a registered uninstall command such as: "C:\Games\X\unins000.exe" /something</summary>
    private static void RunCommand(string command)
    {
        var (file, args) = SplitCommand(command);
        Run(file, args);
    }

    private static void Run(string file, string args = "")
    {
        try
        {
            // UseShellExecute lets Windows show the admin (UAC) prompt if the uninstaller needs one
            Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // user cancelled the admin prompt: nothing to report
        }
        catch (Exception ex)
        {
            MessageDialog.Show(null, ex.Message, "Couldn't start the uninstaller");
        }
    }

    /// <summary>Splits a command line into program and arguments (handles quoted and unquoted paths).</summary>
    private static (string File, string Args) SplitCommand(string command)
    {
        command = command.Trim();

        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            if (end > 0)
                return (command[1..end], command[(end + 1)..].Trim());
        }

        var exe = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0)
            return (command[..(exe + 4)], command[(exe + 4)..].Trim());

        return (command, "");
    }
}