using System.IO;
using System.Text.Json;
using GameDeck.Models;
using GameDeck.Scanners;

namespace GameDeck.Services;

public class LibraryService
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameDeck", "library.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly List<IGameScanner> _scanners =
    [
        new SteamScanner(),
        new EpicScanner(),
        new GogScanner(),
        new UbisoftScanner(),
        new RiotScanner(),
        new XboxScanner(),
        new RockstarScanner(),
        new ItchScanner(),
        new AmazonScanner(),
        new UninstallRegistryScanner("Battle.net", (name, publisher) =>
            (publisher.Contains("Blizzard", StringComparison.OrdinalIgnoreCase) ||
             publisher.Contains("Activision", StringComparison.OrdinalIgnoreCase)) &&
            !name.Contains("Battle.net", StringComparison.OrdinalIgnoreCase)),
        new UninstallRegistryScanner("EA", (name, publisher) =>
            publisher.Contains("Electronic Arts", StringComparison.OrdinalIgnoreCase) &&
            !name.Equals("Origin", StringComparison.OrdinalIgnoreCase) &&
            !name.Equals("EA app", StringComparison.OrdinalIgnoreCase) &&
            !name.Equals("EA Desktop", StringComparison.OrdinalIgnoreCase))
    ];

    public List<Game> Games { get; private set; } = [];

    /// <summary>Loads the saved library (instant, no scanning).</summary>
    public void Load()
    {
        // (a damaged file is set aside and the newest daily backup is used instead of starting empty)
        Games = AutoBackupService.LoadJson<List<Game>>(FilePath) ?? [];
    }

    /// <summary>Runs every scanner. Manually added games are always kept. Call from a background thread.</summary>
    public void Scan()
    {
        var manual = Games.Where(g => g.Source == "Manual").ToList();
        var scanned = new List<Game>();

        foreach (var scanner in _scanners)
        {
            try { scanned.AddRange(scanner.Scan()); }
            catch { /* one broken scanner shouldn't stop the others */ }
        }

        Games = manual
            .Concat(scanned)
            .GroupBy(g => g.LaunchTarget, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        Save();
    }

    public void AddManual(Game game)
    {
        if (Games.Any(g => g.LaunchTarget.Equals(game.LaunchTarget, StringComparison.OrdinalIgnoreCase)))
            return;

        Games.Add(game);
        Games = Games.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        Save();
    }

    public void Remove(Game game)
    {
        Games.Remove(game);
        Save();
    }

    private void Save()
    {
        AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(Games, JsonOptions));
    }
}