using System.Windows;
using System.Windows.Input;
using GameDeck.Models;
using GameDeck.Services;

namespace GameDeck.Views;

public partial class BigPictureWindow
{
    // ---------- now playing: the game started from GameDeck that is running (Resume / Close game) ----------

    /// <summary>The game started from GameDeck that is running now, or null.</summary>
    private Game? NowPlaying => _gameActions.RunningGame();

    // the game asked to close (Close game), and whether it is still running long after that
    private Game? _closingGame;
    private bool _closeSlow;

    /// <summary>
    /// Shows or hides the banner under the tabs, and keeps the open game page's Play / Resume button right.
    /// Called when the screen opens and every second with the clock.
    /// </summary>
    private void UpdateNowPlaying()
    {
        var running = NowPlaying;

        if (!ReferenceEquals(running, _closingGame))
        {
            _closingGame = null;   // it closed (or another game runs now)
            _closeSlow = false;
        }

        if (running is not null)
        {
            var home = ShownKind == ControllerKind.None ? "P" : "Home";
            NowPlayingText.Text = running.DisplayName;
            NowPlayingHint.Text = _closingGame is null ? $"{home}:  resume or close it"
                : _closeSlow ? $"still running: it may be asking you something.  {home}:  resume it"
                : "closing…";
            NowPlayingBanner.Visibility = Visibility.Visible;
        }
        else
        {
            NowPlayingBanner.Visibility = Visibility.Collapsed;
        }

        // the game's page that is open: Resume while its game runs, Play once it has closed
        if (_overlay == Overlay.Details && DetailsView.DataContext is Game shown)
            DetailsView.SetPlayLabel(resume: ReferenceEquals(shown, running));
    }

    /// <summary>
    /// Opens the Power menu with the running game's Resume / Close at the top (Big Picture was opened while the game runs,
    /// like the Home button over a game on a console).
    /// </summary>
    public void ShowNowPlaying()
    {
        UpdateNowPlaying();
        if (NowPlaying is not null)
            OpenPower();
    }

    private void NowPlayingBanner_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_overlay == Overlay.None)
            OpenPower();
    }

    /// <summary>The Power menu's first group while a game runs.</summary>
    private List<PowerEntry> NowPlayingEntries()
    {
        if (NowPlaying is not { } game) return [];

        return
        [
            new PowerEntry($"Resume {game.DisplayName}", () => ResumeGame(game)),
            new PowerEntry($"Close {game.DisplayName}", () => _ = CloseGameAsync(game),
                $"Close {game.DisplayName}?", "Close game")
        ];
    }

    /// <summary>Back to the game: this screen closes and the game's window comes to the front.</summary>
    private void ResumeGame(Game game) => CloseThen(() => _gameActions.Resume(game));

    /// <summary>
    /// Asks the game to close the normal way (like its window's X: it may ask to save). No pop-up: the banner says "closing…"
    /// and goes away once the game has closed; if it is still running long after, the banner says so (it is probably asking
    /// something, and Resume shows it).
    /// </summary>
    private async Task CloseGameAsync(Game game)
    {
        _closingGame = game;
        _closeSlow = false;
        UpdateNowPlaying();

        var closed = await _gameActions.CloseGame(game);
        if (_closing || closed || !ReferenceEquals(_closingGame, game)) return;

        _closeSlow = true;
        UpdateNowPlaying();
    }
}
