using System.Globalization;
using System.IO;
using System.Text.Json;
using GameDeck.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;
using Played = GameDeck.Services.SteamPlaytime.Played;

namespace GameDeck.Services;

/// <summary>
/// The playtime other programs on this PC counted, per game (keyed by launch target), read from their own files; nothing goes
/// online:
///  - Steam (<see cref="SteamPlaytime"/>);
///  - GOG Galaxy's database, which also keeps the hours of the launchers connected to it (Ubisoft, Xbox, EA, Epic...): Steam
///    and GOG games are matched by their id, the others by their exact title, and only to a game of the same launcher;
///  - the itch.io app's database, matched by the game's install folder.
/// With several sources for one game, the highest playtime and the latest date win. Call from a background thread.
/// </summary>
public static class LauncherPlaytime
{
    internal static readonly string GalaxyDatabase = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GOG.com", "Galaxy", "storage", "galaxy-2.0.db");

    private static readonly string ItchDatabase = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "itch", "db", "butler.db");

    // Galaxy's launcher names -> GameDeck's (the Source of the games its titles may be matched to; "" = any but Steam and GOG)
    private static readonly Dictionary<string, string> GalaxyPlatforms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["uplay"] = "Ubisoft", ["xboxone"] = "Xbox", ["origin"] = "EA", ["epic"] = "Epic", ["battlenet"] = "Battle.net",
        ["rockstar"] = "Rockstar", ["amazon"] = "Amazon Games", ["generic"] = ""
    };

    /// <summary>One game of GOG Galaxy's database: its key ("uplay_..."), minutes, last played (UTC) and title.</summary>
    internal record GalaxyGame(string ReleaseKey, long Minutes, DateTime? LastPlayedUtc, string? Title);

    /// <summary>One game of the itch app's database: its install folder's name, seconds and last played (UTC).</summary>
    internal record ItchGame(string FolderName, long Seconds, DateTime? LastPlayedUtc);

    public static Dictionary<string, Played> Read(IReadOnlyCollection<Game> games)
    {
        var all = SteamPlaytime.Read();
        Merge(all, MatchGalaxy(ReadSafely(() => ReadGalaxy(GalaxyDatabase)), games, GogProductIds()));
        Merge(all, MatchItch(ReadSafely(() => ReadItch(ItchDatabase)), games));
        return all;
    }

    private static void Merge(Dictionary<string, Played> into, Dictionary<string, Played> more)
    {
        foreach (var (target, played) in more)
            into[target] = into.TryGetValue(target, out var known)
                ? new Played(Math.Max(known.Seconds, played.Seconds), Latest(known.LastPlayedUtc, played.LastPlayedUtc))
                : played;
    }

    private static DateTime? Latest(DateTime? a, DateTime? b) => a is null ? b : b is null ? a : a > b ? a : b;

    private static List<T> ReadSafely<T>(Func<List<T>> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            CrashLog.Write(ex, "Launcher playtime");   // (that launcher's hours are left out)
            return [];
        }
    }

    // ---------- GOG Galaxy ----------

    internal static List<GalaxyGame> ReadGalaxy(string database) => File.Exists(database) ? Query(database, """
        SELECT g.releaseKey, MAX(g.minutesInGame),
               (SELECT MAX(l.lastPlayedDate) FROM LastPlayedDates l WHERE l.gameReleaseKey = g.releaseKey),
               (SELECT p.value FROM GamePieces p JOIN GamePieceTypes t ON t.id = p.gamePieceTypeId
                WHERE p.releaseKey = g.releaseKey AND t.type IN ('originalTitle', 'title') ORDER BY t.type = 'title' LIMIT 1)
        FROM GameTimes g WHERE g.minutesInGame > 0 GROUP BY g.releaseKey
        """, r => new GalaxyGame(r.GetString(0), r.GetInt64(1), r.IsDBNull(2) ? null : Utc(r.GetString(2)),
                                  r.IsDBNull(3) ? null : TitleOf(r.GetString(3)))) : [];

    /// <summary>Galaxy keeps a title as JSON: {"title":"Cyberpunk 2077"}.</summary>
    internal static string? TitleOf(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("title", out var title) ? title.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>GOG's games on this PC: product id -> the program GameDeck starts (its launch target, see GogScanner).</summary>
    internal static Dictionary<string, string> GogProductIds()
    {
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32).OpenSubKey(@"SOFTWARE\GOG.com\Games");
        if (root is null) return ids;

        foreach (var id in root.GetSubKeyNames())
        {
            using var key = root.OpenSubKey(id);
            if (key?.GetValue("exe") is string exe && exe.Length > 0)
                ids[id] = exe;
        }

        return ids;
    }

    /// <summary>Galaxy's games -> GameDeck's: Steam and GOG by id, the others by exact title within the same launcher.</summary>
    internal static Dictionary<string, Played> MatchGalaxy(IEnumerable<GalaxyGame> rows, IReadOnlyCollection<Game> games,
                                                          IReadOnlyDictionary<string, string> gogExeById)
    {
        var played = new Dictionary<string, Played>(StringComparer.OrdinalIgnoreCase);
        var targets = games.Select(g => g.LaunchTarget).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            foreach (var target in GalaxyTargets(row.ReleaseKey, row.Title, games, gogExeById, targets))
                Merge(played, new() { [target] = new Played(row.Minutes * 60, row.LastPlayedUtc) });
        }

        return played;
    }

    /// <summary>
    /// The launch targets of GameDeck's games that one of Galaxy's games ("steam_620", "gog_1423049311", "uplay_...") is:
    /// Steam and GOG by id, the others by exact title within the same launcher.
    /// </summary>
    internal static IEnumerable<string> GalaxyTargets(string releaseKey, string? galaxyTitle, IReadOnlyCollection<Game> games,
                                                      IReadOnlyDictionary<string, string> gogExeById, IReadOnlySet<string> targets)
    {
        var split = releaseKey.IndexOf('_');
        if (split <= 0) return [];

        var platform = releaseKey[..split];
        var id = releaseKey[(split + 1)..];

        IEnumerable<string> matches = platform.ToLowerInvariant() switch
        {
            "steam" => [$"steam://rungameid/{id}"],
            "gog" => gogExeById.TryGetValue(id, out var exe) ? [exe] : [],
            _ when GalaxyPlatforms.TryGetValue(platform, out var source) && CoverService.Normalize(galaxyTitle) is { Length: > 0 } title =>
                games.Where(g => (source.Length == 0 ? g.Source is not ("Steam" or "GOG") : g.Source == source) &&
                                 (CoverService.Normalize(g.Name) == title || CoverService.Normalize(g.DisplayName) == title))
                     .Select(g => g.LaunchTarget),
            _ => []
        };

        return matches.Where(targets.Contains);
    }

    // ---------- itch.io ----------

    internal static List<ItchGame> ReadItch(string database) => File.Exists(database) ? Query(database, """
        SELECT install_folder_name, seconds_run, last_touched_at FROM caves WHERE seconds_run > 0 AND install_folder_name IS NOT NULL
        """, r => new ItchGame(r.GetString(0), r.GetInt64(1), r.IsDBNull(2) ? null : Utc(r.GetValue(2).ToString()))) : [];

    /// <summary>The itch app's games -> GameDeck's itch games, by install folder (%AppData%\itch\apps\&lt;folder&gt;).</summary>
    internal static Dictionary<string, Played> MatchItch(IEnumerable<ItchGame> rows, IReadOnlyCollection<Game> games)
    {
        var byFolder = games.Where(g => g.Source == "itch.io" && !string.IsNullOrEmpty(g.InstallDir))
            .GroupBy(g => Path.GetFileName(g.InstallDir!.TrimEnd('\\')), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().LaunchTarget, StringComparer.OrdinalIgnoreCase);

        var played = new Dictionary<string, Played>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (byFolder.TryGetValue(row.FolderName, out var target))
                Merge(played, new() { [target] = new Played(row.Seconds, row.LastPlayedUtc) });
        }

        return played;
    }

    // ---------- reading a launcher's database ----------

    /// <summary>
    /// Runs a query on a copy of the database: the launcher may be running and using it, and its database is never touched.
    /// (The -wal file holds the latest changes of a database in use: it is copied with it.)
    /// </summary>
    internal static List<T> Query<T>(string database, string sql, Func<SqliteDataReader, T> read)
    {
        var copy = Path.Combine(Path.GetTempPath(), $"gamedeck-{Guid.NewGuid():N}.db");
        try
        {
            File.Copy(database, copy);
            if (File.Exists(database + "-wal"))
                File.Copy(database + "-wal", copy + "-wal");

            var rows = new List<T>();
            using (var connection = new SqliteConnection($"Data Source={copy};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    rows.Add(read(reader));
            }

            return rows;
        }
        finally
        {
            foreach (var file in new[] { copy, copy + "-wal", copy + "-shm" })
            {
                try { File.Delete(file); }
                catch (IOException) { }
            }
        }
    }

    /// <summary>A date the launchers write as text ("2020-06-20 17:08:50", UTC); null when it can't be read.</summary>
    internal static DateTime? Utc(string? text) =>
        DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)
            ? date
            : null;
}
