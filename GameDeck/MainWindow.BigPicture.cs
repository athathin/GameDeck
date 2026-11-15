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

public partial class MainWindow
{
    // ---------- Big Picture mode (full screen, for a controller) ----------

    private BigPictureWindow? _bigPicture;
    private ControllerWakeService? _wake;

    /// <summary>
    /// Lets a controller open Big Picture mode while GameDeck sits in the tray (Home button, or View + Menu held).
    /// It only listens while that's safe: setting on, Big Picture closed, no game from GameDeck running.
    /// </summary>
    private void StartControllerWake()
    {
        // (the Beta leaves the button to the normal GameDeck while that one is running: two programs must not both answer it)
        _wake = new ControllerWakeService(() =>
            SettingsService.ControllerWake && _bigPicture is null && !_tracker.IsTracking && !AppInfo.NormalGameDeckRunning);

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

        // all games: the hidden ones stay out of sight here too, until the "Hidden games" filter is turned on
        var games = _library.Games.ToList();

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
        else if (games.Where(g => g.LastPlayed is not null).MaxBy(g => g.LastPlayed) is { } lastPlayed)
            _bigPicture.Restore(0, lastPlayed);   // "continue playing": it opens with the game you played last selected

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
}
