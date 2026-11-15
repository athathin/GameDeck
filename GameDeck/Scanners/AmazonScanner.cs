using System.IO;
using System.Text.Json;
using GameDeck.Models;
using GameDeck.Services;

namespace GameDeck.Scanners;

/// <summary>
/// Finds games installed by the Amazon Games app. By default they live in "Amazon Games\Library" on a drive, one folder per
/// game, with a fuel.json file that names the program to start. The library folder is looked for on every fixed drive.
/// </summary>
public class AmazonScanner : IGameScanner
{
    public List<Game> Scan()
    {
        var games = new List<Game>();

        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            var library = Path.Combine(drive.RootDirectory.FullName, "Amazon Games", "Library");
            if (!Directory.Exists(library)) continue;

            foreach (var dir in Directory.EnumerateDirectories(library))
            {
                try
                {
                    var exe = ReadMainExe(dir) ?? ExeFinder.FindMainExe(dir);
                    if (exe is null) continue;

                    games.Add(new Game
                    {
                        Name = Path.GetFileName(dir),
                        Source = "Amazon Games",
                        LaunchTarget = exe,
                        InstallDir = dir
                    });
                }
                catch
                {
                    // one unreadable folder doesn't stop the others
                }
            }
        }

        return games;
    }

    /// <summary>fuel.json: {"Main": {"Command": "Game.exe"}}. Null when it isn't there or the program doesn't exist.</summary>
    private static string? ReadMainExe(string dir)
    {
        try
        {
            var fuel = Path.Combine(dir, "fuel.json");
            if (!File.Exists(fuel)) return null;

            using var json = JsonDocument.Parse(File.ReadAllText(fuel));
            if (!json.RootElement.TryGetProperty("Main", out var main) ||
                !main.TryGetProperty("Command", out var command) || command.GetString() is not { Length: > 0 } relative)
                return null;

            var full = Path.GetFullPath(Path.Combine(dir, relative));

            // only a program inside the game's own folder
            return full.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && File.Exists(full) ? full : null;
        }
        catch
        {
            return null;
        }
    }
}
