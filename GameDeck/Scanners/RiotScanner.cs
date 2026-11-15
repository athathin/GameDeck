using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using GameDeck.Models;

namespace GameDeck.Scanners;

/// <summary>
/// Finds Riot games (VALORANT, League of Legends, ...) from the files the Riot Client keeps in
/// C:\ProgramData\Riot Games. Games are launched through the Riot Client with the product name.
/// </summary>
public partial class RiotScanner : IGameScanner
{
    /// <summary>Riot games have no .exe of their own to launch, so their launch target is this made-up link.</summary>
    public const string LaunchPrefix = "gamedeck-riot://";

    private static readonly string RiotData = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Riot Games");

    public List<Game> Scan()
    {
        var games = new List<Game>();

        var metadata = Path.Combine(RiotData, "Metadata");
        if (!Directory.Exists(metadata)) return games; // Riot Client not installed

        foreach (var folder in Directory.EnumerateDirectories(metadata))
        {
            // folder names look like "valorant.live" or "league_of_legends.live"
            var folderName = Path.GetFileName(folder);
            if (folderName.StartsWith("riot_client", StringComparison.OrdinalIgnoreCase)) continue;

            var settingsFile = Directory.EnumerateFiles(folder, "*.product_settings.yaml").FirstOrDefault();
            if (settingsFile is null) continue;

            var match = InstallPathRegex().Match(File.ReadAllText(settingsFile));
            if (!match.Success) continue;

            var installDir = match.Groups[1].Value.Trim().Trim('"', '\'')
                .Replace(@"\\", @"\").Replace('/', '\\');
            if (!Directory.Exists(installDir)) continue; // settings file left behind after an uninstall

            var dot = folderName.LastIndexOf('.');
            var product = dot > 0 ? folderName[..dot] : folderName;
            var patchline = dot > 0 ? folderName[(dot + 1)..] : "live";

            games.Add(new Game
            {
                Name = DisplayName(product),
                Source = "Riot",
                LaunchTarget = $"{LaunchPrefix}{product}/{patchline}",
                InstallDir = installDir
            });
        }

        return games;
    }

    /// <summary>Path of RiotClientServices.exe, or null if the Riot Client can't be found.</summary>
    public static string? FindClientExe()
    {
        try
        {
            var file = Path.Combine(RiotData, "RiotClientInstalls.json");
            if (File.Exists(file))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                foreach (var key in new[] { "rc_live", "rc_default" })
                {
                    if (doc.RootElement.TryGetProperty(key, out var value) && value.GetString() is { } raw)
                    {
                        var path = raw.Replace('/', '\\');
                        if (File.Exists(path)) return path;
                    }
                }
            }
        }
        catch { }

        const string fallback = @"C:\Riot Games\Riot Client\RiotClientServices.exe";
        return File.Exists(fallback) ? fallback : null;
    }

    private static string DisplayName(string product) => product switch
    {
        "valorant" => "VALORANT",
        "league_of_legends" => "League of Legends",
        "bacon" => "Legends of Runeterra",
        _ => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(product.Replace('_', ' '))
    };

    [GeneratedRegex(@"product_install_full_path:\s*(.+)", RegexOptions.IgnoreCase)]
    private static partial Regex InstallPathRegex();
}