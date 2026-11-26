using System.IO;
using GameDeck.Models;
using Microsoft.Win32;

namespace GameDeck.Scanners;

public class UbisoftScanner : IGameScanner
{
    public List<Game> Scan()
    {
        var games = new List<Game>();

        using var installs = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32).OpenSubKey(@"SOFTWARE\Ubisoft\Launcher\Installs");
        if (installs is null) return games;

        foreach (var id in installs.GetSubKeyNames())
        {
            using var key = installs.OpenSubKey(id);
            var dir = (key?.GetValue("InstallDir") as string)?.Replace('/', '\\').TrimEnd('\\');
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;

            games.Add(new Game
            {
                Name = new DirectoryInfo(dir).Name,
                Source = "Ubisoft",
                LaunchTarget = $"uplay://launch/{id}/0",
                InstallDir = dir
            });
        }

        return games;
    }
}
