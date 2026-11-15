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
    Source
}

/// <summary>Small app settings, saved in %AppData%\GameDeck\settings.json.</summary>
public static class SettingsService
{
    /// <summary>The tile sizes to choose from (1 = normal).</summary>
    public static readonly double[] TileScales = [0.25, 0.5, 1, 1.25, 1.5];

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameDeck", "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private class Data
    {
        public bool OnlineCovers { get; set; } = true;

        [JsonConverter(typeof(JsonStringEnumConverter))]
        public CloseAction CloseAction { get; set; } = CloseAction.Ask;

        [JsonConverter(typeof(JsonStringEnumConverter))]
        public SortMode SortMode { get; set; } = SortMode.Name;

        public double TileScale { get; set; } = 1.0;

        public bool StartMinimized { get; set; } = true;

        public bool MinimizeOnLaunch { get; set; } = true;

        public bool ShowLaunchToast { get; set; } = true;

        public bool ControllerWake { get; set; } = true;

        public bool AutoUpdate { get; set; } = true;

        [JsonConverter(typeof(JsonStringEnumConverter))]
        public AppTheme Theme { get; set; } = AppTheme.Dark;

        public double BigPictureScale { get; set; } = 1.0;

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
        get => Snap(Current.TileScale);   // an old saved value from the slider days becomes the nearest scale
        set
        {
            Current.TileScale = Snap(value);
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

    private static double Snap(double value) => TileScales.MinBy(s => Math.Abs(s - value));

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
