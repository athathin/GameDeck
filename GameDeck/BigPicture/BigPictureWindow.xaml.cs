using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using GameDeck.Models;
using GameDeck.Services;
using static GameDeck.Views.BigPictureLook;

namespace GameDeck.Views;

/// <summary>
/// Big Picture mode: the library full screen, with large tiles, made for a controller on the couch
/// (Xbox, PlayStation and Nintendo controllers; the keyboard and mouse work too).
/// The tabs of the desktop library plus Media, search (with an on-screen keyboard), filters, settings and a Power menu.
/// Choosing a game closes this screen and starts the game through the <c>launch</c> callback, so everything the main
/// window does around a launch still happens.
/// </summary>
public partial class BigPictureWindow : Window
{
    private const double Pitch = 248;   // one tile slot, see BigItem in the XAML
    private const double EdgePad = 6;   // room before the first and after the last tile: the selected one is wider than its slot

    // the same tabs as the desktop library
    private static readonly string[] TabNames = ["All games", "Favorites", "Recently played", "Stats", "Media"];
    private const int StatsTab = 3;

    private enum Overlay { None, Search, Filters, Power, Details, Settings, GameMenu, Browse, Shots, Shortcuts }

    private static readonly string[][] KeyLayout =
    [
        // (the symbols on the right are there for launch arguments too: -dx12, --mode=window, /nolauncher, "C:\a folder")
        ["1", "2", "3", "4", "5", "6", "7", "8", "9", "0", "-"],
        ["Q", "W", "E", "R", "T", "Y", "U", "I", "O", "P", "="],
        ["A", "S", "D", "F", "G", "H", "J", "K", "L", ";", "'"],
        ["Z", "X", "C", "V", "B", "N", "M", ",", ".", "/", "\\"],
        ["Shift", "Caps", "Space", "⌫ Delete", "Clear", "Done"]
    ];

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
    private readonly HashSet<SmartFilter> _smartFilters = [];   // Never played, Played under 1 hour, Recently added
    private readonly HashSet<string> _genreFilters = new(StringComparer.CurrentCultureIgnoreCase);
    private SortMode _sort = SettingsService.SortMode;
    private string _query = "";
    private bool _showHidden;   // the "Hidden games" filter: hidden games are listed too (dimmed)
    private bool _showAddOns;   // the "DLC and soundtracks" filter: Steam add-ons installed as apps are listed too

    private Overlay _overlay = Overlay.None;

    // search keyboard
    private Border[][] _keyCells = [];
    private int _keyRow;
    private int _keyCol;

    // filters panel
    private List<FilterRow> _filterRows = [];
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

        // On the main display (the one marked "Make this my main display" in Windows' display settings), not on whichever
        // screen Windows would pick: a window is maximized on the monitor it sits on when shown, and the main display's top-left
        // corner is where its work area begins. (The XAML's WindowState="Maximized" does the rest.) Opened from the desktop
        // window, the main window moves it to that window's screen instead (ScreenPlacement.OpenOnScreenOf).
        var main = SystemParameters.WorkArea;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = main.Left;
        Top = main.Top;
        Width = Math.Min(800, main.Width);
        Height = Math.Min(600, main.Height);

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
            NavSounds.Play(NavSound.Enter);   // (waits for its file to finish opening if it must)
            _scroll = FindChild<ScrollViewer>(Carousel);
            UpdateTarget();
            _scroll?.ScrollToHorizontalOffset(_targetOffset);
        };
        Closed += (_, _) => Cleanup();

        BuildKeyboard();
        InitContinue();
        DetailsView.ShotsChanged += (_, _) =>
        {
            if (_overlay == Overlay.Details) FillDetailsHints();   // the "Screenshots" hint appears once they are found
        };
        NavSounds.Preload();
        UpdateClock();
        ApplyTab(preserveSelection: false);
        UpdateControllerUi();
        UpdateNowPlaying();
        StartStatus();
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

    /// <summary>Raised just before this screen starts reading the controllers (the background listener must let them go first).</summary>
    public event Action? TakingController;

    // Controllers are only read while this screen is in front, so they aren't touched while a game is running.
    private void Window_Activated(object? sender, EventArgs e)
    {
        if (_closing) return;

        TakingController?.Invoke();
        _pad.Start();
        _timer.Start();
        NoteInput();   // (back in front: the time without input starts again)
        UpdateControllerUi();
        UpdateNowPlaying();   // (a game may have closed while another window was in front)

        HideCursor();   // the pointer only shows up when the mouse is moved
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        _timer.Stop();
        _pad.Stop();
        WakeFromScreenSaver();         // another window is in front: no screen saver behind it

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
        WakeFromScreenSaver();
        SetPadInUse(false);
        ShowCursor();
    }

    private void Cleanup()
    {
        SavePending();   // (a rating changed with left / right just before the screen closed)
        NavSounds.Release();
        StopStatus();
        ScreenSaver.Stop();
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
#if BETA
        LagTick();   // (the Beta logs slow controller input: BigPictureWindow.LagLog.cs)
#endif
        var pressed = _pad.Poll();

        // Home + a button is a shortcut (volume, Alt + Tab...): those buttons aren't presses here. Home's own job (the Power
        // menu) happens when it is let go without a shortcut.
        var homeTapped = ControllerShortcuts.Update(_pad.Held);

        // a press during the screen saver only wakes the screen
        if ((pressed.Count > 0 || homeTapped) && WakeFromScreenSaver())
        {
            pressed.Clear();
            homeTapped = false;
        }

        foreach (var button in pressed)
        {
            if (_closing) break;   // an earlier button already closed this screen (launch, exit...): the rest are stale
            if (ControllerShortcuts.Swallows(button)) continue;

#if BETA
            var started = LagNow;
#endif
            SetPadInUse(true);
            Handle(button);
#if BETA
            LagPress(button, started);
#endif
        }

        if (homeTapped && !_closing)
        {
            SetPadInUse(true);
            Handle(PadButton.Home);
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
        {
            UpdateClock();
            UpdateNowPlaying();   // (the game may have been closed meanwhile)
            UpdateBattery();
            CheckScreenSaver();
        }
    }

}
