using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameDeck.Services;

/// <summary>What the window's X button does.</summary>
public enum CloseAction
{
    Ask,              // show the "minimize or close?" pop-up
    MinimizeToTray,
    Exit
}

/// <summary>How the library is ordered.</summary>
public enum SortMode
{
    Name,
    Playtime,
    LastPlayed,
    Source,
    Size,   // the most space on disk first (see DiskUsage)
    Rating  // the highest rated first, unrated games last
}

/// <summary>Small app settings, saved in %AppData%\GameDeck\settings.json.</summary>
public static class SettingsService
{
    /// <summary>The tile sizes to choose from (1 = normal).</summary>
    public static readonly double[] TileScales = [0.25, 0.5, 1, 1.25, 1.5];

    /// <summary>The Big Picture sound volumes to choose from (1 = full).</summary>
    public static readonly double[] SoundVolumes = [0.25, 0.5, 0.75, 1];

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppInfo.DataFolderName, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private class Data
    {
        public bool OnlineCovers { get; set; } = true;

        public bool ImportPlaytime { get; set; } = true;

        public bool LowBatteryWarning { get; set; } = true;

        public bool WeeklyRecap { get; set; } = true;

        public DateOnly? LastRecapWeek { get; set; }

        public int ScreenSaverMinutes { get; set; } = 10;

        public int BreakReminderMinutes { get; set; }

        public bool AskWhereLeftOff { get; set; } = true;

        [JsonConverter(typeof(JsonStringEnumConverter))]
        public CloseAction CloseAction { get; set; } = CloseAction.Ask;

        [JsonConverter(typeof(JsonStringEnumConverter))]
        public SortMode SortMode { get; set; } = SortMode.Name;

        public double TileScale { get; set; } = 1.0;

        public bool StartMinimized { get; set; } = true;

        public bool StartInBigPicture { get; set; }

        public bool MinimizeOnLaunch { get; set; } = true;

        public bool ShowLaunchToast { get; set; } = true;

        public bool ControllerWake { get; set; } = true;
        public bool ControllerShortcuts { get; set; } = true;
        public string? ScreenshotFolder { get; set; }

        public string? BackupFolder { get; set; }

        public string? AccentColor { get; set; }
        public bool NavSounds { get; set; } = true;
        public double SoundVolume { get; set; } = 1.0;

        public bool AutoUpdate { get; set; } = true;

        [JsonConverter(typeof(JsonStringEnumConverter))]
        public AppTheme Theme { get; set; } = AppTheme.Dark;

        public double BigPictureScale { get; set; } = 1.0;

        public bool SwapConfirmBack { get; set; }

        public string? SteamGridDbKey { get; set; }

        public string? IgdbClientId { get; set; }
        public string? IgdbClientSecret { get; set; }

        public bool QuickLaunchEnabled { get; set; } = true;

        // Ctrl + Alt + G: modifier bits as in GlobalHotkey, and a Windows virtual-key code (0x47 = G)
        public uint QuickLaunchModifiers { get; set; } = GlobalHotkey.Control | GlobalHotkey.Alt;
        public uint QuickLaunchKey { get; set; } = 0x47;
    }

    private static readonly Data Current = Load();

    public static SortMode SortMode
    {
        get => Current.SortMode;
        set
        {
            Current.SortMode = value;
            Save();
        }
    }

    /// <summary>How big the game tiles are drawn (1 = normal).</summary>
    public static double TileScale
    {
        get => Snap(TileScales, Current.TileScale);   // an old saved value from the slider days becomes the nearest scale
        set
        {
            Current.TileScale = Snap(TileScales, value);
            Save();
        }
    }

    /// <summary>How loud Big Picture's sounds play: one of <see cref="SoundVolumes"/> (1 = full).</summary>
    public static double SoundVolume
    {
        get => Snap(SoundVolumes, Current.SoundVolume);
        set
        {
            Current.SoundVolume = Snap(SoundVolumes, value);
            Save();
        }
    }

    /// <summary>What the X button does. "Ask" until the user ticks "Don't ask again" in the close pop-up.</summary>
    public static CloseAction CloseAction
    {
        get => Current.CloseAction;
        set
        {
            Current.CloseAction = value;
            Save();
        }
    }

    /// <summary>When GameDeck is started by Windows at sign-in: true = stay in the system tray, false = open the window.</summary>
    public static bool StartMinimized
    {
        get => Current.StartMinimized;
        set
        {
            Current.StartMinimized = value;
            Save();
        }
    }

    /// <summary>
    /// Console-style: when GameDeck opens its window (started by the user, or by Windows with "minimized" off) it opens
    /// Big Picture mode instead of the desktop window. Exiting Big Picture shows the desktop window.
    /// </summary>
    public static bool StartInBigPicture
    {
        get => Current.StartInBigPicture;
        set
        {
            Current.StartInBigPicture = value;
            Save();
        }
    }

    /// <summary>When false, GameDeck never connects to the internet (no cover downloads, no Steam name lookups).</summary>
    public static bool OnlineCovers
    {
        get => Current.OnlineCovers;
        set
        {
            Current.OnlineCovers = value;
            Save();
        }
    }

    /// <summary>
    /// Games show the playtime Steam, GOG Galaxy or the itch app counted (their files on this PC, see LauncherPlaytime) when it
    /// is higher than GameDeck's.
    /// </summary>
    public static bool ImportPlaytime
    {
        get => Current.ImportPlaytime;
        set
        {
            Current.ImportPlaytime = value;
            Save();
        }
    }

    /// <summary>The break reminder times to choose from, in minutes of one session (0 = off).</summary>
    public static readonly int[] BreakReminderChoices = [0, 60, 90, 120, 180];

    /// <summary>A notice every so many minutes of one game session ("You've been playing for 2 hours"); 0 = off (the default).</summary>
    public static int BreakReminderMinutes
    {
        get => BreakReminderChoices.Contains(Current.BreakReminderMinutes) ? Current.BreakReminderMinutes : 0;
        set
        {
            Current.BreakReminderMinutes = value;
            Save();
        }
    }

    /// <summary>
    /// When a game started from GameDeck closes after a real session (10 minutes or more), ask "where did you leave off?" (the answer
    /// shows on the game's page and in Continue playing next time). On by default; the question can always be skipped.
    /// </summary>
    public static bool AskWhereLeftOff
    {
        get => Current.AskWhereLeftOff;
        set
        {
            Current.AskWhereLeftOff = value;
            Save();
        }
    }

    /// <summary>The screen saver times to choose from, in minutes (0 = off).</summary>
    public static readonly int[] ScreenSaverChoices = [0, 5, 10, 20, 30];

    /// <summary>
    /// After how many minutes without input Big Picture fades to its screen saver (covers moving on black, against burn-in on
    /// OLED screens); 0 = never. One of <see cref="ScreenSaverChoices"/>.
    /// </summary>
    public static int ScreenSaverMinutes
    {
        get => ScreenSaverChoices.Contains(Current.ScreenSaverMinutes) ? Current.ScreenSaverMinutes : 10;
        set
        {
            Current.ScreenSaverMinutes = value;
            Save();
        }
    }

    /// <summary>Once a week, a notice with the week before: how long, on how many days, the most played game (WeeklyRecap).</summary>
    public static bool WeeklyRecap
    {
        get => Current.WeeklyRecap;
        set
        {
            Current.WeeklyRecap = value;
            Save();
        }
    }

    /// <summary>The Monday of the last week whose recap was shown (or skipped), so each week is shown once.</summary>
    public static DateOnly? LastRecapWeek
    {
        get => Current.LastRecapWeek;
        set
        {
            Current.LastRecapWeek = value;
            Save();
        }
    }

    /// <summary>A notice when a connected Xbox controller's battery gets low (15%, then 5%), also during games.</summary>
    public static bool LowBatteryWarning
    {
        get => Current.LowBatteryWarning;
        set
        {
            Current.LowBatteryWarning = value;
            Save();
        }
    }

    /// <summary>Hide GameDeck to the system tray each time a game is started from it.</summary>
    public static bool MinimizeOnLaunch
    {
        get => Current.MinimizeOnLaunch;
        set
        {
            Current.MinimizeOnLaunch = value;
            Save();
        }
    }

    /// <summary>Show the small notice in the bottom-right corner when a game starts.</summary>
    public static bool ShowLaunchToast
    {
        get => Current.ShowLaunchToast;
        set
        {
            Current.ShowLaunchToast = value;
            Save();
        }
    }

    /// <summary>The small sounds of Big Picture mode (selection moves, select, back).</summary>
    public static bool NavSounds
    {
        get => Current.NavSounds;
        set
        {
            Current.NavSounds = value;
            Save();
        }
    }

    /// <summary>Open Big Picture mode from a controller (its Home button, or holding View + Menu) while GameDeck sits in the tray.</summary>
    public static bool ControllerWake
    {
        get => Current.ControllerWake;
        set
        {
            Current.ControllerWake = value;
            Save();
        }
    }

    /// <summary>The Home-button shortcuts (Home + a button: volume, songs, Alt + Tab, keyboard, screenshot), also during games.</summary>
    public static bool ControllerShortcuts
    {
        get => Current.ControllerShortcuts;
        set
        {
            Current.ControllerShortcuts = value;
            Save();
        }
    }

    /// <summary>The folder the controller's screenshots are saved in; null = the default places (see ScreenshotService.CaptureAsync).</summary>
    public static string? ScreenshotFolder
    {
        get => Current.ScreenshotFolder;
        set
        {
            Current.ScreenshotFolder = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            Save();
        }
    }

    /// <summary>A backup file is saved in this folder every day (OneDrive, a USB drive...); null = off (the default).</summary>
    public static string? BackupFolder
    {
        get => Current.BackupFolder;
        set
        {
            Current.BackupFolder = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            Save();
            BackupFolderChanged?.Invoke();
        }
    }

    /// <summary>The backup folder was chosen or turned off (the main window saves a first backup there at once).</summary>
    public static event Action? BackupFolderChanged;

    /// <summary>Confirm with the B button and go back with A (the Nintendo arrangement), on any controller.</summary>
    public static bool SwapConfirmBack
    {
        get => Current.SwapConfirmBack;
        set
        {
            Current.SwapConfirmBack = value;
            Save();
        }
    }

    /// <summary>The user's own SteamGridDB key (a free account at steamgriddb.com): when set, it is used as a last source of cover art.</summary>
    public static string? SteamGridDbKey
    {
        get => Current.SteamGridDbKey;
        set
        {
            Current.SteamGridDbKey = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            Save();
        }
    }

    /// <summary>The user's own Twitch app's Client ID (dev.twitch.tv), for IGDB: with the secret, IGDB is asked too.</summary>
    public static string? IgdbClientId
    {
        get => Current.IgdbClientId;
        set
        {
            Current.IgdbClientId = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            Save();
        }
    }

    /// <summary>The user's own Twitch app's Client Secret, for IGDB. Saved on this PC only.</summary>
    public static string? IgdbClientSecret
    {
        get => Current.IgdbClientSecret;
        set
        {
            Current.IgdbClientSecret = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            Save();
        }
    }

    /// <summary>How big everything is drawn in Big Picture mode: 1 (normal), 1.2 or 1.4 (for a TV across the room).</summary>
    public static double BigPictureScale
    {
        get => Current.BigPictureScale is >= 1.0 and <= 1.5 ? Current.BigPictureScale : 1.0;
        set
        {
            Current.BigPictureScale = value;
            Save();
        }
    }

    /// <summary>The color scheme: dark (the default), light, or following Windows.</summary>
    public static AppTheme Theme
    {
        get => Current.Theme;
        set
        {
            Current.Theme = value;
            Save();
        }
    }

    /// <summary>The accent color ("#8037FE", one of ThemeService.Accents); null = the default purple.</summary>
    public static string? AccentColor
    {
        get => Current.AccentColor;
        set
        {
            Current.AccentColor = value;
            Save();
        }
    }

    /// <summary>Look for a newer GameDeck (on GitHub) each time GameDeck starts, and install it.</summary>
    public static bool AutoUpdate
    {
        get => Current.AutoUpdate;
        set
        {
            Current.AutoUpdate = value;
            Save();
        }
    }

    /// <summary>Whether the global quick launch shortcut is active.</summary>
    public static bool QuickLaunchEnabled
    {
        get => Current.QuickLaunchEnabled;
        set
        {
            Current.QuickLaunchEnabled = value;
            Save();
        }
    }

    public static uint QuickLaunchModifiers => Current.QuickLaunchModifiers;

    public static uint QuickLaunchKey => Current.QuickLaunchKey;

    public static void SetQuickLaunchShortcut(uint modifiers, uint virtualKey)
    {
        Current.QuickLaunchModifiers = modifiers;
        Current.QuickLaunchKey = virtualKey;
        Save();
    }

    /// <summary>The step of <paramref name="steps"/> nearest to <paramref name="value"/> (a saved value that is no step becomes one).</summary>
    public static double Snap(double[] steps, double value) => steps.MinBy(s => Math.Abs(s - value));

    // ---------- a slider that picks one of a few steps (tile size, sound volume): its value is the step's index ----------

    /// <summary>The slider position (0, 1, 2...) of <paramref name="value"/> among <paramref name="steps"/>, the nearest step if it is none.</summary>
    public static int StepIndex(double[] steps, double value) => Array.IndexOf(steps, Snap(steps, value));

    /// <summary>The step at a slider position, which is rounded and kept inside the steps.</summary>
    public static double StepAt(double[] steps, double sliderValue) =>
        steps[Math.Clamp((int)Math.Round(sliderValue), 0, steps.Length - 1)];

    private static Data Load()
    {
        // (a damaged file is set aside and the newest daily backup is used instead of the defaults)
        return AutoBackupService.LoadJson<Data>(FilePath) ?? new Data();
    }

    private static void Save()
    {
        try
        {
            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(Current, JsonOptions));
        }
        catch { }
    }
}
