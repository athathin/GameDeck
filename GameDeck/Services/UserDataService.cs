using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameDeck.Models;

namespace GameDeck.Services;

/// <summary>
/// Everything the user decided per game (favorite, name, hidden, categories, launch options) plus last-played
/// time and playtime, saved in %AppData%\GameDeck\userdata.json.
/// Kept apart from the library file so "Scan for games" never wipes it. Nothing here goes online.
/// </summary>
public class UserDataService
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppInfo.DataFolderName, "userdata.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public class Entry
    {
        public bool Favorite { get; set; }
        public DateTime? LastPlayedUtc { get; set; }
        public long PlaytimeSeconds { get; set; }
        public string? CustomName { get; set; }
        public bool Hidden { get; set; }
        public string? Arguments { get; set; }
        public bool RunAsAdmin { get; set; }
        public List<string>? Tags { get; set; }

        [JsonConverter(typeof(JsonStringEnumConverter))]
        public GameStatus Status { get; set; }

        public int Rating { get; set; }          // 0 = not rated, 1 to 5 stars
        public string? Notes { get; set; }

        public LaunchProfile? Profile { get; set; }
    }

    private Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public void Load()
    {
        // (a damaged file is set aside and the newest daily backup is used instead of starting empty)
        var loaded = AutoBackupService.LoadJson<Dictionary<string, Entry>>(FilePath);
        if (loaded is not null)
            _entries = new Dictionary<string, Entry>(loaded, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Copies the saved values (favorite, playtime, name, hidden, categories, launch options) onto the game.</summary>
    public void Apply(Game game)
    {
        if (!_entries.TryGetValue(game.LaunchTarget, out var entry)) return;

        game.IsFavorite = entry.Favorite;
        game.LastPlayed = entry.LastPlayedUtc;
        game.PlaytimeSeconds = entry.PlaytimeSeconds;
        game.CustomName = entry.CustomName;
        game.IsHidden = entry.Hidden;
        game.LaunchArguments = entry.Arguments;
        game.RunAsAdmin = entry.RunAsAdmin;
        game.Tags = entry.Tags?.ToList() ?? [];   // a copy, so the game and the saved entry never share one list
        game.Status = entry.Status;
        game.Rating = Math.Clamp(entry.Rating, 0, 5);
        game.Notes = entry.Notes;
        game.Profile = entry.Profile?.Clone();   // a copy, like the categories
    }

    /// <summary>Saves the game's launch routine (null or an empty one removes it).</summary>
    public void SetProfile(Game game, LaunchProfile? profile)
    {
        GetEntry(game).Profile = profile is null || profile.IsEmpty ? null : profile;
        Save();
        Apply(game);
    }

    public void SetStatus(Game game, GameStatus status)
    {
        game.Status = status;
        GetEntry(game).Status = status;
        Save();
    }

    public void SetFavorite(Game game, bool favorite)
    {
        game.IsFavorite = favorite;
        GetEntry(game).Favorite = favorite;
        Save();
    }

    public void SetHidden(Game game, bool hidden)
    {
        game.IsHidden = hidden;
        GetEntry(game).Hidden = hidden;
        Save();
    }

    /// <summary>Saves what was entered in "Edit game". Empty values mean "back to the default".</summary>
    public void SaveGameSettings(Game game, string? customName, string? arguments, bool runAsAdmin, List<string> tags,
                                 int rating, string? notes)
    {
        var entry = GetEntry(game);

        customName = customName?.Trim();
        entry.CustomName = string.IsNullOrEmpty(customName) || customName == game.Name ? null : customName;
        entry.Arguments = string.IsNullOrWhiteSpace(arguments) ? null : arguments.Trim();
        entry.RunAsAdmin = runAsAdmin;
        entry.Tags = tags.Count == 0 ? null : tags;
        entry.Rating = Math.Clamp(rating, 0, 5);
        entry.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();

        Save();
        Apply(game);
    }

    /// <summary>Replaces only the game's notes (Big Picture's "Notes…").</summary>
    public void SetNotes(Game game, string? notes)
    {
        GetEntry(game).Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();

        Save();
        Apply(game);
    }

    /// <summary>Replaces the game's categories (used by the right-click Categories menu).</summary>
    public void SetTags(Game game, List<string> tags)
    {
        GetEntry(game).Tags = tags.Count == 0 ? null : tags;
        Save();
        Apply(game);
    }

    /// <summary>Renames a category on every game (also on games that aren't installed right now). Merges if the new name exists.</summary>
    public void RenameCategory(string oldName, string newName)
    {
        foreach (var entry in _entries.Values)
        {
            if (entry.Tags is null || !entry.Tags.Contains(oldName, StringComparer.CurrentCultureIgnoreCase)) continue;

            entry.Tags = entry.Tags
                .Select(t => t.Equals(oldName, StringComparison.CurrentCultureIgnoreCase) ? newName : t)
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        Save();
    }

    /// <summary>Removes a category from every game. The games themselves are untouched.</summary>
    public void DeleteCategory(string name)
    {
        foreach (var entry in _entries.Values)
        {
            if (entry.Tags is null) continue;

            entry.Tags = entry.Tags.Where(t => !t.Equals(name, StringComparison.CurrentCultureIgnoreCase)).ToList();
            if (entry.Tags.Count == 0)
                entry.Tags = null;
        }

        Save();
    }

    /// <summary>
    /// Merges duplicate entries of one game into <paramref name="keep"/>: playtime is added up, the latest last-played
    /// date wins, favorite / categories / status are combined. The others are hidden (not deleted) and their
    /// playtime is zeroed, since it now counts on the kept entry.
    /// </summary>
    public void MergeDuplicates(Game keep, IEnumerable<Game> others)
    {
        var kept = GetEntry(keep);
        var duplicates = others.Where(g => !ReferenceEquals(g, keep)).ToList();

        foreach (var other in duplicates)
        {
            var entry = GetEntry(other);

            kept.PlaytimeSeconds += entry.PlaytimeSeconds;
            entry.PlaytimeSeconds = 0;

            if (entry.LastPlayedUtc is { } played && (kept.LastPlayedUtc is null || played > kept.LastPlayedUtc))
                kept.LastPlayedUtc = played;

            if (entry.Favorite)
            {
                kept.Favorite = true;
                entry.Favorite = false;
            }

            if (kept.Status == GameStatus.None)
                kept.Status = entry.Status;

            if (entry.Tags is { Count: > 0 })
            {
                var tags = (kept.Tags ?? []).Concat(entry.Tags).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
                kept.Tags = tags;
            }

            entry.Hidden = true;
        }

        Save();

        Apply(keep);
        foreach (var other in duplicates)
            Apply(other);
    }

    /// <summary>A copy of everything saved, for backups.</summary>
    public Dictionary<string, Entry> Snapshot() => new(_entries, StringComparer.OrdinalIgnoreCase);

    /// <summary>Restores entries from a backup (they replace the entries of the same games).</summary>
    public void Merge(Dictionary<string, Entry> incoming)
    {
        foreach (var (key, entry) in incoming)
            _entries[key] = entry;

        Save();
    }

    public void RecordLaunch(Game game)
    {
        var now = DateTime.UtcNow;
        game.LastPlayed = now;
        GetEntry(game).LastPlayedUtc = now;
        Save();
    }

    public void AddPlaytime(Game game, TimeSpan time)
    {
        var seconds = (long)time.TotalSeconds;
        if (seconds <= 0) return;

        var entry = GetEntry(game);
        entry.PlaytimeSeconds += seconds;
        game.PlaytimeSeconds = entry.PlaytimeSeconds;
        Save();
    }

    private Entry GetEntry(Game game)
    {
        if (!_entries.TryGetValue(game.LaunchTarget, out var entry))
        {
            entry = new Entry();
            _entries[game.LaunchTarget] = entry;
        }

        return entry;
    }

    private void Save()
    {
        try
        {
            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(_entries, JsonOptions));
        }
        catch { }
    }
}
