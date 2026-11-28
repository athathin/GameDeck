using System.IO;
using GameDeck.Models;
using GameDeck.Services;
using Microsoft.Win32;

namespace GameDeck.Scanners;

public class GogScanner : IGameScanner
{
    public List<Game> Scan()
    {
        var games = new List<Game>();

        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32).OpenSubKey(@"SOFTWARE\GOG.com\Games");
        if (root is null) return games;

        foreach (var id in root.GetSubKeyNames())
        {
            using var key = root.OpenSubKey(id);
            if (key is null) continue;

            var name = key.GetValue("gameName") as string;
            var exe = key.GetValue("exe") as string;
            var path = key.GetValue("path") as string;

            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
                exe = string.IsNullOrWhiteSpace(path) ? null : ExeFinder.FindMainExe(path);

            if (string.IsNullOrWhiteSpace(name) || exe is null) continue;

            // GOG games ship their own uninstaller (unins000.exe) in the game folder
            string? uninstaller = null;
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                uninstaller = Directory.EnumerateFiles(path, "unins*.exe").FirstOrDefault();

            games.Add(new Game
            {
                Name = name,
                Source = "GOG",
                LaunchTarget = exe,
                InstallDir = path,
                GogId = id.All(char.IsDigit) ? id : null,   // (the registry key is the product id: for its details from GOG)
                UninstallCommand = uninstaller is null ? null : $"\"{uninstaller}\""
            });
        }

        return games;
    }
}
