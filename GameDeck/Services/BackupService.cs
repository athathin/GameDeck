using System.IO;
using System.Text.Json;
using GameDeck.Models;

namespace GameDeck.Services;

/// <summary>
/// Saves / restores everything personal in one .json file: favorites, playtime, names, hidden games, categories,
/// launch options and the games added by hand. Nothing leaves the PC: the file goes where the user puts it.
/// </summary>
public static class BackupService
{
    private const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private class Backup
    {
        // 0 when the file has no version at all: that's how files that aren't GameDeck backups get rejected
        public int Version { get; set; }
        public Dictionary<string, UserDataService.Entry> UserData { get; set; } = new();
        public List<Game> ManualGames { get; set; } = [];
    }

    public static void Export(string path, UserDataService userData, LibraryService library)
    {
        var backup = new Backup
        {
            Version = CurrentVersion,
            UserData = userData.Snapshot(),
            ManualGames = library.Games.Where(g => g.Source == "Manual").ToList()
        };

        File.WriteAllText(path, JsonSerializer.Serialize(backup, JsonOptions));
    }

    /// <summary>Restores a backup and returns how many games' data it contained. Throws if the file isn't a GameDeck backup.</summary>
    public static int Import(string path, UserDataService userData, LibraryService library)
    {
        Backup? backup;
        try
        {
            backup = JsonSerializer.Deserialize<Backup>(File.ReadAllText(path));
        }
        catch (JsonException)
        {
            backup = null;
        }

        if (backup is null || backup.Version != CurrentVersion)
            throw new InvalidDataException("This isn't a GameDeck backup file.");

        userData.Merge(backup.UserData);

        // games added by hand: only those whose .exe exists on this PC
        foreach (var game in backup.ManualGames.Where(g => File.Exists(g.LaunchTarget)))
            library.AddManual(game);

        return backup.UserData.Count;
    }
}
