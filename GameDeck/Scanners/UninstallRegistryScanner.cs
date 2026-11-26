using System.IO;
using GameDeck.Models;
using GameDeck.Services;
using Microsoft.Win32;

namespace GameDeck.Scanners;

/// <summary>
/// Finds games through Windows' "installed programs" list, filtered by publisher.
/// Used for launchers that have no easy manifest files (Battle.net, EA, ...).
/// <paramref name="findExe"/> picks the program to launch from the game's folder; by default it's the largest plausible .exe.
/// </summary>
public class UninstallRegistryScanner(string source, Func<string, string, bool> isGame,
                                      Func<string, string?>? findExe = null) : IGameScanner
{
    private const string Root = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>Both the 32-bit and the 64-bit registry view, so the 32-bit and 64-bit builds of GameDeck find the same games.</summary>
    private static IEnumerable<RegistryKey> Hives()
    {
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                RegistryKey? key = null;
                try { key = RegistryKey.OpenBaseKey(hive, view); }
                catch { }

                if (key is not null)
                    yield return key;
            }
        }
    }

    public List<Game> Scan()
    {
        var games = new List<Game>();

        foreach (var hive in Hives())
        {
            using (hive)
            {
                using var key = hive.OpenSubKey(Root);
                if (key is null) continue;

                foreach (var sub in key.GetSubKeyNames())
                {
                    using var app = key.OpenSubKey(sub);
                    var name = app?.GetValue("DisplayName") as string;
                    var publisher = app?.GetValue("Publisher") as string ?? "";
                    var dir = (app?.GetValue("InstallLocation") as string)?.Trim('"');

                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                        continue;
                    if (!isGame(name, publisher)) continue;

                    var exe = (findExe ?? ExeFinder.FindMainExe)(dir);
                    if (exe is null) continue;

                    if (games.Any(g => string.Equals(g.InstallDir, dir, StringComparison.OrdinalIgnoreCase) && g.Name == name))
                        continue;

                    games.Add(new Game
                    {
                        Name = name,
                        Source = source,
                        LaunchTarget = exe,
                        InstallDir = dir,
                        UninstallCommand = app?.GetValue("UninstallString") as string
                    });
                }
            }
        }

        return games;
    }
}
