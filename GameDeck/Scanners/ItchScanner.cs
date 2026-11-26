using System.IO;
using System.IO.Compression;
using System.Text.Json;
using GameDeck.Models;
using GameDeck.Services;

namespace GameDeck.Scanners;

/// <summary>
/// Finds games installed by the itch.io app. It keeps every game in its own folder under %AppData%\itch\apps, and writes a
/// receipt (.itch\receipt.json.gz) there that holds the game's real title. Games installed in a folder of your own choosing
/// are not found this way (add those with "Add games").
/// </summary>
public class ItchScanner : IGameScanner
{
    public List<Game> Scan()
    {
        var games = new List<Game>();

        var apps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "itch", "apps");
        if (!Directory.Exists(apps)) return games;   // the itch app isn't installed

        foreach (var dir in Directory.EnumerateDirectories(apps))
        {
            try
            {
                var exe = ExeFinder.FindMainExe(dir);
                if (exe is null) continue;

                games.Add(new Game
                {
                    Name = ReadTitle(dir) ?? Path.GetFileName(dir),
                    Source = "itch.io",
                    LaunchTarget = exe,
                    InstallDir = dir
                });
            }
            catch
            {
                // one unreadable folder doesn't stop the others
            }
        }

        return games;
    }

    /// <summary>The title from the receipt: {"game": {"title": "..."}}. Null when there is none.</summary>
    private static string? ReadTitle(string dir)
    {
        try
        {
            var receipt = Path.Combine(dir, ".itch", "receipt.json.gz");
            if (!File.Exists(receipt)) return null;

            using var file = File.OpenRead(receipt);
            using var unzipped = new GZipStream(file, CompressionMode.Decompress);
            using var json = JsonDocument.Parse(unzipped);

            return json.RootElement.TryGetProperty("game", out var game) &&
                   game.TryGetProperty("title", out var title) && title.GetString() is { Length: > 0 } text
                ? text
                : null;
        }
        catch
        {
            return null;
        }
    }
}
