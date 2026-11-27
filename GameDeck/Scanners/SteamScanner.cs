using System.IO;
using System.Text.RegularExpressions;
using GameDeck.Models;
using Microsoft.Win32;

namespace GameDeck.Scanners;

public partial class SteamScanner : IGameScanner
{
    public List<Game> Scan()
    {
        var games = new List<Game>();

        var steamPath = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        if (string.IsNullOrWhiteSpace(steamPath))
            return games; // Steam not installed

        steamPath = steamPath.Replace('/', '\\');

        // Every library folder (the main one plus any extra drives)
        var libraries = new List<string> { steamPath };
        var libraryFile = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (File.Exists(libraryFile))
        {
            foreach (Match m in PathRegex().Matches(File.ReadAllText(libraryFile)))
            {
                var path = m.Groups[1].Value.Replace(@"\\", @"\");
                if (!libraries.Contains(path, StringComparer.OrdinalIgnoreCase))
                    libraries.Add(path);
            }
        }

        // Each installed game has an appmanifest_<appid>.acf file
        foreach (var lib in libraries)
        {
            var steamApps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(steamApps)) continue;

            foreach (var acf in Directory.GetFiles(steamApps, "appmanifest_*.acf"))
            {
                var text = File.ReadAllText(acf);
                var appId = ReadValue(text, "appid");
                var name = ReadValue(text, "name");
                if (appId is null || name is null) continue;

                // Skip Steam's own tools/runtimes
                if (name.StartsWith("Steamworks Common Redistributables") ||
                    name.StartsWith("Steam Linux Runtime") ||
                    name.StartsWith("Proton"))
                    continue;

                string? installDir = null;
                if (ReadValue(text, "installdir") is { Length: > 0 } dirName)
                {
                    // games live in steamapps\common, soundtracks in steamapps\music
                    installDir = FindInstallDir(steamApps, dirName);

                    // Steam keeps the manifest of a game whose folder was deleted (it still calls it installed): that game
                    // can't be played and takes no space, so it isn't listed
                    if (installDir is null) continue;
                }

                games.Add(new Game
                {
                    Name = name,
                    Source = "Steam",
                    LaunchTarget = $"steam://rungameid/{appId}",
                    InstallDir = installDir
                });
            }
        }

        return games;
    }

    /// <summary>The app's folder: steamapps\common\{dir} (games) or steamapps\music\{dir} (soundtracks), or null when neither exists.</summary>
    internal static string? FindInstallDir(string steamApps, string dirName) =>
        new[] { "common", "music" }
            .Select(kind => Path.Combine(steamApps, kind, dirName))
            .FirstOrDefault(Directory.Exists);

    private static string? ReadValue(string text, string key)
    {
        var m = Regex.Match(text, $"\"{key}\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex PathRegex();
}
