using System.Collections.ObjectModel;
using System.Globalization;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GameDeck.Models;
using GameDeck.Services;
using GameDeck.Views;
using Microsoft.Win32;

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
    private bool _showHidden;
    private bool _ready;            // stops the tab buttons from firing while the window is still being built
    private bool _launching;        // true while a game is being prepared (Vanguard starting), so a second click on Play is ignored
    private bool _exiting;          // true when the app is really quitting (tray "Exit", Windows shutting down)
    private TrayIcon? _tray;
    private readonly GlobalHotkey _hotkey = new();
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

        // the game was closed: bring GameDeck back the way it was left
        _tracker.Closed += ResumeAfterGame;

        // GameDeck was closed (or crashed) while a routine had switched the power plan: put it back
        _ = Task.Run(LaunchRoutineService.RestorePendingPowerPlan);
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
        _userData.Load();   // favorites, last played, playtime, names...
        _sessions.Load();   // dated play sessions for the Stats tab
        UpdateTileSizeButton();
        Refresh();
        _ready = true;

        StartupService.RefreshPath();   // keeps "launch when Windows starts" pointing at this .exe

        // First run (nothing saved yet): scan automatically
        Loaded += async (_, _) =>
        {
            if (_library.Games.Count == 0)
                await ScanAsync();
        };

        AddHandler(MouseLeftButtonUpEvent, new MouseButtonEventHandler((_, _) => _pressedGame = null), handledEventsToo: true);

        Closing += MainWindow_Closing;

        ThemeService.Changed += UpdateFilterButtons;   // the filter icon is colored in code

        // the quick launch shortcut (Ctrl+Alt+G unless changed in Settings)
        _hotkey.Pressed += ToggleQuickLaunch;
        ApplyQuickLaunchHotkey();

        StartControllerWake();   // Home button on a controller opens Big Picture mode

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
        StartInTray();
        Hide();
    }

    /// <summary>Puts the icon in the tray without showing the window (used when Windows starts GameDeck at sign-in).</summary>
    public void StartInTray()
    {
        _tray ??= new TrayIcon(open: ShowFromTray, bigPicture: OpenBigPicture, exit: ExitApp, recentGames: RecentGamesForTray);
        _tray.Show();
        ReleaseMemory();
    }

    /// <summary>The five games played last (hidden ones left out), for the tray menu. Clicking one starts it.</summary>
    private IReadOnlyList<TrayGame> RecentGamesForTray() => _library.Games
        .Where(g => g.LastPlayed is not null && !g.IsHidden)
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
