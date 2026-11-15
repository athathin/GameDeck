using System.IO;
using System.Text.Json;
using GameDeck.Models;

namespace GameDeck.Services;

/// <summary>One line of the "recent sessions" lists.</summary>
public record SessionRow(string Game, string When, string Duration);

/// <summary>One bar of a stats chart.</summary>
public record ChartBar(string Label, string Value, double Height, double Opacity, string ToolTip);

/// <summary>
/// Remembers every play session (which game, when it started, how long it lasted) in %AppData%\GameDeck\sessions.json,
/// so the Stats tab can show hours per day / week, streaks and a history. Only sessions from now on are known:
/// earlier playtime was saved as a running total without dates. Nothing here goes online.
/// </summary>
public class SessionLog
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameDeck", "sessions.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public class Session
    {
        public string Key { get; set; } = "";        // the game's LaunchTarget, like in userdata.json
        public string Name { get; set; } = "";       // the name at the time, so a removed game still reads well
        public DateTime StartUtc { get; set; }
        public long Seconds { get; set; }
    }

    private List<Session> _sessions = [];

    public int Count => _sessions.Count;

    /// <summary>Total seconds that are recorded here (compared with the running totals to tell old, undated playtime apart).</summary>
    public long LoggedSeconds => _sessions.Sum(s => s.Seconds);

    public void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                _sessions = JsonSerializer.Deserialize<List<Session>>(File.ReadAllText(FilePath)) ?? [];
        }
        catch
        {
            _sessions = [];   // unreadable file: start empty
        }

        _perDay = null;
    }

    public void Add(Game game, DateTime startUtc, TimeSpan length)
    {
        var seconds = (long)length.TotalSeconds;
        if (seconds <= 0) return;

        _sessions.Add(new Session { Key = game.LaunchTarget, Name = game.Name, StartUtc = startUtc, Seconds = seconds });
        _perDay = null;
        Save();
    }

    /// <summary>After duplicates were merged: the hidden entries' sessions now belong to the kept game.</summary>
    public void Reassign(IEnumerable<Game> from, Game to)
    {
        var keys = from.Select(g => g.LaunchTarget).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var changed = false;
        foreach (var session in _sessions.Where(s => keys.Contains(s.Key)))
        {
            session.Key = to.LaunchTarget;
            session.Name = to.Name;
            changed = true;
        }

        if (changed)
            Save();
    }

    // ---------- questions the Stats tab asks ----------

    /// <summary>Seconds played per local calendar day. A session that runs past midnight is split between the days.</summary>
    public Dictionary<DateOnly, long> SecondsPerDay()
    {
        // The Stats screens ask for this several times in a row (charts, streaks, this week): work it out once per change
        // in the sessions. Callers only read the result.
        return _perDay ??= ComputeSecondsPerDay();
    }

    private Dictionary<DateOnly, long>? _perDay;

    private Dictionary<DateOnly, long> ComputeSecondsPerDay()
    {
        var days = new Dictionary<DateOnly, long>();

        foreach (var session in _sessions)
        {
            var start = session.StartUtc.ToLocalTime();
            var end = start.AddSeconds(session.Seconds);

            while (start < end)
            {
                var nextMidnight = start.Date.AddDays(1);
                var sliceEnd = end < nextMidnight ? end : nextMidnight;

                var day = DateOnly.FromDateTime(start);
                days[day] = days.GetValueOrDefault(day) + (long)(sliceEnd - start).TotalSeconds;

                start = sliceEnd;
            }
        }

        return days;
    }

    /// <summary>The last <paramref name="count"/> days, oldest first, today last.</summary>
    public List<ChartBar> LastDays(int count)
    {
        var perDay = SecondsPerDay();
        var today = DateOnly.FromDateTime(DateTime.Now);

        var values = Enumerable.Range(0, count)
            .Select(i => today.AddDays(i - (count - 1)))
            .Select(day => (Day: day, Seconds: perDay.GetValueOrDefault(day)))
            .ToList();

        return ToBars(values, v => v.Day == today ? "Today" : v.Day.ToString("ddd"),
                      v => v.Day.ToString("dddd d MMM"), v => v.Seconds);
    }

    /// <summary>The last <paramref name="count"/> weeks (Monday to Sunday), oldest first, this week last.</summary>
    public List<ChartBar> LastWeeks(int count)
    {
        var perDay = SecondsPerDay();
        var thisMonday = MondayOf(DateOnly.FromDateTime(DateTime.Now));

        var values = Enumerable.Range(0, count)
            .Select(i => thisMonday.AddDays(7 * (i - (count - 1))))
            .Select(monday => (Monday: monday,
                               Seconds: Enumerable.Range(0, 7).Sum(d => perDay.GetValueOrDefault(monday.AddDays(d)))))
            .ToList();

        return ToBars(values, v => v.Monday == thisMonday ? "This wk" : v.Monday.ToString("d MMM"),
                      v => $"Week of {v.Monday:d MMM}", v => v.Seconds);
    }

    public long SecondsThisWeek()
    {
        var perDay = SecondsPerDay();
        var monday = MondayOf(DateOnly.FromDateTime(DateTime.Now));
        return Enumerable.Range(0, 7).Sum(d => perDay.GetValueOrDefault(monday.AddDays(d)));
    }

    /// <summary>Days in a row with some play. The streak is still alive if you played yesterday but not yet today.</summary>
    public (int Current, int Longest) Streaks()
    {
        var days = SecondsPerDay().Where(p => p.Value > 0).Select(p => p.Key).ToHashSet();
        if (days.Count == 0) return (0, 0);

        var today = DateOnly.FromDateTime(DateTime.Now);

        var current = 0;
        var cursor = days.Contains(today) ? today : today.AddDays(-1);
        while (days.Contains(cursor))
        {
            current++;
            cursor = cursor.AddDays(-1);
        }

        var longest = 0;
        foreach (var day in days.Where(d => !days.Contains(d.AddDays(-1))))   // the first day of each run
        {
            var length = 0;
            while (days.Contains(day.AddDays(length)))
                length++;

            longest = Math.Max(longest, length);
        }

        return (current, longest);
    }

    /// <summary>The newest sessions first, optionally only one game's, optionally leaving out some games (hidden ones).</summary>
    public List<SessionRow> Recent(int count, Game? game = null, IReadOnlySet<string>? excludeKeys = null)
    {
        var query = _sessions.AsEnumerable();
        if (game is not null)
            query = query.Where(s => s.Key.Equals(game.LaunchTarget, StringComparison.OrdinalIgnoreCase));
        if (excludeKeys is { Count: > 0 })
            query = query.Where(s => !excludeKeys.Contains(s.Key));

        return query
            .OrderByDescending(s => s.StartUtc)
            .Take(count)
            .Select(s => new SessionRow(s.Name, FormatWhen(s.StartUtc.ToLocalTime()), Game.FormatPlaytime(s.Seconds)))
            .ToList();
    }

    // ---------- helpers ----------

    private static DateOnly MondayOf(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    private static string FormatWhen(DateTime local)
    {
        var day = DateOnly.FromDateTime(local);
        var today = DateOnly.FromDateTime(DateTime.Now);

        var date = day == today ? "Today" : day == today.AddDays(-1) ? "Yesterday" : local.ToString("d MMM yyyy");
        return $"{date}, {local:HH:mm}";
    }

    private const double ChartHeight = 100;

    private static List<ChartBar> ToBars<T>(List<T> values, Func<T, string> label, Func<T, string> when, Func<T, long> seconds)
    {
        var most = Math.Max(1, values.Max(seconds));

        return values.Select(v =>
        {
            var secs = seconds(v);
            var hours = secs / 3600.0;

            return new ChartBar(
                Label: label(v),
                Value: secs <= 0 ? "" : hours >= 1 ? $"{hours:0.#} h" : $"{secs / 60} m",
                Height: secs <= 0 ? 3 : Math.Max(4, ChartHeight * secs / most),
                Opacity: secs <= 0 ? 0.25 : 1,
                ToolTip: $"{when(v)}: {(secs <= 0 ? "no playtime" : Game.FormatPlaytime(secs))}");
        }).ToList();
    }

    private void Save()
    {
        try
        {
            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(_sessions, JsonOptions));
        }
        catch { }
    }
}
