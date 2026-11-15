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
    Func<Action<string>, Task<string?>> CheckForUpdates,
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
    private const double Pitch = 248;   // one tile slot, see BigItem in the XAML
    private const double EdgePad = 6;   // room before the first and after the last tile: the selected one is wider than its slot

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

    private static readonly SolidColorBrush Purple = Frozen(0x80, 0x37, 0xFE);   // the accent: the same in every theme

    // the rest follow the theme (these are the shared brushes of ThemeService: their color changes with the theme)
    private static SolidColorBrush TextGrey => ThemeService.TextSecondary;
    private static SolidColorBrush Dark => ThemeService.SelectFg;            // text on the selected row
    private static SolidColorBrush KeyBackground => ThemeService.RaisedBg;
    private static SolidColorBrush Muted => ThemeService.TextMuted;
    private static SolidColorBrush Divider => ThemeService.AppBg;

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
    private bool _showHidden;   // the "Hidden games" filter: hidden games are listed too (dimmed)

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
        Title = AppInfo.DisplayName + " Big Picture";

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
        ThemeService.Changed += OnThemeChanged;
        ApplyScale();
        _timer.Tick += Timer_Tick;
        _cursorTimer.Tick += (_, _) => HideCursor();
        Loaded += (_, _) =>
        {
            _scroll = FindChild<ScrollViewer>(Carousel);
            UpdateTarget();
            _scroll?.ScrollToHorizontalOffset(_targetOffset);
        };
        Closed += (_, _) => Cleanup();

        BuildKeyboard();
        InitContinue();
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

        HideCursor();   // the pointer only shows up when the mouse is moved
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        _timer.Stop();
        _pad.Stop();

        ShowCursor(autoHide: false);   // a dialog or another program is in front: it needs its pointer
    }

    // ---------- the mouse pointer: hidden until the mouse moves ----------

    private readonly DispatcherTimer _cursorTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private Point _cursorPosition;
    private bool _padInUse = true;   // false after the keyboard or mouse was used last: the hints then show their keys
    private bool _mouseMoved;

    /// <summary>The button hints follow whatever was used last: the controller, or the keyboard and mouse.</summary>
    private void SetPadInUse(bool value)
    {
        if (_padInUse == value) return;

        _padInUse = value;
        UpdateControllerUi();
        RefreshOpenPanelHints();
    }

    /// <summary>True only while the user is really using the mouse: rows that scroll under a hidden pointer must not take the selection.</summary>
    private bool PointerIsChoosing => _mouseMoved && Mouse.OverrideCursor is null;   // the last mouse event was a real move (set before the tiles see it)

    /// <summary>Hides the pointer on the whole screen (it also hides the hand over buttons).</summary>
    private void HideCursor()
    {
        _cursorTimer.Stop();
        _mouseMoved = false;
        _cursorPosition = Mouse.GetPosition(this);   // the spot it hides at: only moving away from it counts as movement
        Mouse.OverrideCursor = Cursors.None;
    }

    /// <summary>Shows the pointer; it hides again after a few seconds without movement.</summary>
    private void ShowCursor(bool autoHide = true)
    {
        Mouse.OverrideCursor = null;
        _cursorTimer.Stop();

        if (autoHide)
            _cursorTimer.Start();
    }

    private void Window_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        // tiles that glide under a still mouse also raise this event: only a real move shows the pointer
        var position = e.GetPosition(this);
        _mouseMoved = position != _cursorPosition;
        if (!_mouseMoved) return;

        _cursorPosition = position;
        SetPadInUse(false);
        ShowCursor();
    }

    private void Cleanup()
    {
        _timer.Stop();
        _cursorTimer.Stop();
        Mouse.OverrideCursor = null;   // (the main window and the dialogs get their pointer back)
        _pad.ControllerChanged -= UpdateControllerUi;   // the screen is gone: nothing left to redraw
        ThemeService.Changed -= OnThemeChanged;
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
        {
            if (_closing) break;   // an earlier button already closed this screen (launch, exit...): the rest are stale

            SetPadInUse(true);
            Handle(button);
        }

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

}
