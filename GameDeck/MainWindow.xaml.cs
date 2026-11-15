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

    // ---------- Big Picture mode (full screen, for a controller) ----------

    private BigPictureWindow? _bigPicture;
    private ControllerWakeService? _wake;

    /// <summary>
    /// Lets a controller open Big Picture mode while GameDeck sits in the tray (Home button, or View + Menu held).
    /// It only listens while that's safe: setting on, Big Picture closed, no game from GameDeck running.
    /// </summary>
    private void StartControllerWake()
    {
        _wake = new ControllerWakeService(() =>
            SettingsService.ControllerWake && _bigPicture is null && !_tracker.IsTracking);

        _wake.WakeRequested += OpenBigPicture;
        _wake.Begin();
    }

    // ---------- coming back after a game ----------

    /// <summary>Where GameDeck was when a game was started, so it can return there when the game closes.</summary>
    private enum ResumeMode { None, Desktop, BigPicture }

    private readonly Dictionary<Game, ResumeMode> _resume = [];

    // true while the desktop window is hidden only because Big Picture is open (it returns when Big Picture closes)
    private bool _mainHiddenForBigPicture;

    // Big Picture closes when a game starts: remember its tab and selected game for the way back
    private int _bigPictureTab;
    private Game? _bigPictureGame;

    private void ResumeAfterGame(Game game)
    {
        if (!_resume.Remove(game, out var mode)) return;

        // another game from GameDeck is still running: stay out of its way
        if (_tracker.IsTracking) return;

        if (mode == ResumeMode.BigPicture)
            OpenBigPicture(_bigPictureTab, _bigPictureGame);
        else if (mode == ResumeMode.Desktop)
            BringToFront();
    }

    /// <summary>Opens the full-screen, controller-friendly library (or brings it back to the front if it's already open).</summary>
    private void OpenBigPicture() => OpenBigPicture(null, null);

    private void OpenBigPicture(int? tab, Game? selected)
    {
        if (_bigPicture is not null)
        {
            WindowFocus.BringToFront(_bigPicture);
            return;
        }

        // hidden games stay hidden here too, like in the library
        var games = _library.Games.Where(g => !g.IsHidden).ToList();

        var hiddenKeys = _library.Games.Where(g => g.IsHidden)
            .Select(g => g.LaunchTarget)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        _bigPicture = new BigPictureWindow(
            games,
            hiddenKeys,
            _sessions,
            launch: game => _ = LaunchGameAsync(game, ResumeMode.BigPicture),
            toggleFavorite: game => _userData.SetFavorite(game, !game.IsFavorite),
            minimizeApp: () =>
            {
                _mainHiddenForBigPicture = false;   // it goes to the tray and stays there
                HideToTray();                       // "Minimize GameDeck" in the Power menu
            },
            exitApp: ExitApp,                       // "Exit GameDeck"
            actions: BigPictureActions(),
            gameMenu: BigPictureGameMenu(),
            onlineCoversChanged: () => SettingsPanel_OnlineCoversChanged(null, EventArgs.Empty),
            quickLaunchChanged: ApplyQuickLaunchHotkey);

        _wake?.Suspend();   // Big Picture reads the controllers itself now

        var screen = _bigPicture;
        screen.Closed += (_, _) =>
        {
            _bigPictureTab = screen.Tab;
            _bigPictureGame = screen.SelectedGame;
            _bigPicture = null;

            if (_view == "Favorites")
                Refresh();                       // favorites may have changed in there

            // Desktop mode and Big Picture are never both on screen: the window that made way for Big Picture comes back
            // now, unless a game is starting (LaunchGameAsync decides) or GameDeck is being minimized / closed
            if (_mainHiddenForBigPicture && !screen.LaunchedGame && !screen.KeepMainHidden)
            {
                _mainHiddenForBigPicture = false;
                ShowFromTray();
            }
            else if (IsVisible)
            {
                _ = LoadVisibleCoversAsync();    // tiles in the main window get their covers back
            }
        };

        if (tab is not null)
            _bigPicture.Restore(tab.Value, selected);

        // Big Picture replaces the desktop window while it is open: otherwise Alt+Tab (or closing Big Picture) lands on a
        // desktop window that was sitting behind it. (Hide, not the tray: no icon for something that comes right back.)
        if (IsVisible)
        {
            _mainHiddenForBigPicture = true;
            Hide();
        }

        _bigPicture.Show();
        WindowFocus.BringToFront(_bigPicture);   // also when it was opened from a controller while another program was in front
    }

    private void BigPictureButton_Click(object sender, RoutedEventArgs e) => OpenBigPicture();

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

    private void ExitApp()
    {
        _exiting = true;
        Close();
    }

    // ---------- showing the library ----------

    /// <param name="reapplyUserData">False when only the search text changed: nothing the user saved is different, so the
    /// favorites, names, categories... don't have to be copied onto every game again for each typed letter.</param>
    private void Refresh(bool reapplyUserData = true)
    {
        if (reapplyUserData)
        {
            foreach (var game in _library.Games)
                _userData.Apply(game);
        }

        // a launcher / category that no longer has any games (after a scan) stops being a filter
        _sourceFilters.RemoveWhere(source => source != ManualSource && !_library.Games.Any(g => g.Source == source));
        _categoryFilters.RemoveWhere(category => !_library.Games.Any(g => HasTag(g, category)));
        UpdateFilterButtons();

        var stats = _view == "Stats";
        GameList.Visibility = stats ? Visibility.Collapsed : Visibility.Visible;
        StatsPanel.Visibility = stats ? Visibility.Visible : Visibility.Collapsed;
        FilterBar.Visibility = stats ? Visibility.Collapsed : Visibility.Visible;
        if (stats)
        {
            ShowStats();
            EmptyText.Visibility = Visibility.Collapsed;
            StatusText.Text = $"{_library.Games.Count} games";
            return;
        }

        var query = SearchBox.Text.Trim();

        IEnumerable<Game> shown = _library.Games;
        if (!_showHidden)
            shown = shown.Where(g => !g.IsHidden);
        if (_sourceFilters.Count > 0)
            shown = shown.Where(g => _sourceFilters.Contains(g.Source));
        if (_categoryFilters.Count > 0)
            shown = shown.Where(g => g.Tags.Any(_categoryFilters.Contains));
        if (_statusFilters.Count > 0)
            shown = shown.Where(g => _statusFilters.Contains(g.Status));
        if (query.Length > 0)
            shown = shown.Where(g => g.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                                     g.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase));

        shown = _view switch
        {
            "Favorites" => Sort(shown.Where(g => g.IsFavorite)),
            "Recent" => shown.Where(g => g.LastPlayed is not null)
                             .OrderByDescending(g => g.LastPlayed)
                             .Take(20),
            _ => Sort(shown)
        };

        var list = shown.ToList();
        var realCount = list.Count;
        if (realCount > 0)
            list.Insert(0, RandomTile);   // the "pick a game for me" tile comes first
        SyncGames(list);

        StatusText.Text = _view switch
        {
            "Favorites" => $"{realCount} favorites",
            "Recent" => $"{realCount} recent",
            _ => $"{realCount} games"
        };

        var filtered = query.Length > 0 || _sourceFilters.Count > 0 || _categoryFilters.Count > 0 || _statusFilters.Count > 0;
        EmptyText.Text = filtered
            ? "No games found."
            : _view switch
            {
                "Favorites" => "No favorites yet. Right-click a game and choose \"Add to favorites\".",
                "Recent" => "Nothing played yet. Games you launch from GameDeck show up here.",
                _ => "No games found. Open Settings (the gear) and choose \"Scan for games\" or \"Add games\"."
            };
        EmptyText.Visibility = realCount == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private IOrderedEnumerable<Game> Sort(IEnumerable<Game> games) => _sort switch
    {
        SortMode.Playtime => games.OrderByDescending(g => g.PlaytimeSeconds).ThenBy(ByName, StringComparer.CurrentCultureIgnoreCase),
        SortMode.LastPlayed => games.OrderByDescending(g => g.LastPlayed ?? DateTime.MinValue).ThenBy(ByName, StringComparer.CurrentCultureIgnoreCase),
        SortMode.Source => games.OrderBy(g => g.Source, StringComparer.CurrentCultureIgnoreCase).ThenBy(ByName, StringComparer.CurrentCultureIgnoreCase),
        _ => games.OrderBy(ByName, StringComparer.CurrentCultureIgnoreCase)
    };

    private static string ByName(Game game) => game.DisplayName;

    private static bool HasTag(Game game, string tag) =>
        game.Tags.Contains(tag, StringComparer.CurrentCultureIgnoreCase);

    /// <summary>
    /// Makes Games match the list with as few changes as possible. Clearing and refilling the collection would throw
    /// every tile away (and its cover) each time you type a letter in the search box.
    /// </summary>
    private void SyncGames(List<Game> target)
    {
        var wanted = new HashSet<Game>(target);

        for (var i = Games.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(Games[i]))
                Games.RemoveAt(i);
        }

        for (var i = 0; i < target.Count; i++)
        {
            if (i < Games.Count && ReferenceEquals(Games[i], target[i])) continue;

            var current = Games.IndexOf(target[i]);
            if (current >= 0)
                Games.Move(current, i);
            else
                Games.Insert(i, target[i]);
        }
    }

    // ---------- covers: only tiles on screen keep their cover in memory ----------

    private void Tile_Loaded(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not Game { IsRandomTile: false } game) return;

        game.CoverWanted = true;
        if (game.Cover is null)
            _ = CoverService.LoadCoverAsync(game);
    }

    private void Tile_Unloaded(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not Game { IsRandomTile: false } game) return;

        game.CoverWanted = false;
        game.Cover = null;   // scrolled away (or filtered out): free the image, it reloads from disk when needed
    }

    private Task LoadVisibleCoversAsync() =>
        Task.WhenAll(Games.Where(g => g.CoverWanted && g.Cover is null).Select(CoverService.LoadCoverAsync));

    // ---------- search ----------

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_ready)
            Refresh(reapplyUserData: false);
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;

        SearchBox.Clear();
        Keyboard.ClearFocus();
    }

    private void ClearSearch_MouseDown(object sender, MouseButtonEventArgs e)
    {
        SearchBox.Clear();
        e.Handled = true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // F11 opens Big Picture mode from anywhere in the window (not while typing a new quick launch shortcut)
        if (e.Key == Key.F11 && !SettingsPanel.IsCapturingShortcut)
        {
            OpenBigPicture();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && FilterPopup.IsOpen)
        {
            FilterPopup.IsOpen = false;
            e.Handled = true;
            return;
        }

        // On a game's page Esc goes back, nothing else applies
        if (DetailsPanel.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Escape)
            {
                CloseDetails();
                e.Handled = true;
            }
            return;
        }

        // On the settings screen Esc goes back, nothing else applies
        if (SettingsPanel.Visibility == Visibility.Visible)
        {
            // while a new shortcut is being captured, Esc only cancels that
            if (e.Key == Key.Escape && !SettingsPanel.IsCapturingShortcut)
            {
                CloseSettings();
                e.Handled = true;
            }
            return;
        }

        // Ctrl+F jumps to the search box
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (_view == "Stats")
                AllTab.IsChecked = true;

            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
    }

    private void ViewTab_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready || sender is not RadioButton { Tag: string view }) return;

        _view = view;
        Refresh();
    }

    // ---------- sort and filters ----------

    private void UpdateFilterButtons()
    {
        // the icon turns purple while a launcher / category filter or "show hidden" is on
        var active = HasActiveFilters;
        FilterButton.Foreground = active ? new SolidColorBrush(Color.FromRgb(0x80, 0x37, 0xFE)) : Brushes.White;
        UpdateFilterChips();
    }

    private void UpdateTileSizeButton()
    {
        var index = Array.FindIndex(SettingsService.TileScales, s => Math.Abs(s - TileScale) < 0.001);
        if (TileSlider is null || index < 0) return;

        TileSlider.Value = index;
        TileSlider.ToolTip = $"Tile size: {FormatScale(TileScale)}×";
    }

    private void TileSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;   // ignore the slider's own start-up values

        var index = Math.Clamp((int)Math.Round(e.NewValue), 0, SettingsService.TileScales.Length - 1);
        TileScale = SettingsService.TileScales[index];
    }

    private static string FormatScale(double scale) => scale.ToString("0.##", CultureInfo.InvariantCulture);

    private static MenuItem MenuEntry(string header, bool isChecked, Action onClick, bool enabled = true)
    {
        // a TextBlock header, so a "_" in a name (a category called "Co_op") isn't swallowed as a keyboard shortcut marker
        var item = new MenuItem { Header = new TextBlock { Text = header }, IsChecked = isChecked, IsEnabled = enabled };
        item.Click += (_, _) => onClick();
        return item;
    }

    private bool HasActiveFilters => _sourceFilters.Count > 0 || _categoryFilters.Count > 0 || _statusFilters.Count > 0 || _showHidden;

    /// <summary>The filter icon: opens the Library Filters panel. (While it's open the icon can't be clicked, so clicking it closes the panel.)</summary>
    private void FilterButton_Click(object sender, RoutedEventArgs e)
    {
        BuildFilterPanel();

        // the panel is wider than the icon: line up its right edge with the icon
        FilterPopup.HorizontalOffset = FilterButton.ActualWidth - 540 - 14;
        FilterPopup.IsOpen = true;
    }

    // ---------- filter chips (in the search box) ----------

    /// <summary>Games added with "Add games" have the source "Manual"; the filters call them "Other".</summary>
    private const string ManualSource = "Manual";

    private static string LauncherName(string source) => source == ManualSource ? "Other" : source;

    private void UpdateFilterChips()
    {
        FilterChips.Children.Clear();

        foreach (var source in _sourceFilters.OrderBy(s => s == ManualSource).ThenBy(s => s, StringComparer.CurrentCultureIgnoreCase).ToList())
            AddFilterChip(LauncherName(source), () => _sourceFilters.Remove(source));

        foreach (var category in _categoryFilters.OrderBy(c => c, StringComparer.CurrentCultureIgnoreCase).ToList())
            AddFilterChip(category, () => _categoryFilters.Remove(category));

        foreach (var status in _statusFilters.OrderBy(s => s).ToList())
            AddFilterChip(status.Label(), () => _statusFilters.Remove(status));

        if (_showHidden)
            AddFilterChip("Hidden games", () => _showHidden = false);

        var any = FilterChips.Children.Count > 0;
        FilterChips.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        ClearAllFiltersButton.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddFilterChip(string text, Action remove)
    {
        var close = new TextBlock
        {
            Text = "✕",
            Foreground = Brushes.White,
            FontSize = 11,
            Margin = new Thickness(7, 1, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            ToolTip = "Remove this filter"
        };
        close.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            remove();
            FiltersChanged();
        };

        FilterChips.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x80, 0x37, 0xFE)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 3, 7, 3),
            Margin = new Thickness(0, 0, 6, 6),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new TextBlock { Text = text, Foreground = Brushes.White, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 200 },
                    close
                }
            }
        });
    }

    /// <summary>After a filter was changed from outside the panel (a chip's x): refresh the list, and the panel if it's open.</summary>
    private void FiltersChanged()
    {
        if (FilterPopup.IsOpen)
            BuildFilterPanel();

        Refresh();
    }

    /// <summary>The x next to the search box: every launcher, every category, hidden games not shown. The sort order stays.</summary>
    private void ClearAllFilters_MouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        _sourceFilters.Clear();
        _categoryFilters.Clear();
        _statusFilters.Clear();
        _showHidden = false;
        FiltersChanged();
    }

    /// <summary>Fills the panel with the launchers and categories that exist right now, ticked as currently chosen.</summary>
    private void BuildFilterPanel()
    {
        // launchers (pick any number: games from any ticked launcher are shown)
        LauncherList.Children.Clear();
        foreach (var source in _library.Games.Select(g => g.Source).Append(ManualSource).Distinct()   // "Other" is always offered
                     .OrderBy(s => s == ManualSource)                       // and goes last
                     .ThenBy(s => s, StringComparer.CurrentCultureIgnoreCase))
        {
            var chosen = source;
            LauncherList.Children.Add(FilterCheckBox(LauncherName(chosen), _sourceFilters.Contains(chosen), on =>
            {
                if (on) _sourceFilters.Add(chosen); else _sourceFilters.Remove(chosen);
                Refresh();
            }));
        }

        // categories
        CategoryList.Children.Clear();
        var categories = AllCategories();
        if (categories.Count == 0)
        {
            CategoryList.Children.Add(new TextBlock
            {
                Text = "None yet. Right-click a game, then Edit game…",
                Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x91, 0xA0)),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap
            });
        }
        foreach (var category in categories)
        {
            var chosen = category;
            CategoryList.Children.Add(FilterCheckBox(chosen, _categoryFilters.Contains(chosen), on =>
            {
                if (on) _categoryFilters.Add(chosen); else _categoryFilters.Remove(chosen);
                Refresh();
            }));
        }

        // status (Playing / Completed / ...)
        StatusList.Children.Clear();
        foreach (var status in Enum.GetValues<GameStatus>().Where(s => s != GameStatus.None))
        {
            var chosen = status;
            StatusList.Children.Add(FilterCheckBox(chosen.Label(), _statusFilters.Contains(chosen), on =>
            {
                if (on) _statusFilters.Add(chosen); else _statusFilters.Remove(chosen);
                Refresh();
            }));
        }

        _buildingFilterPanel = true;
        HiddenCheckBox.IsChecked = _showHidden;
        _buildingFilterPanel = false;

        // sort order (one of four)
        SortList.Children.Clear();
        void AddSort(string text, SortMode mode)
        {
            var radio = new RadioButton
            {
                Content = new TextBlock { Text = text },
                GroupName = "LibrarySort",
                IsChecked = _sort == mode,
                Style = (Style)FindResource("DarkRadioButton"),
                Margin = new Thickness(0, 0, 0, 10)
            };
            radio.Checked += (_, _) =>   // attached after IsChecked was set, so building the panel doesn't trigger it
            {
                _sort = mode;
                SettingsService.SortMode = mode;
                Refresh();
            };
            SortList.Children.Add(radio);
        }
        AddSort("Name (A–Z)", SortMode.Name);
        AddSort("Most played", SortMode.Playtime);
        AddSort("Recently played", SortMode.LastPlayed);
        AddSort("Launcher", SortMode.Source);
    }

    private CheckBox FilterCheckBox(string text, bool isChecked, Action<bool> changed)
    {
        // a TextBlock as content, so a "_" in a name (a category called "Co_op") isn't swallowed as a shortcut marker
        var box = new CheckBox
        {
            Content = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis },
            IsChecked = isChecked,
            Style = (Style)FindResource("DarkCheckBox"),
            Margin = new Thickness(0, 0, 0, 10)
        };
        // Checked / Unchecked (not Click) so keyboard and screen readers work too; attached after IsChecked was set
        box.Checked += (_, _) => changed(true);
        box.Unchecked += (_, _) => changed(false);
        return box;
    }

    private bool _buildingFilterPanel;   // true while the code (not the user) sets the panel's boxes

    private void HiddenCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_buildingFilterPanel) return;

        _showHidden = HiddenCheckBox.IsChecked == true;
        Refresh();
    }

    // ---------- stats ----------

    public record TopGame(string Name, string Time, double Fraction);

    private void ShowStats()
    {
        var played = _library.Games
            .Where(g => g.PlaytimeSeconds > 0)
            .OrderByDescending(g => g.PlaytimeSeconds)
            .ToList();

        var total = played.Sum(g => g.PlaytimeSeconds);
        StatTotal.Text = total <= 0 ? "0 min" : Game.FormatPlaytime(total);
        StatPlayed.Text = played.Count.ToString();
        StatLibrary.Text = _library.Games.Count.ToString();

        var most = played.FirstOrDefault()?.PlaytimeSeconds ?? 1;
        TopGamesList.ItemsSource = played
            .Take(10)
            .Select(g => new TopGame(g.DisplayName, Game.FormatPlaytime(g.PlaytimeSeconds), (double)g.PlaytimeSeconds / most))
            .ToList();

        NoStatsText.Visibility = played.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // over time: needs the dated sessions, so it only covers play since GameDeck started recording them
        StatWeek.Text = FormatHours(_sessions.SecondsThisWeek());

        var (current, longest) = _sessions.Streaks();
        StatStreak.Text = FormatDays(current);
        StatLongestStreak.Text = FormatDays(longest);

        DaysChart.ItemsSource = _sessions.LastDays(7);
        WeeksChart.ItemsSource = _sessions.LastWeeks(8);

        var recent = _sessions.Recent(15);
        RecentSessionsList.ItemsSource = recent;
        NoSessionsText.Visibility = recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // playtime from before sessions were recorded counts in the totals but can't appear in the charts
        UndatedNote.Visibility = total - _sessions.LoggedSeconds > 60 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshStatsButton_Click(object sender, RoutedEventArgs e) => ShowStats();

    private static string FormatHours(long seconds) =>
        seconds <= 0 ? "0 min" : seconds < 3600 ? $"{seconds / 60} min" : $"{seconds / 3600.0:0.#} h";

    private static string FormatDays(int days) => days == 1 ? "1 day" : $"{days} days";

    // ---------- buttons ----------

    private async Task ScanAsync()
    {
        SettingsPanel.SetScanning(true);
        StatusText.Text = "Scanning...";

        CoverService.ClearMisses();              // retry covers that weren't found before
        await Task.Run(_library.Scan);           // background thread keeps the window responsive

        Refresh();
        SettingsPanel.SetScanning(false);
        SettingsPanel.SetGamesStatus($"Scan finished: {_library.Games.Count} games in your library.");
    }

    // The settings actions below run from the desktop settings page and from Big Picture's settings panel. Each takes the
    // window its dialogs belong to and returns a line to show as the result (null = nothing to say).

    private string? FindDuplicates(Window owner)
    {
        var groups = DuplicateFinder.Find(_library.Games);
        if (groups.Count == 0)
            return "No duplicate games found.";

        var dialog = new DuplicatesDialog(groups, (keep, others) =>
        {
            _userData.MergeDuplicates(keep, others);
            _sessions.Reassign(others, keep);   // the session history follows the playtime
        }) { Owner = owner };
        dialog.ShowDialog();

        if (dialog.MergedGroups == 0)
            return null;

        Refresh();   // the merged duplicates are hidden now
        return dialog.MergedGroups == 1 ? "Merged 1 duplicate." : $"Merged {dialog.MergedGroups} duplicates.";
    }

    private void SettingsPanel_DuplicatesRequested(object? sender, EventArgs e)
    {
        if (FindDuplicates(this) is { } status)
            SettingsPanel.SetGamesStatus(status);
    }

    private async void SettingsPanel_ScanRequested(object? sender, EventArgs e) => await ScanAsync();

    private string? AddGames(Window owner)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select the games' .exe files",
            Filter = "Programs (*.exe)|*.exe",
            Multiselect = true
        };

        if (dialog.ShowDialog(owner) != true) return null;

        foreach (var file in dialog.FileNames)
        {
            _library.AddManual(new Game
            {
                Name = Path.GetFileNameWithoutExtension(file),
                Source = "Manual",
                LaunchTarget = file
            });
        }

        Refresh();
        return dialog.FileNames.Length == 1 ? "Added 1 game." : $"Added {dialog.FileNames.Length} games.";
    }

    private void SettingsPanel_AddGamesRequested(object? sender, EventArgs e)
    {
        if (AddGames(this) is { } status)
            SettingsPanel.SetGamesStatus(status);
    }

    private void PickRandomGame()
    {
        var games = RealGames.ToList();
        if (games.Count == 0) return;

        var game = games[Random.Shared.Next(games.Count)];
        GameList.SelectedItem = game;
        GameList.ScrollIntoView(game);
        StatusText.Text = $"How about {game.DisplayName}?";
    }

    /// <summary>The "pick a game for me" tile has no right-click menu.</summary>
    private void Tile_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is Game { IsRandomTile: true })
            e.Handled = true;
    }

    private void SettingsPanel_OnlineCoversChanged(object? sender, EventArgs e)
    {
        if (!SettingsService.OnlineCovers) return;

        CoverService.ClearMisses();
        _ = LoadVisibleCoversAsync();   // fetch covers that were skipped while offline
    }

    // ---------- settings screen ----------

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        NavigateTo(new Page(PageKind.Settings));
    }

    private void CloseSettings() => GoBack();

    private void SettingsPanel_BackRequested(object? sender, EventArgs e) => CloseSettings();

    private void Uninstall(Window owner)
    {
        var dialog = new UninstallDialog { Owner = owner };
        if (dialog.ShowDialog() != true) return;

        try
        {
            UninstallService.Uninstall(dialog.KeepData);
        }
        catch (Exception ex)
        {
            MessageDialog.Show(owner, ex.Message, "Couldn't uninstall GameDeck");
            return;
        }

        ExitApp();   // the cleanup script finishes the job once GameDeck has closed
    }

    private void SettingsPanel_UninstallRequested(object? sender, EventArgs e) => Uninstall(this);

    private void ExportBackup(Window owner)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export GameDeck backup",
            Filter = "GameDeck backup (*.json)|*.json",
            FileName = $"GameDeck-backup-{DateTime.Now:yyyy-MM-dd}.json"
        };

        if (dialog.ShowDialog(owner) != true) return;

        try
        {
            BackupService.Export(dialog.FileName, _userData, _library);
            MessageDialog.Show(owner, "Your backup was saved.", "Backup saved");
        }
        catch (Exception ex)
        {
            MessageDialog.Show(owner, ex.Message, "Couldn't save the backup");
        }
    }

    private void SettingsPanel_ExportRequested(object? sender, EventArgs e) => ExportBackup(this);

    private void ImportBackup(Window owner)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import GameDeck backup",
            Filter = "GameDeck backup (*.json)|*.json"
        };

        if (dialog.ShowDialog(owner) != true) return;

        try
        {
            var count = BackupService.Import(dialog.FileName, _userData, _library);
            Refresh();
            MessageDialog.Show(owner, $"Your backup was restored ({count} games).", "Backup restored");
        }
        catch (Exception ex)
        {
            MessageDialog.Show(owner, ex.Message, "Couldn't restore the backup");
        }
    }

    private void SettingsPanel_ImportRequested(object? sender, EventArgs e) => ImportBackup(this);

    /// <summary>What Big Picture's settings panel can ask of the main window (the same actions as the desktop settings page).</summary>
    private BigPictureSettingsActions BigPictureActions() => new(
        Scan: async () =>
        {
            await ScanAsync();
            return $"Scan finished: {_library.Games.Count} games in your library.";
        },
        AddGames: AddGames,
        FindDuplicates: FindDuplicates,
        ExportBackup: ExportBackup,
        ImportBackup: owner =>
        {
            ImportBackup(owner);
            return null;
        },
        Uninstall: Uninstall,
        GetLibrary: () => (_library.Games.Where(g => !g.IsHidden).ToList(),
                           _library.Games.Where(g => g.IsHidden).Select(g => g.LaunchTarget)
                               .ToHashSet(StringComparer.OrdinalIgnoreCase)));

    // ---------- right-click menu ----------

    private void FavoriteMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: Game game }) return;

        _userData.SetFavorite(game, !game.IsFavorite);

        if (_view == "Favorites")
            Refresh();   // un-favorited games leave this view
    }

    // The game actions below are used by the right-click menu here and by the Menu button's game menu in Big Picture.
    // Each takes the window its dialogs belong to.

    private void EditGame(Game game, Window owner)
    {
        var dialog = new GameSettingsDialog(game) { Owner = owner };
        if (dialog.ShowDialog() != true) return;

        _userData.SaveGameSettings(game, dialog.GameName, dialog.Arguments, dialog.RunAsAdmin, dialog.Tags);
        Refresh();   // the new name can change the order, new categories show up in the Category menu
    }

    private void EditMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: Game game })
            EditGame(game, this);
    }

    private void EditLaunchRoutine(Game game, Window owner)
    {
        var dialog = new LaunchRoutineDialog(game) { Owner = owner };
        if (dialog.ShowDialog() != true) return;

        _userData.SetProfile(game, dialog.Result);
    }

    private void RoutineMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: Game game })
            EditLaunchRoutine(game, this);
    }

    /// <summary>Asked by a launch routine before it closes programs. Yes = close and start, No = start without closing, Cancel = don't start.</summary>
    private MessageBoxResult ConfirmClosing(Game game, IReadOnlyList<string> apps)
    {
        var text = $"Before starting {game.DisplayName}, GameDeck will ask these apps to close:\n\n" +
                   string.Join("\n", apps.Select(a => "•  " + a)) +
                   "\n\nThey can offer to save your work first, and apps that don't close are left running." +
                   "\n\nClose & start closes them, then starts the game. Don't close starts the game and leaves them open.";

        // (the dialog copes with this window being hidden in the tray: it then opens centered on screen)
        return MessageDialog.Choose(this, "Close these apps?", text, "Close & start", "Don't close") switch
        {
            MessageChoice.Yes => MessageBoxResult.Yes,
            MessageChoice.No => MessageBoxResult.No,
            _ => MessageBoxResult.Cancel
        };
    }

    // ---------- categories (right-click > Categories) ----------

    private List<string> AllCategories() => _library.Games
        .SelectMany(g => g.Tags)
        .Distinct(StringComparer.CurrentCultureIgnoreCase)
        .OrderBy(t => t, StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    /// <summary>Fills the Categories submenu: every category (ticked if the game is in it), then New / Manage.</summary>
    private void CategoriesMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, sender) || sender is not MenuItem { DataContext: Game game } parent) return;

        parent.Items.Clear();

        var categories = AllCategories();
        foreach (var category in categories)
        {
            var chosen = category;
            parent.Items.Add(MenuEntry(chosen, HasTag(game, chosen), () => ToggleCategory(game, chosen)));
        }

        if (categories.Count > 0)
            parent.Items.Add(new Separator());

        parent.Items.Add(MenuEntry("New category…", false, () => AddToNewCategory(game, this)));

        // nothing to manage until at least one category exists, so the option isn't shown at all
        if (categories.Count > 0)
            parent.Items.Add(MenuEntry("Manage categories…", false, () => ManageCategories(this)));
    }

    private void ToggleCategory(Game game, string category)
    {
        var tags = game.Tags.ToList();
        var existing = tags.FirstOrDefault(t => t.Equals(category, StringComparison.CurrentCultureIgnoreCase));

        if (existing is not null)
            tags.Remove(existing);
        else
            tags.Add(category);

        _userData.SetTags(game, tags);
        Refresh();
    }

    private void AddToNewCategory(Game game, Window owner)
    {
        var dialog = new TextPromptDialog("New category",
            $"Name the new category. \"{game.DisplayName}\" will be added to it.", "", "Create") { Owner = owner };
        if (dialog.ShowDialog() != true) return;

        // typing the name of an existing category just adds the game to it
        var name = AllCategories().FirstOrDefault(c => c.Equals(dialog.Value, StringComparison.CurrentCultureIgnoreCase)) ?? dialog.Value;

        if (!HasTag(game, name))
        {
            var tags = game.Tags.ToList();
            tags.Add(name);
            _userData.SetTags(game, tags);
        }

        Refresh();
    }

    private void ManageCategories(Window owner)
    {
        var dialog = new ManageCategoriesDialog(
            load: () => AllCategories()
                .Select(c => (c, _library.Games.Count(g => HasTag(g, c))))
                .ToList(),
            rename: (oldName, newName) =>
            {
                _userData.RenameCategory(oldName, newName);
                if (_categoryFilters.Remove(oldName))
                    _categoryFilters.Add(newName);   // keep filtering by it under its new name
                Refresh();
            },
            delete: name =>
            {
                _userData.DeleteCategory(name);
                Refresh();   // a filter on a deleted category resets itself here
            })
        {
            Owner = owner
        };

        dialog.ShowDialog();
    }

    // ---------- status (right-click > Status) ----------

    /// <summary>Fills the Status submenu: None, then each status, ticked for the game's current one.</summary>
    private void StatusMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, sender) || sender is not MenuItem { DataContext: Game game } parent) return;

        parent.Items.Clear();

        foreach (var status in Enum.GetValues<GameStatus>())
        {
            var chosen = status;
            parent.Items.Add(MenuEntry(chosen.Label(), game.Status == chosen, () =>
            {
                _userData.SetStatus(game, chosen);
                Refresh();   // the game may no longer match a status filter
            }));

            if (chosen == GameStatus.None)
                parent.Items.Add(new Separator());
        }
    }

    private void ToggleHidden(Game game)
    {
        _userData.SetHidden(game, !game.IsHidden);
        Refresh();
    }

    private void HideMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: Game game })
            ToggleHidden(game);
    }

    private void AddCover(Game game, Window owner)
    {
        var dialog = new OpenFileDialog
        {
            Title = $"Choose cover art for {game.DisplayName}",
            Filter = "Images (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp"
        };

        if (dialog.ShowDialog() != true) return;

        if (!CoverService.SetCustomCover(game, dialog.FileName))
            MessageDialog.Show(owner, "That file couldn't be read as an image. Try a .jpg, .png or .bmp.", "Couldn't use that image");
    }

    private void AddCoverMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: Game game })
            AddCover(game, this);
    }

    private async void RemoveCoverMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: Game game })
            await CoverService.RemoveCustomCoverAsync(game);
    }

    private void UninstallMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: Game game })
            ProviderActions.Uninstall(game);
    }

    private void RemoveFromLibrary(Game game, Window owner)
    {
        if (game.Source != "Manual")
        {
            MessageDialog.Show(owner, "Only games you added manually can be removed.\n" +
                                      "Scanned games disappear on their own when they're uninstalled.\n" +
                                      "To just get a game out of the way, use \"Hide from library\".", "Can't remove this game");
            return;
        }

        _library.Remove(game);
        Refresh();
    }

    private void RemoveMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: Game game })
            RemoveFromLibrary(game, this);
    }

    /// <summary>What the game menu in Big Picture (the controller's Menu button) can ask of the main window.</summary>
    private BigPictureGameActions BigPictureGameMenu() => new(
        Edit: EditGame,
        EditRoutine: EditLaunchRoutine,
        AllCategories: AllCategories,
        ToggleCategory: ToggleCategory,
        NewCategory: AddToNewCategory,
        ManageCategories: ManageCategories,
        SetStatus: (game, status) =>
        {
            _userData.SetStatus(game, status);
            Refresh();
        },
        ToggleHidden: ToggleHidden,
        AddCover: AddCover,
        RemoveCover: game => CoverService.RemoveCustomCoverAsync(game),
        Uninstall: ProviderActions.Uninstall,
        RemoveFromLibrary: RemoveFromLibrary);

    // ---------- clicking on the grid ----------

    private void RootGrid_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Clicks on tiles and buttons are handled by those controls, so this only
        // fires for empty space: deselect the current game.
        GameList.SelectedItem = null;
        Keyboard.ClearFocus();
    }

    // The tile the left button went down on. Double-clicking the title bar maximizes the window, and the
    // button release that follows lands on whatever tile is now under the mouse: without a matching press
    // on the same tile, that release is not a click.
    private Game? _pressedGame;

    private Game? TileUnder(MouseButtonEventArgs e) =>
        e.OriginalSource is DependencyObject source &&
        ItemsControl.ContainerFromElement(GameList, source) is ListBoxItem { DataContext: Game game }
            ? game
            : null;

    private void GameList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        _pressedGame = TileUnder(e);

    private void GameList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // Only react when the click landed on a tile, not on empty space or the scrollbar
        var game = TileUnder(e);
        var pressed = _pressedGame;
        _pressedGame = null;

        if (game is null || !ReferenceEquals(game, pressed))
            return;

        if (game.IsRandomTile)
        {
            PickRandomGame();
            return;
        }

        OpenDetails(game);
    }

    // ---------- a game's page ----------

    private void OpenDetails(Game game) => NavigateTo(new Page(PageKind.Details, game));

    private void CloseDetails() => GoBack();

    // ---------- going back and forward (like a browser: Back button, Esc, the mouse's side buttons) ----------

    private enum PageKind { Library, Details, Settings }

    private record Page(PageKind Kind, Game? Game = null);

    private Page _page = new(PageKind.Library);
    private readonly Stack<Page> _backPages = new();
    private readonly Stack<Page> _forwardPages = new();

    /// <summary>Opens a page and remembers the current one, so Back returns to it.</summary>
    private void NavigateTo(Page page)
    {
        _backPages.Push(_page);
        _forwardPages.Clear();   // like a browser: a new page ends the "forward" history
        ShowPage(page);
    }

    private void GoBack()
    {
        if (_backPages.Count == 0) return;

        _forwardPages.Push(_page);
        ShowPage(_backPages.Pop());
    }

    private void GoForward()
    {
        if (_forwardPages.Count == 0) return;

        _backPages.Push(_page);
        ShowPage(_forwardPages.Pop());
    }

    private void ShowPage(Page page)
    {
        // a game that was removed from the library in the meantime has no page any more
        if (page.Kind == PageKind.Details && (page.Game is null || !_library.Games.Contains(page.Game)))
            page = new Page(PageKind.Library);

        _page = page;

        LibraryView.Visibility = page.Kind == PageKind.Library ? Visibility.Visible : Visibility.Collapsed;
        DetailsPanel.Visibility = page.Kind == PageKind.Details ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = page.Kind == PageKind.Settings ? Visibility.Visible : Visibility.Collapsed;

        if (page.Kind == PageKind.Details)
        {
            var ranked = _library.Games
                .Where(g => g.PlaytimeSeconds > 0)
                .OrderByDescending(g => g.PlaytimeSeconds)
                .ToList();

            DetailsPanel.Show(page.Game!, ranked.IndexOf(page.Game!) + 1, ranked.Count, _sessions.Recent(8, page.Game));
        }
        else if (page.Kind == PageKind.Settings)
        {
            SettingsPanel.Reload();
        }
    }

    /// <summary>The mouse's side buttons: Back (usually the lower one) and Forward.</summary>
    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.XButton1)
            GoBack();
        else if (e.ChangedButton == MouseButton.XButton2)
            GoForward();
        else
            return;

        e.Handled = true;
    }

    private void DetailsPanel_BackRequested(object? sender, EventArgs e) => CloseDetails();

    private async void DetailsPanel_PlayRequested(object? sender, Game game) => await LaunchGameAsync(game);

    private async Task LaunchGameAsync(Game game, ResumeMode? from = null)
    {
        var launched = await LaunchCoreAsync(game, from);

        // Started from Big Picture, which made the desktop window step aside: decide what shows now
        if (from != ResumeMode.BigPicture || !_mainHiddenForBigPicture) return;

        if (!launched)
        {
            OpenBigPicture(_bigPictureTab, _bigPictureGame);   // it didn't start (cancelled...): back to where we were
            return;
        }

        _mainHiddenForBigPicture = false;

        if (SettingsService.MinimizeOnLaunch)
        {
            StartInTray();   // stays out of the way; the tray icon is how to find it again
        }
        else
        {
            ShowActivated = false;   // visible again behind the game, without taking the keyboard from it
            Show();
            ShowActivated = true;
        }
    }

    /// <returns>True when the game was started.</returns>
    private async Task<bool> LaunchCoreAsync(Game game, ResumeMode? from)
    {
        if (_launching) return false;

        // where to come back to when the game closes: Big Picture if it started there, the window if it was on screen,
        // and nothing if GameDeck was sitting in the tray (quick launch, tray menu)
        var resume = from ?? (IsVisible ? ResumeMode.Desktop : ResumeMode.None);

        // Riot games need Riot Vanguard, which only has to run while playing: start it now, then launch.
        // (false = the user cancelled Windows' admin prompt, so don't launch.)
        if (VanguardService.IsNeededBy(game))
        {
            _launching = true;
            try
            {
                if (!await VanguardService.EnsureRunningAsync()) return false;
            }
            finally
            {
                _launching = false;
            }
        }

        // The game's launch routine (close / open apps, power plan). Runs while a second click on Play is ignored.
        // false = the user chose Cancel in the "close these apps?" question, so the game doesn't start.
        _launching = true;
        LaunchRoutineService.PrepareResult routine;
        try
        {
            routine = await _routines.PrepareAsync(game, ConfirmClosing);
            if (!routine.Proceed) return false;
        }
        finally
        {
            _launching = false;
        }

        var openLauncherWindows = LauncherSilencer.Snapshot(game);   // windows already open stay untouched
        if (!LaunchService.Launch(game))
        {
            await _routines.FinishAsync(game);   // the game never started: undo what the routine did
            return false;
        }

        _resume[game] = resume;
        _userData.RecordLaunch(game);   // "last played"
        _tracker.Track(game);           // playtime: starts counting once the game's process appears
        _ = LauncherSilencer.MinimizeNewWindowsAsync(game, openLauncherWindows);   // hide the launcher that pops up

        if (_view == "Recent" || _sort == SortMode.LastPlayed)
            Refresh();                  // the game moves to the front of the list

        if (SettingsService.ShowLaunchToast)
            GameToastWindow.ShowFor(game.DisplayName, routine.Summary);

        // get out of the way: GameDeck waits in the tray (it keeps timing the game from there)
        if (SettingsService.MinimizeOnLaunch && IsVisible)
            HideToTray();

        return true;
    }
}
