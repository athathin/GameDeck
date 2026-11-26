using System.IO;
using System.Text.Json;
using GameDeck.Models;

namespace GameDeck.Services;

/// <summary>
/// Saves / restores everything personal in one .json file: favorites, playtime, names, hidden games, categories, status,
/// ratings, notes, launch options, launch routines and the games added by hand. Nothing leaves the PC: the file goes where the user puts it.
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

    private const int KeepDaily = 7;   // the daily backups kept in the chosen folder

    public static void Export(string path, UserDataService userData, LibraryService library) =>
        File.WriteAllText(path, Serialize(userData, library));

    private static string Serialize(UserDataService userData, LibraryService library) => JsonSerializer.Serialize(new Backup
    {
        Version = CurrentVersion,
        UserData = userData.Snapshot(),
        ManualGames = library.Games.Where(g => g.Source == "Manual").ToList()
    }, JsonOptions);

    /// <summary>The folder the daily backups go in, inside the one the user chose ("GameDeck backups").</summary>
    public static string FolderIn(string chosen) => Path.Combine(chosen, AppInfo.DisplayName + " backups");

    /// <summary>
    /// The daily backup in the folder the user chose (Settings › Backup): today's file, made once a day, the same kind of file as
    /// Export backup (Import backup reads it). The newest few are kept. Returns false when it couldn't be saved (drive gone).
    /// </summary>
    public static bool SaveDailyIfDue(string chosen, UserDataService userData, LibraryService library)
    {
        try
        {
            var folder = FolderIn(chosen);
            var today = Path.Combine(folder, DailyName(DateTime.Now));
            if (File.Exists(today)) return true;

            Directory.CreateDirectory(folder);

            // written next to it, then renamed: a backup cut short (drive pulled out) is never left as today's
            var part = today + ".part";
            File.WriteAllText(part, Serialize(userData, library));
            File.Move(part, today, overwrite: true);

            foreach (var old in OldDailies(Directory.GetFiles(folder, "GameDeck-backup-*.json")))
            {
                try { File.Delete(old); } catch { }
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return false;   // the folder isn't there right now (a USB drive): tried again later
        }
    }

    internal static string DailyName(DateTime day) => $"GameDeck-backup-{day:yyyy-MM-dd}.json";

    /// <summary>The daily backup files beyond the newest few (their names sort by date).</summary>
    internal static IEnumerable<string> OldDailies(IEnumerable<string> files) => files
        .Where(f => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(f), @"^GameDeck-backup-\d{4}-\d{2}-\d{2}\.json$"))
        .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal)
        .Skip(KeepDaily);

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
