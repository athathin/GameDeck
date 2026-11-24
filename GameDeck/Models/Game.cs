using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace GameDeck.Models;

/// <summary>Where a game is in the user's backlog.</summary>
public enum GameStatus
{
    None,
    Playing,
    Completed,
    UpNext,
    Dropped
}

public static class GameStatusExtensions
{
    /// <summary>The name shown to the user ("Up next", not "UpNext").</summary>
    public static string Label(this GameStatus status) => status switch
    {
        GameStatus.UpNext => "Up next",
        GameStatus.None => "No status",
        _ => status.ToString()
    };
}

public class Game : INotifyPropertyChanged
{
    // ---------- saved in the library file ----------
    public string Name { get; set; } = "";
    public string Source { get; set; } = "";       // "Steam", "Epic", "Manual"...
    public string LaunchTarget { get; set; } = ""; // an .exe path or a URI like steam://rungameid/220

    /// <summary>Command that uninstalls the game (GOG's unins000.exe, or the registered uninstall string). Null if unknown.</summary>
    public string? UninstallCommand { get; set; }

    /// <summary>The game's install folder. Used to recognise the running game for playtime tracking.</summary>
    public string? InstallDir { get; set; }

    /// <summary>True for the special "pick a game for me" tile at the start of the grid (it is not a real game).</summary>
    [JsonIgnore]
    public bool IsRandomTile { get; init; }

    // ---------- cover ----------
    private ImageSource? _cover;

    /// <summary>The cover image. Loaded in the background after the tile appears, never saved to disk.</summary>
    [JsonIgnore]
    public ImageSource? Cover
    {
        get => _cover;
        set => Set(ref _cover, value);
    }

    private bool _hasCustomCover;

    /// <summary>True when the user picked this game's cover (decides if "Remove cover art" is shown).</summary>
    [JsonIgnore]
    public bool HasCustomCover
    {
        get => _hasCustomCover;
        set => Set(ref _hasCustomCover, value);
    }

    // ---------- the user's own changes (kept in userdata.json, not in the library file) ----------
    private string? _customName;

    /// <summary>A name the user typed in "Edit game". Only changes what is shown: Name stays the real one (covers are matched by it).</summary>
    [JsonIgnore]
    public string? CustomName
    {
        get => _customName;
        set
        {
            if (Set(ref _customName, value))
                Notify(nameof(DisplayName));
        }
    }

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(CustomName) ? Name : CustomName;

    private bool _isHidden;

    [JsonIgnore]
    public bool IsHidden
    {
        get => _isHidden;
        set
        {
            if (Set(ref _isHidden, value))
                Notify(nameof(HideMenuText));
        }
    }

    [JsonIgnore]
    public string HideMenuText => IsHidden ? "Unhide" : "Hide from library";

    /// <summary>Extra command-line arguments for the game (empty = none).</summary>
    [JsonIgnore]
    public string? LaunchArguments { get; set; }

    [JsonIgnore]
    public bool RunAsAdmin { get; set; }

    /// <summary>The user's own categories for this game.</summary>
    [JsonIgnore]
    public List<string> Tags { get; set; } = [];

    /// <summary>What GameDeck does before / after starting this game (null = nothing).</summary>
    [JsonIgnore]
    public LaunchProfile? Profile { get; set; }

    private int _rating;

    /// <summary>The user's rating: 0 (not rated) to 5 stars.</summary>
    [JsonIgnore]
    public int Rating
    {
        get => _rating;
        set
        {
            if (Set(ref _rating, value))
            {
                Notify(nameof(RatingText));
                Notify(nameof(HasRating));
            }
        }
    }

    [JsonIgnore]
    public bool HasRating => Rating > 0;

    /// <summary>The rating as stars, e.g. ★★★★☆ (empty when not rated).</summary>
    [JsonIgnore]
    public string RatingText => Rating <= 0 ? "" : new string('★', Rating) + new string('☆', 5 - Rating);

    private string? _notes;

    /// <summary>The user's own notes about the game.</summary>
    [JsonIgnore]
    public string? Notes
    {
        get => _notes;
        set
        {
            if (Set(ref _notes, value))
                Notify(nameof(HasNotes));
        }
    }

    [JsonIgnore]
    public bool HasNotes => !string.IsNullOrWhiteSpace(Notes);

    private GameStatus _status;

    /// <summary>Playing / Completed / Up next / Dropped, or None.</summary>
    [JsonIgnore]
    public GameStatus Status
    {
        get => _status;
        set
        {
            if (Set(ref _status, value))
            {
                Notify(nameof(HasStatus));
                Notify(nameof(StatusText));
            }
        }
    }

    [JsonIgnore]
    public bool HasStatus => Status != GameStatus.None;

    [JsonIgnore]
    public string StatusText => Status == GameStatus.None ? "" : Status.Label();

    /// <summary>True while the game's tile is on screen. The cover is only kept in memory during that time.</summary>
    [JsonIgnore]
    public bool CoverWanted { get; set; }

    /// <summary>True when the game starts straight from an .exe (so arguments and "run as administrator" apply).</summary>
    [JsonIgnore]
    public bool IsDirectExe => File.Exists(LaunchTarget);

    /// <summary>Arguments can be passed to .exe games and to Steam games; other launchers start the game themselves.</summary>
    [JsonIgnore]
    public bool SupportsArguments => SteamAppId is not null || IsDirectExe;

    // ---------- favorites / playtime ----------
    private bool _isFavorite;

    [JsonIgnore]
    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (Set(ref _isFavorite, value))
                Notify(nameof(FavoriteMenuText));
        }
    }

    private DateTime? _lastPlayed;

    [JsonIgnore]
    public DateTime? LastPlayed
    {
        get => _lastPlayed;
        set
        {
            if (Set(ref _lastPlayed, value))
                Notify(nameof(LastPlayedText));
        }
    }

    /// <summary>Playtime for the details page, e.g. "12 h 30 min" or "Not played yet".</summary>
    [JsonIgnore]
    public string PlaytimeText => PlaytimeSeconds <= 0 ? "Not played yet" : FormatPlaytime(PlaytimeSeconds);

    /// <summary>Last-played date for the details page, e.g. "9 Nov 2026" or "Never".</summary>
    [JsonIgnore]
    public string LastPlayedText => LastPlayed is { } time ? time.ToLocalTime().ToString("d MMM yyyy") : "Never";

    private long _playtimeSeconds;

    [JsonIgnore]
    public long PlaytimeSeconds
    {
        get => _playtimeSeconds;
        set
        {
            if (Set(ref _playtimeSeconds, value))
            {
                Notify(nameof(PlaytimeShort));
                Notify(nameof(PlaytimeText));
                Notify(nameof(HasPlaytime));
            }
        }
    }

    [JsonIgnore]
    public string FavoriteMenuText => IsFavorite ? "Remove from favorites" : "Add to favorites";

    [JsonIgnore]
    public bool HasPlaytime => PlaytimeSeconds > 0;

    /// <summary>Short playtime shown on the tile, e.g. "45 min" or "12 h 30 min".</summary>
    [JsonIgnore]
    public string PlaytimeShort => PlaytimeSeconds <= 0 ? "" : FormatPlaytime(PlaytimeSeconds);

    public static string FormatPlaytime(long seconds) => seconds switch
    {
        < 60 => "< 1 min",
        < 3600 => $"{seconds / 60} min",
        _ => $"{seconds / 3600} h {seconds % 3600 / 60} min"
    };

    // ---------- helpers ----------

    /// <summary>Where the game comes from, as shown to the user: "Steam", "Epic"... or "Added by hand".</summary>
    [JsonIgnore]
    public string SourceText => Source == "Manual" ? "Added by hand" : Source;

    /// <summary>The Steam app id for Steam games, otherwise null.</summary>
    [JsonIgnore]
    public string? SteamAppId =>
        LaunchTarget.StartsWith("steam://rungameid/", StringComparison.OrdinalIgnoreCase)
            ? LaunchTarget["steam://rungameid/".Length..]
            : null;

    /// <summary>Folder whose running programs count as "this game is running". Null if unknown.</summary>
    [JsonIgnore]
    public string? TrackingFolder =>
        !string.IsNullOrWhiteSpace(InstallDir)
            ? InstallDir
            : File.Exists(LaunchTarget) ? Path.GetDirectoryName(LaunchTarget) : null;

    /// <summary>Colourful gradient shown while a game has no cover (colour is derived from the name).</summary>
    [JsonIgnore]
    public Brush PlaceholderBrush
    {
        get
        {
            // every tile asks for this each time it is drawn: build it once per name (it is frozen, so it can be shared)
            if (_placeholder is not null && _placeholderFor == Name)
                return _placeholder;

            var hash = Name.Aggregate(17, (h, c) => h * 31 + c);
            var hue = Math.Abs(hash % 360);

            var brush = new LinearGradientBrush(FromHsv(hue, 0.45, 0.45), FromHsv((hue + 30) % 360, 0.55, 0.22), 90);
            brush.Freeze();

            _placeholderFor = Name;
            return _placeholder = brush;
        }
    }

    private Brush? _placeholder;
    private string? _placeholderFor;

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Notify(name!);
        return true;
    }

    private void Notify(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static Color FromHsv(double h, double s, double v)
    {
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;

        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x)
        };

        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}