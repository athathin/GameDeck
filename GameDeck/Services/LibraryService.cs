using System.IO;
using System.Text.Json;
using GameDeck.Models;
using GameDeck.Scanners;

namespace GameDeck.Services;

public class LibraryService
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppInfo.DataFolderName, "library.json");

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

    /// <summary>
    /// Puts right the launch links of Xbox games saved with the wrong app id (the Minecraft Launcher's "!Game" is "!Minecraft"),
    /// without a scan. Returns the links changed (old, new), so what is saved under the old one can follow.
    /// </summary>
    public List<(string Old, string New)> RepairXboxLinks()
    {
        var changed = new List<(string Old, string New)>();

        foreach (var game in Games)
        {
            if (XboxScanner.CorrectedLaunchTarget(game) is not { } target) continue;

            changed.Add((game.LaunchTarget, target));
            game.LaunchTarget = target;
        }

        if (changed.Count > 0)
        {
            try { Save(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }   // (put right again at the next start)
        }

        return changed;
    }

    /// <summary>Runs every scanner. Manually added games are always kept. Call from a background thread.</summary>
    public void Scan()
    {
        Games = ScanAll();
        Save();
    }

    /// <summary>
    /// Like <see cref="Scan"/>, but the library is only replaced (and saved) when the scan found a game that is new or gone,
    /// or one whose name or folder changed: otherwise the games already shown stay as they are. True when it changed.
    /// Call from a background thread.
    /// </summary>
    public bool ScanForChanges()
    {
        var found = ScanAll();
        if (SameGames(Games, found)) return false;

        Games = found;
        Save();
        return true;
    }

    /// <summary>True when both lists hold the same games (launch link, name, source, folder, uninstaller), in any order.</summary>
    internal static bool SameGames(IReadOnlyList<Game> a, IReadOnlyList<Game> b)
    {
        static string Key(Game g) => string.Join("\u001F", g.LaunchTarget.ToUpperInvariant(), g.Name, g.Source,
            g.InstallDir?.ToUpperInvariant() ?? "", g.UninstallCommand ?? "");

        return a.Count == b.Count && a.Select(Key).Order(StringComparer.Ordinal).SequenceEqual(b.Select(Key).Order(StringComparer.Ordinal));
    }

    private List<Game> ScanAll()
    {
        var manual = Games.Where(g => g.Source == "Manual").ToList();
        var scanned = new List<Game>();

        foreach (var scanner in _scanners)
        {
            try { scanned.AddRange(scanner.Scan()); }
            catch { /* one broken scanner shouldn't stop the others */ }
        }

        return manual
            .Concat(scanned)
            .GroupBy(g => g.LaunchTarget, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
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