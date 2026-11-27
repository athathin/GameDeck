using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using GameDeck.Models;
using GameDeck.Services;
using GameDeck.Views;

namespace GameDeck;

public partial class MainWindow : Window
{
    private readonly LibraryService _library = new();
    private readonly UserDataService _userData = new();
    private readonly SessionLog _sessions = new();
    private readonly LaunchRoutineService _routines = new();
    private readonly PlaytimeTracker _tracker;
    private readonly DispatcherTimer _tileSaveTimer;   // saves the tile size shortly after it changes

    private string _view = "All";   // "All", "Favorites", "Recent" or "Stats"
    private SortMode _sort;
    private readonly HashSet<string> _sourceFilters = new(StringComparer.OrdinalIgnoreCase);                 // empty = every launcher
    private readonly HashSet<string> _categoryFilters = new(StringComparer.CurrentCultureIgnoreCase);     // empty = every category
    private readonly HashSet<GameStatus> _statusFilters = [];   // empty = every status
    private readonly HashSet<SmartFilter> _smartFilters = [];   // empty = no smart filter (Never played, Recently added...)
    private readonly HashSet<string> _genreFilters = new(StringComparer.CurrentCultureIgnoreCase);   // empty = every genre
    private bool _showHidden;
    private bool _showAddOns;       // the "DLC and soundtracks" filter: Steam add-ons installed as apps are listed too
    private Dictionary<string, SteamAppInfo.App> _steamApps = [];   // what kind of app each Steam game is (see SteamAppInfo)
    private bool _ready;            // stops the tab buttons from firing while the window is still being built
    private bool _launching;        // true while a game is being prepared (Vanguard starting), so a second click on Play is ignored
    private bool _exiting;          // true when the app is really quitting (tray "Exit", Windows shutting down)
    private TrayIcon? _tray;
    private readonly GlobalHotkey _hotkey = new();
    private readonly LowBatteryWarning _lowBattery = new();
    private readonly BreakReminder _breaks;
    private readonly WeeklyRecapNotice? _recap;   // (null only while the constructor runs: a game can't end before it's made)
    private QuickLaunchWindow? _quickLaunch;

    public ObservableCollection<Game> Games { get; } = new();

    /// <summary>The "pick a game for me" tile. It sits first in <see cref="Games"/>, so always use <see cref="RealGames"/> for counting or picking.</summary>
    private static readonly Game RandomTile = new() { Name = "Pick a game for me", IsRandomTile = true };

    private IEnumerable<Game> RealGames => Games.Where(g => !g.IsRandomTile);

    public static readonly DependencyProperty TileScaleProperty = DependencyProperty.Register(
        nameof(TileScale), typeof(double), typeof(MainWindow),
        new PropertyMetadata(1.0, (d, _) => ((MainWindow)d).OnTileScaleChanged()));

    /// <summary>How big the tiles are drawn (1 = normal), one of <see cref="SettingsService.TileScales"/>. Bound to the tile layout.</summary>
    public double TileScale
    {
        get => (double)GetValue(TileScaleProperty);
        set => SetValue(TileScaleProperty, value);
    }

    public MainWindow()
    {
        _tracker = new PlaytimeTracker(_userData, _sessions);

        // a game is over: undo what its launch routine did (reopen apps, restore the power plan...)
        _tracker.Ended += game => _ = _routines.FinishAsync(game);

        // Big Picture's "Now playing" banner and the game page's Resume button change straight away
        _tracker.Ended += _ => _bigPicture?.GameEnded();

        // the launchers write the hours of a game when it closes: read them again a little later
        _tracker.Ended += async game =>
        {
            if (game.Source is not ("Steam" or "itch.io")) return;

            await Task.Delay(TimeSpan.FromSeconds(15));
            if (SettingsService.ImportPlaytime)
                await LoadLauncherPlaytimeAsync();
            if (game.Source == "Steam")
                await LoadAchievementsAsync();   // (the ones unlocked in that session)
        };

        // the game was closed: bring GameDeck back the way it was left
        _tracker.Closed += ResumeAfterGame;

        // and measure what was skipped while it ran (sizes are only read between games)
        _tracker.Closed += (_, _) => MeasureSizes();

        // a game is over: the library may have changed meanwhile (an update, a game installed), and a weekly recap that
        // waited for the game can show now
        _tracker.Ended += game =>   // (named: a lone "_" parameter would take the "_ =" discards below)
        {
            _ = ScanQuietlyAsync();
            _recap?.Check();
        };

        // GameDeck was closed (or crashed) while a routine had switched the power plan: put it back
        _ = Task.Run(LaunchRoutineService.RestorePendingPowerPlan);
        _ = Task.Run(DisplayModes.RestorePending);   // (and the screen mode a game's routine had switched)
        _sort = SettingsService.SortMode;

        _tileSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _tileSaveTimer.Tick += (_, _) =>
        {
            _tileSaveTimer.Stop();
            SettingsService.TileScale = TileScale;
        };
        TileScale = SettingsService.TileScale;

        InitializeComponent();
        Title = AppInfo.DisplayName;
        TitleBarStyler.Apply(this);   // title bar in the same color as the window
        DataContext = this;

        _library.Load();    // saved library shows instantly
        ScreenshotService.Library = () => _library.Games;   // a game's page tells its captures from other games' (Portal / Portal 2)
        _userData.Load();   // favorites, last played, playtime, names...
        _sessions.Load();   // dated play sessions for the Stats tab

        // Xbox games saved with the wrong app id (it said "can't find the file" at launch): put right, their data follows
        foreach (var (oldKey, newKey) in _library.RepairXboxLinks())
        {
            _userData.RenameKey(oldKey, newKey);
            _sessions.RenameKey(oldKey, newKey);
        }
        UpdateTileSizeButton();
        Refresh();
        _ready = true;
        _ = LoadLauncherPlaytimeAsync();   // the hours Steam, GOG Galaxy and itch counted (read in the background)
        _ = LoadAchievementsAsync();       // and the achievements Steam (and GOG Galaxy) keep
        _ = LoadSteamAppsAsync();          // and which Steam apps are DLC or soundtracks, not games
        FetchGenres();                  // the Steam games' genres not asked for yet
        // each game's size on disk (in the background, sizes older than a few days), once start-up is over: also in the tray
        _sizesTimer.Tick += SizesTimer_Tick;
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(MeasureSizes));

        StartupService.RefreshPath();   // keeps "launch when Windows starts" pointing at this .exe

        // First run (nothing saved yet): scan automatically
        Loaded += async (_, _) =>
        {
            if (_library.Games.Count == 0)
                await ScanAsync();
        };

        // Every start: a quiet scan once start-up is over (also when GameDeck starts in the tray), so games installed or
        // uninstalled since last time show up / go without "Scan for games". Then again each time GameDeck comes back to the
        // front (at most once a minute): a game just installed or uninstalled in its launcher.
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            await ScanQuietlyAsync(spaced: true);   // (skipped when coming to the front just did one)
        }));
        Activated += (_, _) => _ = ScanQuietlyAsync(spaced: true);

        AddHandler(MouseLeftButtonUpEvent, new MouseButtonEventHandler((_, _) => _pressedGame = null), handledEventsToo: true);

        Closing += MainWindow_Closing;

        ThemeService.Changed += UpdateFilterButtons;   // the filter icon is colored in code

        // the quick launch shortcut (Ctrl+Alt+G unless changed in Settings)
        _hotkey.Pressed += ToggleQuickLaunch;
        ApplyQuickLaunchHotkey();

        StartControllerWake();   // Home button on a controller opens Big Picture mode
        _lowBattery.Start();     // a notice when the controller's battery runs low (also during games)
        StartDailyUpdateCheck(); // left running for days: a newer version is still found (installed at the next start)
        StartFolderBackups();    // a backup every day in the folder the user chose (Settings: off by default)

        // "you've been playing for 2 hours" (Settings: off by default)
        _breaks = new BreakReminder(() => _tracker.RunningGame is { } game && _tracker.RunningSinceUtc is { } since ? (game, since) : null);
        _breaks.Start();

        // "your week in games": once a week, the week before (Settings: on by default), once start-up is over
        _recap = new WeeklyRecapNotice(_sessions,
            () => _library.Games.Where(g => g.IsHidden).Select(g => g.LaunchTarget).ToHashSet(StringComparer.OrdinalIgnoreCase),
            () => _tracker.IsTracking);
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(_recap.Start));

        // Windows shutting down / signing out must never be blocked by the "minimize or close?" pop-up
        Application.Current.SessionEnding += (_, _) => _exiting = true;
    }

    private void OnTileScaleChanged()
    {
        UpdateTileSizeButton();
        if (!_ready) return;

        _tileSaveTimer.Stop();
        _tileSaveTimer.Start();   // restarts the countdown: only the final size gets written to disk
    }

    // ---------- closing / system tray ----------

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (!_exiting)
        {
            var action = SettingsService.CloseAction;

            if (action == CloseAction.Ask)
            {
                var dialog = new CloseChoiceDialog { Owner = IsVisible ? this : null };
                if (dialog.ShowDialog() != true)
                {
                    e.Cancel = true;   // pop-up dismissed: keep the window open
                    return;
                }

                action = dialog.Choice;
                if (dialog.DontAskAgain)
                    SettingsService.CloseAction = action;
            }

            if (action == CloseAction.MinimizeToTray)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }
        }

        // Really closing: save the time played so far for any game still running
        _tileSaveTimer.Stop();
        SettingsService.TileScale = TileScale;
        _tracker.FlushAll();
        _hotkey.Dispose();
        _lowBattery.Dispose();
        _breaks.Dispose();
        _recap?.Dispose();
        _updateTimer.Stop();
        StopFolderBackups();
        _wake?.Dispose();
        _tray?.Dispose();
    }

    // ---------- quick launch (global shortcut) ----------

    /// <summary>
    /// Registers (or removes) the shortcut according to the settings. Whatever the user chose is used as is: if another
    /// program already owns the combination, Windows refuses it and the shortcut simply doesn't fire.
    /// </summary>
    private void ApplyQuickLaunchHotkey()
    {
        if (SettingsService.QuickLaunchEnabled)
            _hotkey.Register(SettingsService.QuickLaunchModifiers, SettingsService.QuickLaunchKey);
        else
            _hotkey.Unregister();
    }

    private void SettingsPanel_QuickLaunchChanged(object? sender, EventArgs e) => ApplyQuickLaunchHotkey();

    /// <summary>The shortcut opens the quick launch box, or closes it if it's already open.</summary>
    private void ToggleQuickLaunch()
    {
        if (_quickLaunch is not null)
        {
            _quickLaunch.Close();
            return;
        }

        _quickLaunch = new QuickLaunchWindow(_library.Games, game => _ = LaunchGameAsync(game));
        _quickLaunch.Closed += (_, _) => _quickLaunch = null;
        _quickLaunch.Show();
    }

    private void HideToTray()
    {
        _settingsWindow?.Close();   // (it would stay on screen without the window it belongs to)
        StartInTray();
        Hide();
    }

    /// <summary>Puts the icon in the tray without showing the window (used when Windows starts GameDeck at sign-in).</summary>
    public void StartInTray()
    {
        _tray ??= new TrayIcon(open: ShowFromTray, settings: OpenSettingsFromTray, bigPicture: OpenBigPicture, exit: ExitApp,
                               recentGames: RecentGamesForTray,
                               afterGame: AfterGameForTray, setAfterGame: choice => _afterGame = choice);
        _tray.Show();
        ReleaseMemory();
    }

    /// <summary>The five games played last (hidden ones left out), for the tray menu. Clicking one starts it.</summary>
    private IReadOnlyList<TrayGame> RecentGamesForTray() => _library.Games
        .Where(g => g.LastPlayed is not null && !g.IsHidden && !g.IsAddOn)
        .OrderByDescending(g => g.LastPlayed)
        .Take(5)
        .Select(g => new TrayGame(g.DisplayName, CoverService.FindCachedCover(g), () => _ = LaunchGameAsync(g)))
        .ToList();

    /// <summary>
    /// In the tray nothing is on screen, so drop the cover pictures (they reload when the window comes back),
    /// compact the heap and give the freed memory back to Windows.
    /// </summary>
    private void ReleaseMemory()
    {
        foreach (var game in _library.Games)
            game.Cover = null;

        ClearMedia();   // the Media tab's thumbnails too (filled again when the window comes back)

        // wait until the window has finished hiding, otherwise the tiles would still be in use
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, MemoryTrimmer.Trim);
    }

    /// <summary>Called when GameDeck is started a second time: shows this window (from the tray, minimized or behind others).</summary>
    public void BringToFront()
    {
        ShowFromTray();

        // Activate() alone can leave the window behind others; toggling Topmost reliably raises it
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void ShowFromTray()
    {
        // opening the desktop window (tray icon, GameDeck started again) replaces Big Picture instead of piling on top of it
        if (_bigPicture is not null)
        {
            _mainHiddenForBigPicture = false;
            _bigPicture.Close();
        }

        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;

        Activate();
        _tray?.Hide();

        _ = LoadVisibleCoversAsync();   // the covers were dropped while in the tray

        if (_view == "Media")
            ShowMedia();                // (its thumbnails too)
    }

    /// <summary>The tray menu's "Settings": the settings window belongs to the main window, so that comes back too, behind it.</summary>
    private void OpenSettingsFromTray()
    {
        ShowFromTray();
        OpenSettings();
    }

    /// <summary>Closes GameDeck right now, from an error window: the playtime so far is saved and nothing is asked.</summary>
    public void ForceExit()
    {
        _exiting = true;
        Application.Current.Shutdown(1);
    }

    private void ExitApp()
    {
        _exiting = true;
        Close();
    }

}
