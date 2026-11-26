using System.IO;
using System.Text.Json;
using GameDeck.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;

namespace GameDeck.Services;

/// <summary>How many of a game's achievements are unlocked.</summary>
public record AchievementCount(int Unlocked, int Total)
{
    public override string ToString() => $"{Unlocked} / {Total}";
}

/// <summary>
/// The achievements unlocked per game (keyed by launch target), read from files on this PC; nothing goes online:
///  - Steam's own: appcache\stats keeps, for each game played while Steam was online, the list of its achievements
///    (UserGameStatsSchema_&lt;app&gt;.bin) and the player's unlocked ones (UserGameStats_&lt;account&gt;_&lt;app&gt;.bin). An
///    achievement is one bit of a stat: the schema lists the stat's bits, the player's file holds the stat's value;
///  - GOG Galaxy's database, when it's installed: its count per game, also for the launchers connected to it.
/// Steam's own count wins for a Steam game. Call from a background thread.
/// </summary>
public static class Achievements
{
    public static Dictionary<string, AchievementCount> Read(IReadOnlyCollection<Game> games)
    {
        var counts = new Dictionary<string, AchievementCount>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var (target, count) in ReadGalaxy(games))
                counts[target] = count;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            CrashLog.Write(ex, "Achievements (GOG Galaxy)");
        }

        foreach (var (target, count) in ReadSteam(games))
            counts[target] = count;   // (Steam's own files are exact: they win)

        return counts;
    }

    // ---------- Steam ----------

    private const string SteamPrefix = "steam://rungameid/";

    private static Dictionary<string, AchievementCount> ReadSteam(IReadOnlyCollection<Game> games)
    {
        var counts = new Dictionary<string, AchievementCount>(StringComparer.OrdinalIgnoreCase);
        if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is not string steam) return counts;

        var folder = Path.Combine(steam, "appcache", "stats");
        if (!Directory.Exists(folder)) return counts;

        foreach (var game in games.Where(g => g.LaunchTarget.StartsWith(SteamPrefix, StringComparison.OrdinalIgnoreCase)))
        {
            var app = game.LaunchTarget[SteamPrefix.Length..];
            if (!app.All(char.IsAsciiDigit)) continue;

            try
            {
                var schema = Path.Combine(folder, $"UserGameStatsSchema_{app}.bin");
                if (!File.Exists(schema)) continue;

                // several Steam accounts on this PC: the one that played it last
                var player = new DirectoryInfo(folder).GetFiles($"UserGameStats_*_{app}.bin")
                    .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();

                var count = Count(BinaryKeyValues.Parse(File.ReadAllBytes(schema)),
                                  player is null ? null : BinaryKeyValues.Parse(File.ReadAllBytes(player.FullName)));
                if (count is not null)
                    counts[game.LaunchTarget] = count;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
            {
                // this game's files can't be read right now (Steam writing them, or another format): no count for it
            }
        }

        return counts;
    }

    /// <summary>
    /// The count from a game's schema (its one top section → "stats" → each stat → "bits" → one entry per achievement, named by
    /// its bit) and the player's stats (each stat → "data": the stat's bits). Null when the game has no achievements.
    /// </summary>
    internal static AchievementCount? Count(Dictionary<string, object> schema, Dictionary<string, object>? player)
    {
        if (schema.Values.FirstOrDefault() is not Dictionary<string, object> game ||
            !game.TryGetValue("stats", out var statsValue) || statsValue is not Dictionary<string, object> stats)
            return null;

        // the player's file: its one top section ("cache") holds the stats by id
        var values = player?.Values.FirstOrDefault() as Dictionary<string, object>;

        int total = 0, unlocked = 0;
        foreach (var (statId, statValue) in stats)
        {
            if (statValue is not Dictionary<string, object> stat ||
                !stat.TryGetValue("bits", out var bitsValue) || bitsValue is not Dictionary<string, object> bits)
                continue;

            // an achievements stat whose list is empty: the file is an incomplete leftover (Counter-Strike 2's), so it can't be
            // trusted at all (GOG Galaxy's count, if there is one, is used instead)
            if (bits.Count == 0) return null;

            var data = values is not null && values.TryGetValue(statId, out var playerStat) &&
                       playerStat is Dictionary<string, object> p && p.TryGetValue("data", out var d) && d is int bitsSet
                ? unchecked((uint)bitsSet)
                : 0u;

            foreach (var bit in bits.Keys)
            {
                if (!int.TryParse(bit, out var index) || index is < 0 or > 31) continue;

                total++;
                if ((data >> index & 1) == 1)
                    unlocked++;
            }
        }

        return total == 0 ? null : new AchievementCount(unlocked, total);
    }

    // ---------- GOG Galaxy ----------

    private static Dictionary<string, AchievementCount> ReadGalaxy(IReadOnlyCollection<Game> games)
    {
        var counts = new Dictionary<string, AchievementCount>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(LauncherPlaytime.GalaxyDatabase)) return counts;

        var rows = LauncherPlaytime.Query(LauncherPlaytime.GalaxyDatabase, """
            SELECT p.releaseKey, p.value,
                   (SELECT n.value FROM GamePieces n JOIN GamePieceTypes nt ON nt.id = n.gamePieceTypeId
                    WHERE n.releaseKey = p.releaseKey AND nt.type IN ('originalTitle', 'title') ORDER BY nt.type = 'title' LIMIT 1)
            FROM GamePieces p JOIN GamePieceTypes t ON t.id = p.gamePieceTypeId WHERE t.type = 'myAchievementsCount'
            """, r => (Key: r.GetString(0), Count: GalaxyCount(r.GetString(1)),
                       Title: r.IsDBNull(2) ? null : LauncherPlaytime.TitleOf(r.GetString(2))));

        var targets = games.Select(g => g.LaunchTarget).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var gog = LauncherPlaytime.GogProductIds();

        foreach (var row in rows.Where(r => r.Count is not null))
        {
            foreach (var target in LauncherPlaytime.GalaxyTargets(row.Key, row.Title, games, gog, targets))
                counts[target] = row.Count!;
        }

        return counts;
    }

    /// <summary>Galaxy keeps the count as JSON: {"all":57,"unlocked":9} (null for a game without achievements).</summary>
    internal static AchievementCount? GalaxyCount(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            return root.TryGetProperty("all", out var all) && all.ValueKind == JsonValueKind.Number && all.GetInt32() > 0 &&
                   root.TryGetProperty("unlocked", out var unlocked) && unlocked.ValueKind == JsonValueKind.Number
                ? new AchievementCount(Math.Min(unlocked.GetInt32(), all.GetInt32()), all.GetInt32())
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
