using System.IO;
using System.Text.Json;
using GameDeck.Models;

namespace GameDeck.Services;

/// <summary>
/// How much space each game takes on disk: the size of its install folder, measured in the background and kept in
/// %AppData%\GameDeck\sizes.json, so it shows at once next time. A size older than a few days is measured again. Works for
/// every launcher (only the install folder is read); a folder Windows won't let GameDeck read (some Xbox games) has no size.
/// </summary>
public static class DiskUsage
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppInfo.DataFolderName, "sizes.json");

    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(3);

    public class Entry
    {
        public string Folder { get; set; } = "";   // the folder measured (a game moved to another drive is measured again)
        public long Bytes { get; set; }
        public DateTime MeasuredUtc { get; set; }
    }

    private static Dictionary<string, Entry> _sizes = new(StringComparer.OrdinalIgnoreCase);
    private static bool _loaded;
    private static bool _measuring;

    /// <summary>Puts the sizes measured before on the games (instantly, from the file).</summary>
    public static void ApplySaved(IEnumerable<Game> games)
    {
        Load();
        foreach (var game in games)
            game.SizeBytes = FolderOf(game) is { } folder && _sizes.TryGetValue(game.LaunchTarget, out var entry) &&
                             entry.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase)
                ? entry.Bytes
                : null;
    }

    /// <summary>
    /// Measures, one at a time in the background, the games that have no size yet or an old one. <paramref name="changed"/> runs
    /// on the UI thread after each game that got a (new) size. Only one pass runs at a time. While <paramref name="busy"/> says so
    /// (a game is running: reading the disk could make it stutter), it stops; the rest is measured at the next pass.
    /// </summary>
    public static async Task MeasureAsync(IReadOnlyList<Game> games, Action changed, Func<bool> busy)
    {
        if (_measuring) return;
        _measuring = true;

        try
        {
            Load();
            var now = DateTime.UtcNow;

            foreach (var game in games)
            {
                if (busy()) break;
                if (FolderOf(game) is not { } folder) continue;

                if (_sizes.TryGetValue(game.LaunchTarget, out var known) &&
                    known.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase) && now - known.MeasuredUtc < MaxAge)
                    continue;

                var bytes = await Task.Run(() => Measure(folder));

                if (bytes is { } size)
                    _sizes[game.LaunchTarget] = new Entry { Folder = folder, Bytes = size, MeasuredUtc = now };
                else
                    _sizes.Remove(game.LaunchTarget);

                if (game.SizeBytes != bytes)
                {
                    game.SizeBytes = bytes;
                    changed();
                }
            }

            Save();
        }
        finally
        {
            _measuring = false;
        }
    }

    /// <summary>
    /// The folder whose size is the game's: its install folder, or the folder of its .exe (games added by hand). Null when there is
    /// none, or when it is a place that holds much more than one game (a drive, Program Files, Downloads, the desktop...).
    /// </summary>
    internal static string? FolderOf(Game game)
    {
        if (game.TrackingFolder is not { } folder) return null;

        string full;
        try
        {
            full = Path.GetFullPath(folder).TrimEnd('\\');
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return null;
        }

        return IsSharedFolder(full) ? null : full;
    }

    private static readonly Lazy<HashSet<string>> SharedFolders = new(() =>
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path))
                set.Add(path.TrimEnd('\\'));
        }

        foreach (var special in new[]
                 {
                     Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.Desktop,
                     Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.Windows,
                     Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData
                 })
            Add(Environment.GetFolderPath(special));

        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
        return set;
    });

    /// <summary>A drive's root, or one of the folders where many programs live together.</summary>
    internal static bool IsSharedFolder(string full) =>
        Path.GetPathRoot(full)?.TrimEnd('\\').Equals(full, StringComparison.OrdinalIgnoreCase) == true ||
        SharedFolders.Value.Contains(full);

    /// <summary>The size of every file in the folder and below it (links to other folders aren't followed), or null when it can't be read.</summary>
    private static long? Measure(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return null;

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint   // (a junction to another folder would count it twice, or loop)
            };

            long total = 0;
            var any = false;
            foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", options))
            {
                total += file.Length;
                any = true;
            }

            return any ? total : null;   // nothing readable (a protected Xbox folder): no size rather than "0 bytes"
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>One of the biggest games on the "Space on disk" card: the game (its Uninstall button) and its line.</summary>
    public record Line(Game Game, string Text);

    /// <summary>The "Space on disk" card: a headline, a line about the games not played lately, and the biggest games.</summary>
    public record Card(string Headline, string Compare, IReadOnlyList<Line> Lines);

    /// <summary>
    /// The Stats tab's "Space on disk" card: the space all the games take, how much of it belongs to games not played for half a
    /// year (or never), and the biggest games with when each was played last. Null when no size is known yet.
    /// </summary>
    public static Card? Summary(IEnumerable<Game> games, DateTime nowUtc, int count = 8)
    {
        // DLC and soundtracks that Steam installs as apps of their own: their space counts, but not as games
        var all = games.Where(g => g.SizeBytes is > 0).ToList();
        var addOns = all.Where(g => g.IsAddOn).ToList();
        var sized = all.Where(g => !g.IsAddOn).OrderByDescending(g => g.SizeBytes).ToList();
        if (all.Count == 0) return null;

        var total = all.Sum(g => g.SizeBytes!.Value);
        var what = sized.Count == 1 ? "1 game" : $"{sized.Count} games";
        if (addOns.Count > 0)
            what += addOns.Count == 1 ? " and 1 DLC" : $" and {addOns.Count} DLC";
        var headline = $"{Format(total)} for {what}";

        var idle = sized.Where(g => g.LastPlayed is not { } played || nowUtc - played > TimeSpan.FromDays(182)).ToList();
        var compare = idle.Count == 0
            ? "Every game here was played in the last 6 months"
            : $"{Format(idle.Sum(g => g.SizeBytes!.Value))} is {(idle.Count == 1 ? "1 game" : $"{idle.Count} games")} not played in the last 6 months";

        var lines = sized.Take(count)
            .Select(g => new Line(g, $"{g.DisplayName}:  {Format(g.SizeBytes!.Value)}  ·  {LastPlayed(g, nowUtc)}"))
            .ToList();

        return new Card(headline, compare, lines);
    }

    /// <summary>"played today", "played 3 weeks ago", "never played".</summary>
    internal static string LastPlayed(Game game, DateTime nowUtc)
    {
        if (game.LastPlayed is not { } played) return "never played";

        var days = (int)(DateOnly.FromDateTime(nowUtc.ToLocalTime()).DayNumber - DateOnly.FromDateTime(played.ToLocalTime()).DayNumber);
        return days switch
        {
            <= 0 => "played today",
            1 => "played yesterday",
            < 14 => $"played {days} days ago",
            < 60 => $"played {days / 7} weeks ago",
            < 730 => $"played {days / 30} months ago",
            _ => $"played {days / 365} years ago"
        };
    }

    /// <summary>"48.2 GB", "750 MB", "12 KB".</summary>
    public static string Format(long bytes) => bytes switch
    {
        >= 1L << 40 => $"{bytes / (double)(1L << 40):0.0#} TB",
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} MB",
        _ => $"{Math.Max(1, bytes / 1024)} KB"
    };

    private static void Load()
    {
        if (_loaded) return;
        _loaded = true;

        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(FilePath)) is { } saved)
                _sizes = new Dictionary<string, Entry>(saved, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // (only a cache: everything is measured again)
        }
    }

    private static void Save()
    {
        try
        {
            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(_sizes));
        }
        catch { }
    }
}
