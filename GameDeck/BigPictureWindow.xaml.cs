using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GameDeck.Models;
using GameDeck.Services;

namespace GameDeck.Views;

/// <summary>
/// Big Picture mode: the library full screen, with large tiles, made for a controller on the couch
/// (Xbox, PlayStation and Nintendo controllers; the keyboard and mouse work too).
/// Same tabs as the desktop library, plus search (with an on-screen keyboard), filters and a Power menu.
/// Choosing a game closes this screen and starts the game through the <c>launch</c> callback, so everything the main
/// window does around a launch still happens.
/// </summary>
/// <summary>What Big Picture's settings panel can ask of the main window: the actions of the desktop settings page.</summary>
/// <remarks>Each action takes the window its dialogs belong to and returns a result line to show (null = nothing to say).</remarks>
public record BigPictureSettingsActions(
    Func<Task<string?>> Scan,
    Func<Window, string?> AddGames,
    Func<Window, string?> FindDuplicates,
    Action<Window> ExportBackup,
    Func<Window, string?> ImportBackup,
    Action<Window> Uninstall,
    Func<(IReadOnlyList<Game> Games, IReadOnlySet<string> HiddenKeys)> GetLibrary);

/// <summary>What the game menu (the controller's Menu button, the right-click menu of the desktop library) can ask of the main window.</summary>
public record BigPictureGameActions(
    Action<Game, Window> Edit,
    Action<Game, Window> EditRoutine,
    Func<IReadOnlyList<string>> AllCategories,
    Action<Game, string> ToggleCategory,
    Action<Game, Window> NewCategory,
    Action<Window> ManageCategories,
    Action<Game, GameStatus> SetStatus,
    Action<Game> ToggleHidden,
    Action<Game, Window> AddCover,
    Func<Game, Task> RemoveCover,
    Action<Game> Uninstall,
    Action<Game, Window> RemoveFromLibrary);

public partial class BigPictureWindow : Window
{
    private const double Pitch = 248;   // one tile (220) plus its side margins (2 x 14), see BigItem in the XAML

    // the same tabs as the desktop library
    private static readonly string[] TabNames = ["All games", "Favorites", "Recently played", "Stats"];
    private const int StatsTab = 3;

    private enum Overlay { None, Search, Filters, Power, Details, Settings, GameMenu }

    private record ButtonLabels(string Confirm, string Back, string X, string Y, string Left, string Right,
                                string Select, string Start, string Trigger, string Home);

    /// <summary>One line of the filters panel: a heading, a tick box, a radio button or a plain action.</summary>
    private record FilterRow(string Text, bool IsHeader = false, Func<bool>? IsOn = null, Action? Activate = null, bool IsRadio = false);

    /// <summary>One entry of the Power menu. With a ConfirmTitle it asks "are you sure?" first.</summary>
    private record PowerEntry(string Text, Action Run, string? ConfirmTitle = null, string? ConfirmButton = null);

    private static readonly string[][] KeyLayout =
    [
        ["1", "2", "3", "4", "5", "6", "7", "8", "9", "0"],
        ["Q", "W", "E", "R", "T", "Y", "U", "I", "O", "P"],
        ["A", "S", "D", "F", "G", "H", "J", "K", "L", "'"],
        ["Z", "X", "C", "V", "B", "N", "M", ",", ".", "-"],
        ["Space", "⌫ Delete", "Clear", "Done"]
    ];

    // Frozen brushes are cheaper for WPF (no change tracking) and can be shared by every element that uses them
    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static readonly SolidColorBrush Purple = Frozen(0x80, 0x37, 0xFE);
    private static readonly SolidColorBrush TextGrey = Frozen(0xC5, 0xCB, 0xD6);
    private static readonly SolidColorBrush Dark = Frozen(0x17, 0x1A, 0x21);
    private static readonly SolidColorBrush KeyBackground = Frozen(0x2A, 0x2F, 0x3A);
    private static readonly SolidColorBrush Muted = Frozen(0x8A, 0x91, 0xA0);
    private static readonly SolidColorBrush Divider = Frozen(0x12, 0x14, 0x1A);

    private IReadOnlyList<Game> _library;               // replaced when a scan, an import... changed the library
    private readonly SessionLog _sessions;
    private IReadOnlySet<string> _hiddenKeys;
    private readonly Action<Game> _launch;
    private readonly Action<Game> _toggleFavorite;
    private readonly Action _minimizeApp;
    private readonly Action _exitApp;
    private readonly BigPictureSettingsActions _actions;
    private readonly BigPictureGameActions _gameActions;
    private readonly Action _onlineCoversChanged;
    private readonly Action _quickLaunchChanged;

    /// <summary>True when the screen closed because a game was started (the main window decides what shows meanwhile).</summary>
    public bool LaunchedGame { get; private set; }

    /// <summary>True when the screen closed to minimize or exit GameDeck: the main window must not pop up in between.</summary>
    public bool KeepMainHidden { get; private set; }

    private readonly ObservableCollection<Game> _items = [];
    private readonly GamepadService _pad = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };

    // covers this screen asked for -> whether the main window's tile already wanted it (so closing doesn't take it away)
    private readonly Dictionary<Game, bool> _touchedCovers = [];

    // what the list is narrowed down by
    private readonly HashSet<string> _launcherFilters = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _categoryFilters = new(StringComparer.CurrentCultureIgnoreCase);
    private readonly HashSet<GameStatus> _statusFilters = [];
    private SortMode _sort = SettingsService.SortMode;
    private string _query = "";

    private Overlay _overlay = Overlay.None;

    // search keyboard
    private Border[][] _keyCells = [];
    private int _keyRow;
    private int _keyCol;

    // filters panel
    private List<FilterRow> _filterRows = [];
    private List<Border> _filterCells = [];
    private int _filterIndex;

    // power menu
    private PowerEntry? _powerConfirming;
    private List<List<(string Text, Action Run)>> _powerGroups = [];
    private int _powerIndex;

    private ScrollViewer? _scroll;
    private double _targetOffset;
    private int _tab;
    private int _tick;
    private bool _closing;

    /// <param name="games">The games to show. Hidden games are not passed in, so they never appear here.</param>
    /// <param name="hiddenKeys">Launch targets of the hidden games, so their play sessions stay out of the Stats tab too.</param>
    /// <param name="minimizeApp">"Minimize GameDeck" in the Power menu (called after this screen closed).</param>
    /// <param name="exitApp">"Exit GameDeck" in the Power menu (called after this screen closed).</param>
    /// <param name="actions">What the settings panel asks of the main window (scan, add games, backup...).</param>
    public BigPictureWindow(IReadOnlyList<Game> games, IReadOnlySet<string> hiddenKeys, SessionLog sessions,
                            Action<Game> launch, Action<Game> toggleFavorite, Action minimizeApp, Action exitApp,
                            BigPictureSettingsActions actions, BigPictureGameActions gameMenu,
                            Action onlineCoversChanged, Action quickLaunchChanged)
    {
        InitializeComponent();

        _actions = actions;
        _gameActions = gameMenu;
        _onlineCoversChanged = onlineCoversChanged;
        _quickLaunchChanged = quickLaunchChanged;

        _library = games;
        _hiddenKeys = hiddenKeys;
        _sessions = sessions;
        _launch = launch;
        _toggleFavorite = toggleFavorite;
        _minimizeApp = minimizeApp;
        _exitApp = exitApp;

        Carousel.ItemsSource = _items;

        _pad.ControllerChanged += UpdateControllerUi;
        _timer.Tick += Timer_Tick;
        Loaded += (_, _) =>
        {
            _scroll = FindChild<ScrollViewer>(Carousel);
            UpdateTarget();
            _scroll?.ScrollToHorizontalOffset(_targetOffset);
        };
        Closed += (_, _) => Cleanup();

        BuildKeyboard();
        UpdateClock();
        ApplyTab(preserveSelection: false);
        UpdateControllerUi();
    }

    // ---------- picking up where it was left ----------

    /// <summary>The tab showing now (stays readable after the window closed).</summary>
    public int Tab => _tab;

    /// <summary>The game selected now (stays readable after the window closed).</summary>
    public Game? SelectedGame => Carousel.SelectedItem as Game;

    /// <summary>Goes back to a tab and a game, before the window is shown (used when a game was closed).</summary>
    public void Restore(int tab, Game? game)
    {
        if (tab < 0 || tab >= TabNames.Length) return;

        _tab = tab;
        ApplyTab(preserveSelection: false);

        if (game is not null && _items.Contains(game))
            Carousel.SelectedItem = game;

        UpdateTarget();
    }

    // ---------- window / controller life cycle ----------

    // Controllers are only read while this screen is in front, so they aren't touched while a game is running.
    private void Window_Activated(object? sender, EventArgs e)
    {
        if (_closing) return;

        _pad.Start();
        _timer.Start();
        UpdateControllerUi();
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        _timer.Stop();
        _pad.Stop();
    }

    private void Cleanup()
    {
        _timer.Stop();
        _pad.ControllerChanged -= UpdateControllerUi;   // the screen is gone: nothing left to redraw
        _pad.Dispose();

        // hand covers back the way the main window expects them
        foreach (var (game, mainWindowWantedIt) in _touchedCovers)
        {
            if (mainWindowWantedIt) continue;

            game.CoverWanted = false;
            game.Cover = null;
        }

        _touchedCovers.Clear();
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        foreach (var button in _pad.Poll())
            Handle(button);

        // glide towards the selected tile instead of jumping
        if (_scroll is not null)
        {
            var current = _scroll.HorizontalOffset;
            var distance = _targetOffset - current;

            if (Math.Abs(distance) > 0.5)
                _scroll.ScrollToHorizontalOffset(current + distance * 0.25);
            else if (distance != 0)
                _scroll.ScrollToHorizontalOffset(_targetOffset);
        }

        if (++_tick % 60 == 0)
            UpdateClock();
    }

    // ---------- what the buttons do ----------

    private void Handle(PadButton button)
    {
        // an open menu / keyboard / panel takes all the input
        if (_overlay != Overlay.None)
        {
            HandleOverlay(button);
            return;
        }

        // the icons at the top (search, filters, settings, power) are selected with the stick: they take the directions
        if (_topFocus && HandleTopBar(button))
            return;

        switch (button)
        {
            case PadButton.Left: Move(-1); break;
            case PadButton.Right: Move(1); break;
            case PadButton.Up: UpOrDown(-1); break;      // up to the icons at the top; scrolls the Stats tab
            case PadButton.Down: UpOrDown(1); break;
            case PadButton.Confirm: OpenDetails(); break;   // like desktop mode: the game's page first, Play from there
            case PadButton.Back: AskToExit(); break;   // not instantly: it could be a stray press
            case PadButton.X: ToggleFavorite(); break;
            case PadButton.Y: OpenSearch(); break;
            case PadButton.Select: OpenFilters(); break;
            case PadButton.Start: OpenGameMenu(); break;   // the right-click menu of the desktop library
            case PadButton.Home: OpenPower(); break;
            case PadButton.RightTrigger: PickRandom(); break;
            case PadButton.LeftShoulder: SwitchTab(-1); break;
            case PadButton.RightShoulder: SwitchTab(1); break;
        }
    }

    private void HandleOverlay(PadButton button)
    {
        switch (_overlay)
        {
            case Overlay.Search: HandleSearch(button); break;
            case Overlay.Filters: HandleFilters(button); break;
            case Overlay.Power: HandlePower(button); break;
            case Overlay.Details: HandleDetails(button); break;
            case Overlay.Settings: HandleSettings(button); break;
            case Overlay.GameMenu: HandleGameMenu(button); break;
        }
    }

    private void HandleDetails(PadButton button)
    {
        switch (button)
        {
            case PadButton.Confirm: _ = PressPlayThenLaunch(); break;
            case PadButton.Back: CloseOverlay(); break;
        }
    }

    private bool _pressingPlay;

    /// <summary>Shows the Play button being pressed for a moment, then starts the game.</summary>
    private async Task PressPlayThenLaunch()
    {
        if (_pressingPlay || _closing) return;

        _pressingPlay = true;
        DetailsView.PressPlay();
        await Task.Delay(180);
        _pressingPlay = false;

        if (_overlay == Overlay.Details)
            LaunchSelected();
    }

    private void OpenDetails()
    {
        if (Carousel.SelectedItem is not Game game) return;

        var ranked = _library.Where(g => g.PlaytimeSeconds > 0).OrderByDescending(g => g.PlaytimeSeconds).ToList();
        DetailsView.Show(game, ranked.IndexOf(game) + 1, ranked.Count, _sessions.Recent(8, game));
        DetailsView.SelectPlay();   // the controller has no pointer: show Play as the selected button

        var l = Labels();
        FillHints(DetailsHints, (l.Confirm, "Play"), (l.Back, "Back"));
        ShowOverlay(Overlay.Details);
    }

    private void DetailsView_BackRequested(object? sender, EventArgs e) => CloseOverlay();

    private void DetailsView_PlayRequested(object? sender, Game game) => _ = PressPlayThenLaunch();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // choosing a new quick launch shortcut: every key press is part of it
        if (_capturingShortcut && _overlay == Overlay.Settings)
        {
            CaptureShortcut(e);
            e.Handled = true;
            return;
        }

        // typing in the search box: letters arrive as text (Window_PreviewTextInput); only the keys below are handled here
        PadButton? pad = e.Key switch
        {
            Key.Left => PadButton.Left,
            Key.Right => PadButton.Right,
            Key.Up => PadButton.Up,
            Key.Down => PadButton.Down,
            Key.Enter => PadButton.Confirm,
            Key.Escape => PadButton.Back,
            Key.Space when _overlay != Overlay.Search => PadButton.Confirm,
            Key.Back when _overlay == Overlay.Search => PadButton.X,    // backspace
            Key.Tab => PadButton.Select,
            _ => null
        };

        // the letter shortcuts only exist while no menu is open (they would clash with typing)
        if (pad is null && _overlay == Overlay.None)
        {
            pad = e.Key switch
            {
                Key.F => PadButton.X,
                Key.S or Key.OemQuestion => PadButton.Y,
                Key.P => PadButton.Home,
                Key.M or Key.Apps => PadButton.Start,
                Key.R => PadButton.RightTrigger,
                Key.PageUp => PadButton.LeftShoulder,
                Key.PageDown => PadButton.RightShoulder,
                _ => null
            };
        }

        if (pad is null) return;

        Handle(pad.Value);
        e.Handled = true;
    }

    /// <summary>A real keyboard can type into the search box too.</summary>
    private void Window_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (_overlay != Overlay.Search) return;

        foreach (var c in e.Text)
        {
            if (char.IsLetterOrDigit(c) || " '-.,:".Contains(c))
                TypeText(char.ToLowerInvariant(c).ToString());
        }

        e.Handled = true;
    }

    private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_overlay == Overlay.None)
        {
            if (_tab == StatsTab)
                ScrollStats(e.Delta > 0 ? -1 : 1);
            else
                Move(e.Delta > 0 ? -1 : 1);
        }
        else if (_overlay == Overlay.Filters)
        {
            MoveFilter(e.Delta > 0 ? -1 : 1);
        }

        e.Handled = true;
    }

    private void SearchButton_Click(object sender, MouseButtonEventArgs e) => OpenSearch();

    private void FilterButton_Click(object sender, MouseButtonEventArgs e) => OpenFilters();

    private void PowerButton_Click(object sender, MouseButtonEventArgs e) => OpenPower();

    private void SettingsButton_Click(object sender, MouseButtonEventArgs e) => OpenSettings();

    // ---------- the mouse on the row of games ----------

    private Point _lastMouse;
    private Game? _pressedTile;
    private bool _hoverSelecting;   // the mouse chose the tile: the row must not glide to center it under the pointer

    private Game? TileUnder(object source) =>
        source is DependencyObject element &&
        ItemsControl.ContainerFromElement(Carousel, element) is ListBoxItem { DataContext: Game game }
            ? game
            : null;

    /// <summary>The tile under the mouse becomes the selected one, as with the stick (the row only follows when you move the mouse).</summary>
    private void Carousel_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        var position = e.GetPosition(this);
        if (position == _lastMouse) return;   // tiles gliding past a mouse that isn't moving are not a choice
        _lastMouse = position;

        if (_overlay != Overlay.None || TileUnder(e.OriginalSource) is not { } game ||
            ReferenceEquals(game, Carousel.SelectedItem))
            return;

        if (_topFocus)
            FocusTopBar(false);

        _hoverSelecting = true;
        try { Carousel.SelectedItem = game; }
        finally { _hoverSelecting = false; }
    }

    // A tile can't take the keyboard focus, and a list only selects an item it can focus: so a click is handled here.
    // Like in the desktop library, a click on a tile opens the game's page.
    private void Carousel_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        _pressedTile = TileUnder(e.OriginalSource);

    private void Carousel_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var tile = TileUnder(e.OriginalSource);
        var pressed = _pressedTile;
        _pressedTile = null;

        // (pressed and released on the same tile; not on empty space, not behind an open menu)
        if (_overlay != Overlay.None || tile is null || !ReferenceEquals(tile, pressed))
            return;

        Carousel.SelectedItem = tile;
        OpenDetails();
    }

    private void Move(int step)
    {
        if (_items.Count == 0) return;

        Carousel.SelectedIndex = Math.Clamp(Math.Max(Carousel.SelectedIndex, 0) + step, 0, _items.Count - 1);
    }

    private void UpOrDown(int direction)
    {
        if (_tab == StatsTab)
        {
            // scroll the stats; at the very top, up goes on to the icons
            if (direction < 0 && StatsScroll.VerticalOffset <= 0)
                FocusTopBar(true);
            else
                ScrollStats(direction);
        }
        else if (direction < 0)
        {
            FocusTopBar(true);
        }
    }

    // ---------- the icons at the top, reachable with the stick ----------

    private bool _topFocus;
    private int _topIndex;

    private Border[]? _topIcons;

    private Border[] TopIcons => _topIcons ??= [SearchButton, FilterButton, SettingsButton, PowerButton];

    private void FocusTopBar(bool on)
    {
        _topFocus = on;
        if (on) _topIndex = Math.Clamp(_topIndex, 0, TopIcons.Length - 1);

        UpdateTopBar();
        BuildMainHints();
    }

    /// <summary>Selected icon: white with a dark symbol, like the selected key or row elsewhere in this screen.</summary>
    private void UpdateTopBar()
    {
        var icons = TopIcons;
        for (var i = 0; i < icons.Length; i++)
        {
            var selected = _topFocus && i == _topIndex;
            var glyph = (TextBlock)icons[i].Child;

            if (selected)
            {
                icons[i].Background = Brushes.White;
                glyph.Foreground = Dark;
            }
            else
            {
                icons[i].ClearValue(BackgroundProperty);   // back to the style's colors (including the mouse hover)
                glyph.Foreground = Brushes.White;
            }
        }
    }

    /// <summary>Returns true when the button was used up here.</summary>
    private bool HandleTopBar(PadButton button)
    {
        switch (button)
        {
            case PadButton.Left: _topIndex = Math.Max(0, _topIndex - 1); UpdateTopBar(); return true;
            case PadButton.Right: _topIndex = Math.Min(TopIcons.Length - 1, _topIndex + 1); UpdateTopBar(); return true;
            case PadButton.Up: return true;
            case PadButton.Down or PadButton.Back: FocusTopBar(false); return true;   // back to the games

            case PadButton.Confirm:
                switch (_topIndex)
                {
                    case 0: OpenSearch(); break;
                    case 1: OpenFilters(); break;
                    case 2: OpenSettings(); break;
                    default: OpenPower(); break;
                }
                return true;

            default: return false;   // tabs, Home, favorite... keep working from up here
        }
    }

    private void ScrollStats(int direction) =>
        StatsScroll.ScrollToVerticalOffset(Math.Max(0, StatsScroll.VerticalOffset + direction * 240));

    private void LaunchSelected()
    {
        if (_closing || Carousel.SelectedItem is not Game game) return;

        LaunchedGame = true;
        CloseScreen();
        _launch(game);   // after closing, so the game isn't started behind a full-screen window
    }

    private void CloseScreen()
    {
        if (_closing) return;

        _closing = true;
        Close();
    }

    /// <summary>Closes this screen, then does something in the main window (minimize, exit...).</summary>
    private void CloseThen(Action after, bool keepMainHidden = true)
    {
        KeepMainHidden = keepMainHidden;
        CloseScreen();
        after();
    }

    private void ToggleFavorite()
    {
        if (Carousel.SelectedItem is not Game game) return;

        var index = Carousel.SelectedIndex;
        _toggleFavorite(game);

        // in the Favorites tab an un-favorited game leaves the list
        if (_tab == 1 && !game.IsFavorite)
        {
            _items.Remove(game);
            EmptyText.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            Carousel.SelectedIndex = _items.Count == 0 ? -1 : Math.Min(index, _items.Count - 1);
        }
    }

    private void PickRandom()
    {
        if (_items.Count == 0) return;

        var index = Random.Shared.Next(_items.Count);
        if (_items.Count > 1 && index == Carousel.SelectedIndex)
            index = (index + 1) % _items.Count;

        Carousel.SelectedIndex = index;
    }

    // ---------- tabs and the list ----------

    private void SwitchTab(int step)
    {
        _tab = (_tab + step + TabNames.Length) % TabNames.Length;
        ApplyTab(preserveSelection: false);
    }

    private bool HasActiveFilters => _launcherFilters.Count > 0 || _categoryFilters.Count > 0 || _statusFilters.Count > 0;

    private void ApplyTab(bool preserveSelection)
    {
        // the Stats tab shows numbers and charts instead of the row of games
        var stats = _tab == StatsTab;
        Carousel.Visibility = stats ? Visibility.Collapsed : Visibility.Visible;
        StatsScroll.Visibility = stats ? Visibility.Visible : Visibility.Collapsed;

        if (stats)
        {
            _items.Clear();   // frees the covers too (the tiles unload)
            EmptyText.Visibility = Visibility.Collapsed;
            DetailsPanel.Visibility = Visibility.Collapsed;
            FilterSummary.Visibility = Visibility.Collapsed;

            BuildStats();
            StatsScroll.ScrollToTop();
            BuildTabs();
            BuildMainHints();
            return;
        }

        BuildMainHints();
        var keep = preserveSelection ? Carousel.SelectedItem as Game : null;

        // narrow the library down by the filters and the search, like the desktop library does
        IEnumerable<Game> source = _library;
        if (_launcherFilters.Count > 0)
            source = source.Where(g => _launcherFilters.Contains(g.Source));
        if (_categoryFilters.Count > 0)
            source = source.Where(g => g.Tags.Any(_categoryFilters.Contains));
        if (_statusFilters.Count > 0)
            source = source.Where(g => _statusFilters.Contains(g.Status));
        if (_query.Length > 0)
            source = source.Where(g => g.DisplayName.Contains(_query, StringComparison.CurrentCultureIgnoreCase) ||
                                       g.Name.Contains(_query, StringComparison.CurrentCultureIgnoreCase));

        IEnumerable<Game> list = _tab switch
        {
            1 => Sort(source.Where(g => g.IsFavorite)),
            2 => source.Where(g => g.LastPlayed is not null).OrderByDescending(g => g.LastPlayed).Take(30),
            _ => Sort(source)
        };

        _items.Clear();
        foreach (var game in list)
            _items.Add(game);

        var narrowed = HasActiveFilters || _query.Length > 0;
        EmptyText.Text = narrowed
            ? "No games match your search or filters.\nPress the filters button to change them."
            : _tab switch
            {
                1 => "No favorites yet.\nPress the favorite button on a game to add it here.",
                2 => "Nothing played yet.\nGames you start from GameDeck show up here.",
                _ => "No games found.\nOpen GameDeck's settings and scan for games."
            };
        EmptyText.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var index = keep is null ? 0 : Math.Max(0, _items.IndexOf(keep));
        Carousel.SelectedIndex = _items.Count == 0 ? -1 : index;

        UpdateFilterSummary();
        BuildTabs();
        UpdateDetails();

        // a new list starts at its first tile straight away
        UpdateTarget();
        _scroll?.ScrollToHorizontalOffset(_targetOffset);
    }

    private IOrderedEnumerable<Game> Sort(IEnumerable<Game> games) => _sort switch
    {
        SortMode.Playtime => games.OrderByDescending(g => g.PlaytimeSeconds).ThenBy(ByName, StringComparer.CurrentCultureIgnoreCase),
        SortMode.LastPlayed => games.OrderByDescending(g => g.LastPlayed ?? DateTime.MinValue).ThenBy(ByName, StringComparer.CurrentCultureIgnoreCase),
        SortMode.Source => games.OrderBy(g => g.Source, StringComparer.CurrentCultureIgnoreCase).ThenBy(ByName, StringComparer.CurrentCultureIgnoreCase),
        _ => games.OrderBy(ByName, StringComparer.CurrentCultureIgnoreCase)
    };

    private static string ByName(Game game) => game.DisplayName;

    private static string LauncherName(string source) => source == "Manual" ? "Other" : source;

    /// <summary>A line under the tabs saying what the list is narrowed down by.</summary>
    private void UpdateFilterSummary()
    {
        var parts = new List<string>();
        if (_query.Length > 0)
            parts.Add($"Search \"{_query}\"");
        parts.AddRange(_launcherFilters.Select(LauncherName).Order());
        parts.AddRange(_categoryFilters.Order());
        parts.AddRange(_statusFilters.OrderBy(s => s).Select(s => s.Label()));

        FilterSummary.Text = "Showing: " + string.Join("  ·  ", parts);
        FilterSummary.Visibility = parts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Carousel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateDetails();

        if (!_hoverSelecting)
            UpdateTarget();
    }

    /// <summary>Where to scroll so the selected tile sits in the middle of the screen.</summary>
    private void UpdateTarget()
    {
        if (Carousel.SelectedIndex < 0) return;

        var viewport = _scroll?.ViewportWidth is > 0 ? _scroll.ViewportWidth : Carousel.ActualWidth;
        var max = Math.Max(0, _items.Count * Pitch - viewport);

        _targetOffset = Math.Clamp(Carousel.SelectedIndex * Pitch + Pitch / 2 - viewport / 2, 0, max);
    }

    private void UpdateDetails()
    {
        if (Carousel.SelectedItem is not Game game)
        {
            DetailsPanel.Visibility = Visibility.Collapsed;
            return;
        }

        DetailsPanel.Visibility = Visibility.Visible;
        NameText.Text = game.DisplayName;

        var parts = new List<string>
        {
            game.Source == "Manual" ? "Added by hand" : game.Source,
            game.PlaytimeSeconds > 0 ? $"{game.PlaytimeText} played" : "Not played yet"
        };

        if (game.LastPlayed is not null)
            parts.Add($"Last played {game.LastPlayedText}");
        if (game.HasStatus)
            parts.Add(game.StatusText);

        SubText.Text = string.Join("  ·  ", parts);
    }

    // ---------- search (on-screen keyboard) ----------

    private void OpenSearch()
    {
        if (_tab == StatsTab) return;   // nothing to search there

        ShowOverlay(Overlay.Search);
        UpdateQueryUi();
        UpdateKeyboardSelection();
        BuildOverlayHints();
    }

    private void BuildKeyboard()
    {
        KeyRows.Children.Clear();
        _keyCells = new Border[Rows.Length][];

        for (var r = 0; r < Rows.Length; r++)
        {
            var row = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1 };
            _keyCells[r] = new Border[Rows[r].Length];

            for (var c = 0; c < Rows[r].Length; c++)
            {
                var cell = new Border
                {
                    Margin = new Thickness(4),
                    Padding = new Thickness(0, 14, 0, 14),
                    CornerRadius = new CornerRadius(8),
                    Background = KeyBackground,
                    Child = new TextBlock
                    {
                        Text = Rows[r][c], Foreground = Brushes.White, FontSize = 22,
                        HorizontalAlignment = HorizontalAlignment.Center
                    }
                };

                var (row0, col0) = (r, c);
                cell.MouseEnter += (_, _) =>
                {
                    if (_keyRow == row0 && _keyCol == col0) return;

                    _keyRow = row0;   // the mouse selects the key it points at
                    _keyCol = col0;
                    UpdateKeyboardSelection();
                };

                cell.MouseLeftButtonUp += (_, _) =>
                {
                    _keyRow = row0;
                    _keyCol = col0;
                    UpdateKeyboardSelection();
                    PressKey();
                };

                _keyCells[r][c] = cell;
                row.Children.Add(cell);
            }

            KeyRows.Children.Add(row);
        }
    }

    // (named so the code above reads naturally)
    private static string[][] Rows => KeyLayout;

    private void UpdateKeyboardSelection()
    {
        for (var r = 0; r < _keyCells.Length; r++)
        {
            for (var c = 0; c < _keyCells[r].Length; c++)
            {
                var selected = r == _keyRow && c == _keyCol;
                var cell = _keyCells[r][c];

                cell.Background = selected ? Brushes.White : KeyBackground;
                ((TextBlock)cell.Child).Foreground = selected ? Dark : Brushes.White;
            }
        }
    }

    private void HandleSearch(PadButton button)
    {
        switch (button)
        {
            case PadButton.Left: MoveKey(0, -1); break;
            case PadButton.Right: MoveKey(0, 1); break;
            case PadButton.Up: MoveKey(-1, 0); break;
            case PadButton.Down: MoveKey(1, 0); break;
            case PadButton.Confirm: PressKey(); break;
            case PadButton.X: Backspace(); break;
            case PadButton.Y: TypeText(" "); break;
            case PadButton.Back or PadButton.Start: CloseOverlay(); break;
        }
    }

    private void MoveKey(int rowStep, int colStep)
    {
        if (rowStep != 0)
        {
            var newRow = Math.Clamp(_keyRow + rowStep, 0, Rows.Length - 1);

            // keep roughly the same horizontal position when the next row has a different number of keys
            var oldLast = Math.Max(1, Rows[_keyRow].Length - 1);
            var newLast = Rows[newRow].Length - 1;
            _keyCol = (int)Math.Round((double)_keyCol / oldLast * newLast);
            _keyRow = newRow;
        }

        if (colStep != 0)
        {
            var length = Rows[_keyRow].Length;
            _keyCol = (_keyCol + colStep + length) % length;
        }

        UpdateKeyboardSelection();
    }

    private void PressKey()
    {
        var key = Rows[_keyRow][_keyCol];

        switch (key)
        {
            case "Space": TypeText(" "); break;
            case "⌫ Delete": Backspace(); break;
            case "Clear": SetQuery(""); break;
            case "Done": CloseOverlay(); break;
            default: TypeText(key.ToLowerInvariant()); break;
        }
    }

    private void TypeText(string text)
    {
        if (_query.Length + text.Length > 40) return;
        if (text == " " && (_query.Length == 0 || _query.EndsWith(' '))) return;   // no leading or double spaces

        SetQuery(_query + text);
    }

    private void Backspace()
    {
        if (_query.Length > 0)
            SetQuery(_query[..^1]);
    }

    /// <summary>The list follows the search as it is typed.</summary>
    private void SetQuery(string query)
    {
        _query = query;
        ApplyTab(preserveSelection: false);
        UpdateQueryUi();
    }

    private void UpdateQueryUi()
    {
        QueryText.Text = _query;
        QueryHint.Visibility = _query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ResultCountText.Text = _items.Count == 1 ? "1 game" : $"{_items.Count} games";
    }

    // ---------- filters ----------

    private void OpenFilters()
    {
        if (_tab == StatsTab) return;

        _filterIndex = 0;
        ShowOverlay(Overlay.Filters);
        RebuildFilters();
        BuildOverlayHints();
    }

    /// <summary>Lists the launchers, categories and statuses that exist in the library, then the sort orders.</summary>
    private void RebuildFilters()
    {
        var rows = new List<FilterRow> { new("Clear all filters", Activate: ClearFilters) };

        var launchers = _library.Select(g => g.Source).Distinct()
            .OrderBy(s => s == "Manual").ThenBy(s => s, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (launchers.Count > 0)
        {
            rows.Add(new FilterRow("LAUNCHER", IsHeader: true));
            foreach (var launcher in launchers)
            {
                var chosen = launcher;
                rows.Add(new FilterRow(LauncherName(chosen), IsOn: () => _launcherFilters.Contains(chosen),
                                       Activate: () => Toggle(_launcherFilters, chosen)));
            }
        }

        var categories = _library.SelectMany(g => g.Tags).Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(t => t, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (categories.Count > 0)
        {
            rows.Add(new FilterRow("CATEGORY", IsHeader: true));
            foreach (var category in categories)
            {
                var chosen = category;
                rows.Add(new FilterRow(chosen, IsOn: () => _categoryFilters.Contains(chosen),
                                       Activate: () => Toggle(_categoryFilters, chosen)));
            }
        }

        rows.Add(new FilterRow("STATUS", IsHeader: true));
        foreach (var status in Enum.GetValues<GameStatus>().Where(s => s != GameStatus.None))
        {
            var chosen = status;
            rows.Add(new FilterRow(chosen.Label(), IsOn: () => _statusFilters.Contains(chosen),
                                   Activate: () => Toggle(_statusFilters, chosen)));
        }

        rows.Add(new FilterRow("SORT BY", IsHeader: true));
        foreach (var (text, mode) in new[]
                 {
                     ("Name (A–Z)", SortMode.Name), ("Most played", SortMode.Playtime),
                     ("Last played", SortMode.LastPlayed), ("Launcher", SortMode.Source)
                 })
        {
            var chosen = mode;
            rows.Add(new FilterRow(text, IsOn: () => _sort == chosen, Activate: () => _sort = chosen, IsRadio: true));
        }

        _filterRows = rows;
        if (_filterRows[_filterIndex].IsHeader)
            _filterIndex = 0;

        RenderFilters();
    }

    private static void Toggle<T>(HashSet<T> set, T value)
    {
        if (!set.Remove(value))
            set.Add(value);
    }

    private void ClearFilters()
    {
        _launcherFilters.Clear();
        _categoryFilters.Clear();
        _statusFilters.Clear();
    }

    private void RenderFilters() =>
        _filterCells = RenderRows(FilterList, _filterRows, _filterIndex, index =>
        {
            _filterIndex = index;
            ActivateFilterRow();
        }, index =>
        {
            if (_filterIndex == index) return;

            _filterIndex = index;
            RenderOnHover(RenderFilters);
        });

    private bool _renderingOnHover;

    /// <summary>Redraws a list because the mouse moved onto another row: the list must not scroll while that happens.</summary>
    private void RenderOnHover(Action render)
    {
        _renderingOnHover = true;
        try { render(); }
        finally { _renderingOnHover = false; }
    }

    /// <summary>Draws a list of headings, tick boxes, radio buttons and actions (the filters and the settings share it).</summary>
    /// <returns>One cell per row (an empty one for a heading), so the indexes line up with <paramref name="rows"/>.</returns>
    private List<Border> RenderRows(StackPanel panel, List<FilterRow> rows, int selectedIndex, Action<int> activate,
                                    Action<int> hover)
    {
        panel.Children.Clear();
        var cells = new List<Border>();

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];

            if (row.IsHeader)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = row.Text, Foreground = Muted, FontSize = 14,
                    FontWeight = FontWeights.SemiBold, Margin = new Thickness(14, 22, 0, 8)
                });
                cells.Add(new Border());   // keeps the indexes lined up with the rows
                continue;
            }

            var selected = i == selectedIndex;
            var on = row.IsOn?.Invoke() == true;

            var line = new DockPanel();
            if (row.IsOn is not null)
            {
                // tick box / radio button, drawn with Windows' own icon font
                var glyph = row.IsRadio ? (on ? "" : "") : (on ? "" : "");
                line.Children.Add(new TextBlock
                {
                    Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 20,
                    Foreground = selected ? Dark : on ? Purple : Muted,
                    Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center
                });
            }

            line.Children.Add(new TextBlock
            {
                Text = row.Text, FontSize = 19, VerticalAlignment = VerticalAlignment.Center,
                Foreground = selected ? Dark : TextGrey, TextTrimming = TextTrimming.CharacterEllipsis
            });

            var cell = new Border
            {
                Background = selected ? Brushes.White : Brushes.Transparent,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16, 12, 16, 12),
                Margin = new Thickness(0, 1, 0, 1),
                Child = line,
                Cursor = Cursors.Hand
            };

            var index = i;
            cell.MouseEnter += (_, _) => hover(index);
            cell.MouseLeftButtonUp += (_, _) => activate(index);

            cells.Add(cell);
            panel.Children.Add(cell);
        }

        // keep the selected row on screen when the panel is long
        // (not when the mouse chose the row: scrolling under the pointer would make the next row the one it points at)
        if (selectedIndex < cells.Count && !_renderingOnHover)
            cells[selectedIndex].BringIntoView();

        return cells;
    }

    private void HandleFilters(PadButton button)
    {
        switch (button)
        {
            case PadButton.Up: MoveFilter(-1); break;
            case PadButton.Down: MoveFilter(1); break;
            case PadButton.Confirm: ActivateFilterRow(); break;
            case PadButton.Back or PadButton.Select: CloseOverlay(); break;
        }
    }

    private void MoveFilter(int step)
    {
        var next = _filterIndex;

        do
        {
            next += step;
            if (next < 0 || next >= _filterRows.Count) return;   // at the end: stay where we are
        }
        while (_filterRows[next].IsHeader);

        _filterIndex = next;
        RenderFilters();
    }

    private void ActivateFilterRow()
    {
        _filterRows[_filterIndex].Activate?.Invoke();

        RebuildFilters();                       // ticks change
        ApplyTab(preserveSelection: false);     // the list behind the panel follows
    }

    // ---------- settings ----------

    private List<FilterRow> _settingsRows = [];
    private int _settingsIndex;

    private void OpenSettings()
    {
        _settingsIndex = 1;   // row 0 is a heading
        _capturingShortcut = false;
        ShowSettingsStatus(null);
        ShowOverlay(Overlay.Settings);
        RenderSettings();
        FillHints(SettingsHints, (Labels().Confirm, "Change"), (Labels().Back, "Close"));
    }

    /// <summary>The settings that make sense from the couch. Everything else is on the desktop settings page.</summary>
    private void RenderSettings()
    {
        // the same settings as the desktop page, in the same order
        var rows = new List<FilterRow>
        {
            new("GAMES", IsHeader: true),
            new("Scan for games", Activate: () => RunSettingsAction(_actions.Scan)),
            new("Add games…  (pick .exe files with the keyboard and mouse)",
                Activate: () => RunSettingsAction(() => Task.FromResult(_actions.AddGames(this)))),
            new("Find duplicates…", Activate: () => RunSettingsAction(() => Task.FromResult(_actions.FindDuplicates(this)))),

            new("GENERAL", IsHeader: true),
            new("Launch GameDeck when Windows starts", IsOn: () => StartupService.IsEnabled,
                Activate: () => StartupService.SetEnabled(!StartupService.IsEnabled))
        };

        if (StartupService.IsEnabled)   // only makes sense while launching with Windows is on
            rows.Add(new("Launch GameDeck minimized to the system tray", IsOn: () => SettingsService.StartMinimized,
                Activate: () => SettingsService.StartMinimized = !SettingsService.StartMinimized));

        rows.AddRange(
        [
            new("Minimize to the system tray when closing GameDeck", IsOn: () => SettingsService.CloseAction == CloseAction.MinimizeToTray,
                Activate: () => SettingsService.CloseAction = SettingsService.CloseAction == CloseAction.MinimizeToTray
                    ? CloseAction.Exit
                    : CloseAction.MinimizeToTray),
            new("Minimize GameDeck to the system tray when a game starts", IsOn: () => SettingsService.MinimizeOnLaunch,
                Activate: () => SettingsService.MinimizeOnLaunch = !SettingsService.MinimizeOnLaunch),
            new("Show a notice in the corner when a game starts", IsOn: () => SettingsService.ShowLaunchToast,
                Activate: () => SettingsService.ShowLaunchToast = !SettingsService.ShowLaunchToast),
            new("Download cover art from the internet", IsOn: () => SettingsService.OnlineCovers,
                Activate: () =>
                {
                    SettingsService.OnlineCovers = !SettingsService.OnlineCovers;
                    _onlineCoversChanged();
                }),

            new("Install updates automatically when GameDeck starts  (version " + UpdateService.CurrentVersionText + ")",
                IsOn: () => SettingsService.AutoUpdate,
                Activate: () => SettingsService.AutoUpdate = !SettingsService.AutoUpdate),

            new("QUICK LAUNCH", IsHeader: true),
            new("Open a quick launch box with a keyboard shortcut", IsOn: () => SettingsService.QuickLaunchEnabled,
                Activate: () =>
                {
                    SettingsService.QuickLaunchEnabled = !SettingsService.QuickLaunchEnabled;
                    _quickLaunchChanged();
                })
        ]);

        if (SettingsService.QuickLaunchEnabled)   // the keys are typed on a keyboard
            rows.Add(new(_capturingShortcut
                ? "Shortcut: press the new keys on the keyboard…  (Esc to cancel)"
                : "Shortcut: " + GlobalHotkey.Describe(SettingsService.QuickLaunchModifiers, SettingsService.QuickLaunchKey) +
                  "  (choose to change it)", Activate: StartCapturingShortcut));

        rows.AddRange(
        [
            new("Open Big Picture mode from a controller", IsOn: () => SettingsService.ControllerWake,
                Activate: () => SettingsService.ControllerWake = !SettingsService.ControllerWake),

            new("BACKUP", IsHeader: true),
            new("Export backup…", Activate: () => RunSettingsAction(() =>
            {
                _actions.ExportBackup(this);
                return Task.FromResult<string?>(null);
            }, changesLibrary: false)),
            new("Import backup…", Activate: () => RunSettingsAction(() => Task.FromResult(_actions.ImportBackup(this)))),

            new("UNINSTALL", IsHeader: true),
            new("Uninstall GameDeck…", Activate: () => RunSettingsAction(() =>
            {
                _actions.Uninstall(this);
                return Task.FromResult<string?>(null);
            }, changesLibrary: false))
        ]);

        _settingsRows = rows;

        _ = RenderRows(SettingsList, _settingsRows, _settingsIndex, index =>
        {
            _settingsIndex = index;
            ActivateSettingsRow();
        }, index =>
        {
            if (_settingsIndex == index || _capturingShortcut) return;

            _settingsIndex = index;
            RenderOnHover(RenderSettings);
        });
    }

    private void HandleSettings(PadButton button)
    {
        if (_busy) return;   // a scan is running

        if (_capturingShortcut)
        {
            if (button == PadButton.Back)
                StopCapturingShortcut();   // the keys themselves come from the keyboard (Window_PreviewKeyDown)
            return;
        }

        switch (button)
        {
            case PadButton.Up: MoveSetting(-1); break;
            case PadButton.Down: MoveSetting(1); break;
            case PadButton.Confirm: ActivateSettingsRow(); break;
            case PadButton.Back: CloseOverlay(); break;
        }
    }

    private bool _busy;

    /// <summary>Runs one of the actions of the desktop settings page and shows its result line at the bottom of the panel.</summary>
    private async void RunSettingsAction(Func<Task<string?>> action, bool changesLibrary = true)
    {
        if (_busy) return;

        _busy = true;
        ShowSettingsStatus("Working…");

        string? status;
        try
        {
            status = await action();
        }
        catch (Exception ex)
        {
            status = ex.Message;
        }
        finally
        {
            _busy = false;
        }

        if (_closing) return;   // (uninstalling closes GameDeck)

        // scanning, adding, merging or restoring changed the library: the screen behind shows the new one
        if (changesLibrary)
            ReloadLibrary();

        ShowSettingsStatus(status);
    }

    /// <summary>Takes the library as the main window has it now and redraws the screen behind the panel, keeping the selection.</summary>
    private void ReloadLibrary()
    {
        var index = Carousel.SelectedIndex;
        var before = Carousel.SelectedItem as Game;

        var (games, hiddenKeys) = _actions.GetLibrary();
        _library = games;
        _hiddenKeys = hiddenKeys;
        ApplyTab(preserveSelection: true);

        // the selected game may be gone (hidden, removed): stay at the same place in the row
        if (_tab != StatsTab && _items.Count > 0 && (before is null || !_items.Contains(before)))
            Carousel.SelectedIndex = Math.Clamp(index, 0, _items.Count - 1);
    }

    private void ShowSettingsStatus(string? text)
    {
        SettingsStatus.Text = text ?? "";
        SettingsStatus.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---- changing the quick launch shortcut (typed on the keyboard) ----

    private bool _capturingShortcut;

    private void StartCapturingShortcut()
    {
        _capturingShortcut = true;
        RenderSettings();
    }

    private void StopCapturingShortcut()
    {
        _capturingShortcut = false;
        RenderSettings();
    }

    /// <summary>Returns true when the key was used for the shortcut.</summary>
    private bool CaptureShortcut(KeyEventArgs e)
    {
        // Alt combinations arrive as "System" keys
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape)
        {
            StopCapturingShortcut();
            return true;
        }

        // a modifier on its own isn't a shortcut yet: keep waiting for the real key
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.System or Key.None)
            return true;

        uint modifiers = 0;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) modifiers |= GlobalHotkey.Control;
        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0) modifiers |= GlobalHotkey.Alt;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) modifiers |= GlobalHotkey.Shift;
        if ((Keyboard.Modifiers & ModifierKeys.Windows) != 0) modifiers |= GlobalHotkey.Win;

        // Ctrl or Alt (or both) is required: Shift or Win alone, or a bare key, would take over normal typing
        if ((modifiers & (GlobalHotkey.Control | GlobalHotkey.Alt)) == 0)
        {
            ShowSettingsStatus("The shortcut needs Ctrl or Alt. Try again.");
            return true;
        }

        SettingsService.SetQuickLaunchShortcut(modifiers, (uint)KeyInterop.VirtualKeyFromKey(key));
        _quickLaunchChanged();
        ShowSettingsStatus(null);
        StopCapturingShortcut();
        return true;
    }

    private void MoveSetting(int step)
    {
        var next = _settingsIndex;

        do
        {
            next += step;
            if (next < 0 || next >= _settingsRows.Count) return;
        }
        while (_settingsRows[next].IsHeader);

        _settingsIndex = next;
        RenderSettings();
    }

    private void ActivateSettingsRow()
    {
        _settingsRows[_settingsIndex].Activate?.Invoke();

        if (!_closing)          // ("All settings" closed the screen)
            RenderSettings();   // the ticks change
    }

    // ---------- the game menu (the Menu button: the right-click menu of the desktop library) ----------

    /// <summary>One page of the game menu: the main list, or a submenu (Categories, Status).</summary>
    private sealed class MenuPage(string title, Func<List<FilterRow>> build)
    {
        public string Title { get; } = title;
        public Func<List<FilterRow>> Build { get; } = build;
        public int Index { get; set; }
    }

    private readonly Stack<MenuPage> _menuPages = new();
    private List<FilterRow> _menuRows = [];
    private Game? _menuGame;

    private void Carousel_RightClick(object sender, MouseButtonEventArgs e)
    {
        // a right click on a tile selects it and opens its menu, like in the desktop library
        if (_overlay != Overlay.None || e.OriginalSource is not DependencyObject source ||
            ItemsControl.ContainerFromElement(Carousel, source) is not ListBoxItem { DataContext: Game game })
            return;

        Carousel.SelectedItem = game;
        OpenGameMenu();
        e.Handled = true;
    }

    private void OpenGameMenu()
    {
        if (_tab == StatsTab || Carousel.SelectedItem is not Game game) return;

        _menuGame = game;
        _menuPages.Clear();
        _menuPages.Push(new MenuPage(game.DisplayName, () => MainMenuRows(game)));

        ShowOverlay(Overlay.GameMenu);
        RenderGameMenu();
    }

    private void CloseGameMenu()
    {
        _menuPages.Clear();
        CloseOverlay();
    }

    private void RenderGameMenu()
    {
        var page = _menuPages.Peek();
        _menuRows = page.Build();
        page.Index = Math.Clamp(page.Index, 0, _menuRows.Count - 1);

        GameMenuTitle.Text = page.Title;
        FillHints(GameMenuHints, (Labels().Confirm, "Choose"), (Labels().Back, _menuPages.Count > 1 ? "Back" : "Close"));

        _ = RenderRows(GameMenuList, _menuRows, page.Index, index =>
        {
            page.Index = index;
            ActivateMenuRow();
        }, index =>
        {
            if (page.Index == index) return;

            page.Index = index;
            RenderOnHover(RenderGameMenu);
        });
    }

    private void HandleGameMenu(PadButton button)
    {
        var page = _menuPages.Peek();

        switch (button)
        {
            case PadButton.Up: MoveMenu(page, -1); break;
            case PadButton.Down: MoveMenu(page, 1); break;
            case PadButton.Confirm: ActivateMenuRow(); break;

            case PadButton.Back or PadButton.Start:
                if (_menuPages.Count > 1 && button == PadButton.Back)
                {
                    _menuPages.Pop();   // out of a submenu, back to the list
                    RenderGameMenu();
                }
                else
                {
                    CloseGameMenu();
                }
                break;
        }
    }

    private void MoveMenu(MenuPage page, int step)
    {
        var next = Math.Clamp(page.Index + step, 0, _menuRows.Count - 1);
        if (next == page.Index) return;

        page.Index = next;
        RenderGameMenu();
    }

    private void ActivateMenuRow()
    {
        var page = _menuPages.Peek();
        _menuRows[page.Index].Activate?.Invoke();
    }

    /// <summary>Runs one of the game's actions, then shows the library as it is now. The menu closes, or stays for ticks.</summary>
    private void GameAction(Action run, bool closeMenu = true)
    {
        run();

        if (_closing) return;   // (the action closed GameDeck)

        ReloadLibrary();

        if (closeMenu)
            CloseGameMenu();
        else
            RenderGameMenu();   // the ticks changed
    }

    private List<FilterRow> MainMenuRows(Game game)
    {
        var actions = _gameActions;

        var rows = new List<FilterRow>
        {
            new(game.FavoriteMenuText, Activate: () =>
            {
                ToggleFavorite();   // (in the Favorites tab an un-favorited game leaves the row)
                CloseGameMenu();
            }),
            new("Edit game…", Activate: () => GameAction(() => actions.Edit(game, this))),
            new("Launch routine…", Activate: () => GameAction(() => actions.EditRoutine(game, this))),
            new("Categories  ›", Activate: () => OpenSubmenu("Categories", () => CategoryRows(game))),
            new("Status  ›", Activate: () => OpenSubmenu("Status", () => StatusRows(game))),
            new(game.HideMenuText, Activate: () => GameAction(() => actions.ToggleHidden(game))),
            new("Add cover art…", Activate: () => GameAction(() => actions.AddCover(game, this)))
        };

        if (game.HasCustomCover)
            rows.Add(new("Remove cover art", Activate: () => GameAction(() => _ = actions.RemoveCover(game))));

        rows.Add(new("Uninstall", Activate: () => GameAction(() => actions.Uninstall(game))));
        rows.Add(new("Remove from library", Activate: () => GameAction(() => actions.RemoveFromLibrary(game, this))));

        return rows;
    }

    private void OpenSubmenu(string title, Func<List<FilterRow>> build)
    {
        _menuPages.Push(new MenuPage(title, build));
        RenderGameMenu();
    }

    private static bool InCategory(Game game, string category) =>
        game.Tags.Any(t => t.Equals(category, StringComparison.CurrentCultureIgnoreCase));

    /// <summary>Every category, ticked when the game is in it, then New / Manage (like the desktop menu).</summary>
    private List<FilterRow> CategoryRows(Game game)
    {
        var actions = _gameActions;
        var categories = actions.AllCategories();

        var rows = categories
            .Select(c => new FilterRow(c, IsOn: () => InCategory(game, c),
                Activate: () => GameAction(() => actions.ToggleCategory(game, c), closeMenu: false)))
            .ToList();

        rows.Add(new("New category…", Activate: () => GameAction(() => actions.NewCategory(game, this), closeMenu: false)));

        if (categories.Count > 0)   // nothing to manage until a category exists
            rows.Add(new("Manage categories…", Activate: () => GameAction(() => actions.ManageCategories(this), closeMenu: false)));

        return rows;
    }

    private List<FilterRow> StatusRows(Game game) =>
        Enum.GetValues<GameStatus>()
            .Select(s => new FilterRow(s.Label(), IsOn: () => game.Status == s, IsRadio: true,
                Activate: () => GameAction(() => _gameActions.SetStatus(game, s))))
            .ToList();

    // ---------- power menu ----------

    // true while the "exit Big Picture?" question (from the Back button) is showing instead of the Power menu
    private bool _exitQuestion;

    private void AskToExit()
    {
        _exitQuestion = true;
        _powerConfirming = new PowerEntry("Exit Big Picture Mode", CloseScreen, "Exit Big Picture mode?", "Exit");
        _powerIndex = 0;
        ShowOverlay(Overlay.Power);
        RenderPower();
    }

    private void OpenPower()
    {
        _exitQuestion = false;
        _powerConfirming = null;
        _powerIndex = 0;
        ShowOverlay(Overlay.Power);
        RenderPower();
    }

    /// <summary>The menu, in three groups like Steam's: the PC, GameDeck, and Cancel. Dangerous ones ask again first.</summary>
    private List<List<PowerEntry>> PowerMenu() =>
    [
        [
            new PowerEntry("Suspend System", () => { CloseOverlay(); SystemPower.Suspend(); }),
            new PowerEntry("Turn Off System", SystemPower.TurnOff, "Turn off this PC?", "Turn off now"),
            new PowerEntry("Restart System", SystemPower.Restart, "Restart this PC?", "Restart now")
        ],
        [
            new PowerEntry("Minimize GameDeck", () => CloseThen(_minimizeApp)),
            new PowerEntry("Exit Big Picture Mode", CloseScreen),
            new PowerEntry("Exit GameDeck", () => CloseThen(_exitApp), "Exit GameDeck?", "Exit GameDeck")
        ],
        [
            new PowerEntry("Cancel", CloseOverlay)
        ]
    ];

    private void RenderPower()
    {
        PowerList.Children.Clear();

        if (_powerConfirming is { } entry)
        {
            PowerTitle.Text = entry.ConfirmTitle ?? "Are you sure?";
            _powerGroups =
            [
                [(entry.ConfirmButton ?? "Yes", () => { CloseOverlay(); entry.Run(); })],
                [("Cancel", () =>
                {
                    if (_exitQuestion) { CloseOverlay(); return; }   // asked from Back: there is no menu to go back to

                    _powerConfirming = null;
                    _powerIndex = 0;
                    RenderPower();
                })]
            ];
        }
        else
        {
            PowerTitle.Text = "Power";
            _powerGroups = PowerMenu()
                .Select(group => group.Select(e => (e.Text, Run: e.ConfirmTitle is null
                    ? e.Run
                    : () => { _powerConfirming = e; _powerIndex = 0; RenderPower(); })).ToList())
                .ToList();
        }

        var index = 0;
        for (var g = 0; g < _powerGroups.Count; g++)
        {
            if (g > 0)
                PowerList.Children.Add(new Border { Height = 2, Background = Divider });

            foreach (var (text, _) in _powerGroups[g])
            {
                var selected = index == _powerIndex;
                var itemIndex = index;

                var cell = new Border
                {
                    Background = selected ? Brushes.White : Brushes.Transparent,
                    Padding = new Thickness(24, 15, 24, 15),
                    Cursor = Cursors.Hand,
                    Child = new TextBlock { Text = text, FontSize = 19, Foreground = selected ? Dark : TextGrey }
                };

                cell.MouseEnter += (_, _) =>
                {
                    if (_powerIndex == itemIndex) return;

                    _powerIndex = itemIndex;   // the mouse selects what it points at, like the stick does
                    RenderPower();
                };

                cell.MouseLeftButtonUp += (_, _) =>
                {
                    _powerIndex = itemIndex;
                    ActivatePower();
                };

                PowerList.Children.Add(cell);
                index++;
            }
        }
    }

    private void HandlePower(PadButton button)
    {
        var count = _powerGroups.Sum(g => g.Count);

        switch (button)
        {
            case PadButton.Up:
                _powerIndex = Math.Max(0, _powerIndex - 1);
                RenderPower();
                break;

            case PadButton.Down:
                _powerIndex = Math.Min(count - 1, _powerIndex + 1);
                RenderPower();
                break;

            case PadButton.Confirm:
                ActivatePower();
                break;

            case PadButton.Back or PadButton.Home:   // Home opens the menu, so it closes it again too
                if (_powerConfirming is not null && !_exitQuestion)
                {
                    _powerConfirming = null;   // back from "are you sure?" to the menu
                    _powerIndex = 0;
                    RenderPower();
                }
                else
                {
                    CloseOverlay();
                }
                break;
        }
    }

    private void ActivatePower() => _powerGroups.SelectMany(g => g).ElementAt(_powerIndex).Run();

    // ---------- menus on top of the screen ----------

    private void ShowOverlay(Overlay overlay)
    {
        _overlay = overlay;
        SearchOverlay.Visibility = overlay == Overlay.Search ? Visibility.Visible : Visibility.Collapsed;
        FilterOverlay.Visibility = overlay == Overlay.Filters ? Visibility.Visible : Visibility.Collapsed;
        PowerOverlay.Visibility = overlay == Overlay.Power ? Visibility.Visible : Visibility.Collapsed;
        DetailsOverlay.Visibility = overlay == Overlay.Details ? Visibility.Visible : Visibility.Collapsed;
        SettingsOverlay.Visibility = overlay == Overlay.Settings ? Visibility.Visible : Visibility.Collapsed;
        GameMenuOverlay.Visibility = overlay == Overlay.GameMenu ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CloseOverlay()
    {
        ShowOverlay(Overlay.None);
        UpdateDetails();
    }

    // ---------- the Stats tab ----------

    private void BuildStats()
    {
        var played = _library.Where(g => g.PlaytimeSeconds > 0).OrderByDescending(g => g.PlaytimeSeconds).ToList();
        var total = played.Sum(g => g.PlaytimeSeconds);

        StatTotal.Text = total <= 0 ? "0 min" : Game.FormatPlaytime(total);
        StatPlayed.Text = played.Count.ToString();
        StatLibrary.Text = _library.Count.ToString();

        var week = _sessions.SecondsThisWeek();
        StatWeek.Text = week <= 0 ? "0 min" : week < 3600 ? $"{week / 60} min" : $"{week / 3600.0:0.#} h";

        var (current, longest) = _sessions.Streaks();
        StatStreak.Text = current == 1 ? "1 day" : $"{current} days";
        StatLongest.Text = longest == 1 ? "1 day" : $"{longest} days";

        DaysChart.ItemsSource = _sessions.LastDays(7);
        WeeksChart.ItemsSource = _sessions.LastWeeks(8);

        // most played: a bar per game, the longest one full width
        TopList.Children.Clear();
        NoPlaytimeText.Visibility = played.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var most = played.FirstOrDefault()?.PlaytimeSeconds ?? 1;
        foreach (var game in played.Take(5))
        {
            var fraction = Math.Clamp((double)game.PlaytimeSeconds / most, 0.02, 1);

            var bar = new Grid { Height = 12 };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(fraction, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - fraction + 0.0001, GridUnitType.Star) });
            bar.Children.Add(new Border { CornerRadius = new CornerRadius(6), Background = Purple });

            var row = new Grid { Margin = new Thickness(0, 0, 0, 14) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });

            row.Children.Add(new TextBlock
            {
                Text = game.DisplayName, Foreground = Brushes.White, FontSize = 18,
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 16, 0)
            });

            Grid.SetColumn(bar, 1);
            row.Children.Add(bar);

            var time = new TextBlock
            {
                Text = Game.FormatPlaytime(game.PlaytimeSeconds), Foreground = TextGrey,
                FontSize = 16, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(time, 2);
            row.Children.Add(time);

            TopList.Children.Add(row);
        }

        // sessions of hidden games are left out, so their names don't show up here
        var recent = _sessions.Recent(8, excludeKeys: _hiddenKeys);
        SessionsList.ItemsSource = recent;
        NoSessionsText.Visibility = recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- covers (same approach as the main window's tiles) ----------

    private void Tile_Loaded(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not Game game) return;

        _touchedCovers.TryAdd(game, game.CoverWanted);
        game.CoverWanted = true;

        if (game.Cover is null)
            _ = CoverService.LoadCoverAsync(game);
    }

    private void Tile_Unloaded(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not Game game) return;

        // scrolled far away: free the picture, unless the main window's own tile still shows this game
        if (_touchedCovers.Remove(game, out var mainWindowWantedIt) && !mainWindowWantedIt)
        {
            game.CoverWanted = false;
            game.Cover = null;
        }
    }

    // ---------- the controller shown on screen, and the button hints ----------

    private void UpdateClock() => ClockText.Text = DateTime.Now.ToString("t");

    private void UpdateControllerUi()
    {
        StatusText.Text = !_pad.Available
            ? "Controller support isn't available. The keyboard works."
            : _pad.ActiveKind == ControllerKind.None
                ? "No controller found. Connect one, or use the keyboard."
                : _pad.ActiveName;

        BuildMainHints();
        BuildOverlayHints();
        BuildTabs();
    }

    // built once: the labels are asked for every time a hint bar, a tab row or a panel is drawn
    private static readonly ButtonLabels PlayStationLabels = new("✕", "○", "□", "△", "L1", "R1", "Share", "Options", "R2", "PS");
    private static readonly ButtonLabels NintendoLabels = new("A", "B", "X", "Y", "L", "R", "−", "+", "ZR", "Home");
    private static readonly ButtonLabels XboxLabels = new("A", "B", "X", "Y", "LB", "RB", "View", "Menu", "RT", "Xbox");
    private static readonly ButtonLabels KeyboardLabels = new("Enter", "Esc", "F", "S", "PgUp", "PgDn", "Tab", "P", "R", "P");   // no controller

    private ButtonLabels Labels() => _pad.ActiveKind switch
    {
        ControllerKind.PlayStation => PlayStationLabels,
        ControllerKind.Nintendo => NintendoLabels,
        ControllerKind.Xbox or ControllerKind.Generic => XboxLabels,
        _ => KeyboardLabels
    };

    private static void FillHints(Panel panel, params (string Button, string Action)[] hints)
    {
        panel.Children.Clear();

        foreach (var (button, action) in hints)
        {
            panel.Children.Add(Chip(button));
            panel.Children.Add(new TextBlock
            {
                Text = action, Foreground = TextGrey, FontSize = 16, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 30, 0)
            });
        }
    }

    /// <summary>
    /// The bar at the bottom: Power on the left (the controller's Home button), then the game menu, Select and Back on the right,
    /// each with a round button in this screen's own colors.
    /// </summary>
    private void BuildMainHints()
    {
        var l = Labels();
        var onController = _pad.ActiveKind != ControllerKind.None;

        FillBar(HintsLeft, (HomeIcon(l, onController), "POWER"));

        var items = new List<(UIElement Icon, string Text)>();

        if (_tab != StatsTab && !_topFocus)
            items.Add((onController ? OptionsIcon() : Chip(l.Start), "GAME MENU"));

        if (_tab != StatsTab || _topFocus)
            items.Add((RoundButton(l.Confirm, onController), "SELECT"));

        items.Add((RoundButton(l.Back, onController), "BACK"));

        FillBar(HintsPanel, items.ToArray());
    }

    private static void FillBar(Panel panel, params (UIElement Icon, string Text)[] items)
    {
        panel.Children.Clear();

        foreach (var (icon, text) in items)
        {
            panel.Children.Add(icon);
            panel.Children.Add(new TextBlock
            {
                Text = text, Foreground = Brushes.White, FontSize = 16, FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 34, 0)
            });
        }

        // no gap after the last one, so the bar lines up with the screen's edge
        if (panel.Children.Count > 0 && panel.Children[^1] is TextBlock last)
            last.Margin = new Thickness(12, 0, 0, 0);
    }

    /// <summary>A round button in the screen's dark key color with the controller's label ("A", "B", "✕", "○")...; the keyboard key when there is no controller.</summary>
    private static UIElement RoundButton(string text, bool onController) =>
        onController ? Round(new TextBlock { Text = text, FontSize = 18 }) : Chip(text);

    private static Border Round(TextBlock content)
    {
        content.Foreground = Brushes.White;
        content.FontWeight = FontWeights.Bold;
        content.HorizontalAlignment = HorizontalAlignment.Center;
        content.VerticalAlignment = VerticalAlignment.Center;

        return new Border
        {
            Width = 34, Height = 34, CornerRadius = new CornerRadius(17), Background = KeyBackground,
            VerticalAlignment = VerticalAlignment.Center, Child = content
        };
    }

    /// <summary>The Home button of the controller in use: the Xbox button, the PS button, or Home (a house).</summary>
    private UIElement HomeIcon(ButtonLabels labels, bool onController)
    {
        if (!onController)
            return Chip(labels.Home);

        return _pad.ActiveKind switch
        {
            ControllerKind.Xbox => Logo(XboxLogo, 34),
            ControllerKind.PlayStation => Logo(PlayStationLogo, 30),
            _ => Round(new TextBlock { Text = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 17 })
        };
    }

    // The Xbox and PlayStation marks as vector shapes (24 x 24, from the Simple Icons set, CC0), so they stay sharp at any size.
    // "F1" = the usual (non-zero) fill rule of SVG, which WPF doesn't use by default.
    private static readonly Geometry XboxLogo = FrozenGeometry(
        "F1 M4.102 21.033C6.211 22.881 8.977 24 12 24c3.026 0 5.789-1.119 7.902-2.967 1.877-1.912-4.316-8.709-7.902-11.417-3.582 2.708-9.779 9.505-7.898 11.417zm11.16-14.406c2.5 2.961 7.484 10.313 6.076 12.912C23.002 17.48 24 14.861 24 12.004c0-3.34-1.365-6.362-3.57-8.536 0 0-.027-.022-.082-.042-.063-.022-.152-.045-.281-.045-.592 0-1.985.434-4.805 3.246zM3.654 3.426c-.057.02-.082.041-.086.042C1.365 5.642 0 8.664 0 12.004c0 2.854.998 5.473 2.661 7.533-1.401-2.605 3.579-9.951 6.08-12.91-2.82-2.813-4.216-3.245-4.806-3.245-.131 0-.223.021-.281.046v-.002zM12 3.551S9.055 1.828 6.755 1.746c-.903-.033-1.454.295-1.521.339C7.379.646 9.659 0 11.984 0H12c2.334 0 4.605.646 6.766 2.085-.068-.046-.615-.372-1.52-.339C14.946 1.828 12 3.545 12 3.545v.006z");

    private static readonly Geometry PlayStationLogo = FrozenGeometry(
        "F1 M8.984 2.596v17.547l3.915 1.261V6.688c0-.69.304-1.151.794-.991.636.18.76.814.76 1.505v5.875c2.441 1.193 4.362-.002 4.362-3.152 0-3.237-1.126-4.675-4.438-5.827-1.307-.448-3.728-1.186-5.39-1.502zm4.656 16.241l6.296-2.275c.715-.258.826-.625.246-.818-.586-.192-1.637-.139-2.357.123l-4.205 1.5V14.98l.24-.085s1.201-.42 2.913-.615c1.696-.18 3.785.03 5.437.661 1.848.601 2.04 1.472 1.576 2.072-.465.6-1.622 1.036-1.622 1.036l-8.544 3.107V18.86zM1.807 18.6c-1.9-.545-2.214-1.668-1.352-2.32.801-.586 2.16-1.052 2.16-1.052l5.615-2.013v2.313L4.205 17c-.705.271-.825.632-.239.826.586.195 1.637.15 2.343-.12L8.247 17v2.074c-.12.03-.256.044-.39.073-1.939.331-3.996.196-6.038-.479z");

    private static Geometry FrozenGeometry(string data)
    {
        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }

    private static UIElement Logo(Geometry shape, double size) => new System.Windows.Shapes.Path
    {
        Data = shape, Fill = Brushes.White, Stretch = Stretch.Uniform, Width = size, Height = size,
        VerticalAlignment = VerticalAlignment.Center
    };

    /// <summary>The controller's Menu / Options button: three lines.</summary>
    private static UIElement OptionsIcon() => new TextBlock
    {
        Text = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), Foreground = Brushes.White, FontSize = 30,
        VerticalAlignment = VerticalAlignment.Center
    };

    /// <summary>The hints inside the search keyboard and the filters panel (the Power menu needs none).</summary>
    private void BuildOverlayHints()
    {
        var l = Labels();

        FillHints(SearchHints, (l.Confirm, "Type"), (l.X, "Delete"), (l.Y, "Space"), (l.Start, "Done"), (l.Back, "Close"));
        FillHints(FilterHints, (l.Confirm, "Choose"), (l.Back, "Close"));
    }

    private void BuildTabs()
    {
        var l = Labels();
        TabsPanel.Children.Clear();

        TabsPanel.Children.Add(Chip(l.Left));

        for (var i = 0; i < TabNames.Length; i++)
        {
            var index = i;
            var selected = i == _tab;

            var tab = new Border
            {
                Margin = new Thickness(22, 0, 0, 0),
                Padding = new Thickness(0, 4, 0, 6),
                BorderThickness = new Thickness(0, 0, 0, 3),
                BorderBrush = selected ? Purple : Brushes.Transparent,
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
                Child = new TextBlock
                {
                    Text = TabNames[i],
                    FontSize = 24,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = selected ? Brushes.White : Muted
                }
            };

            tab.MouseLeftButtonUp += (_, _) =>
            {
                if (_overlay != Overlay.None) return;

                _tab = index;
                ApplyTab(preserveSelection: false);
            };

            TabsPanel.Children.Add(tab);
        }

        var right = Chip(l.Right);
        right.Margin = new Thickness(22, 0, 0, 0);
        TabsPanel.Children.Add(right);
    }

    /// <summary>A small rounded label like a button cap: "A", "✕", "LB"...</summary>
    private static Border Chip(string text) => new()
    {
        Background = KeyBackground,
        CornerRadius = new CornerRadius(12),
        Padding = new Thickness(11, 3, 11, 3),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = text, Foreground = Brushes.White, FontSize = 15, FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        }
    };

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;

            var deeper = FindChild<T>(child);
            if (deeper is not null) return deeper;
        }

        return null;
    }
}
