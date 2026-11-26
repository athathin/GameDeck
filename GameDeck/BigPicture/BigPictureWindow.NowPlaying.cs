using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
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

            // "still running?" was on screen, and the game has closed meanwhile: nothing left to ask
            if (_askingAboutClose && _overlay == Overlay.Power)
            {
                _powerQuestion = null;
                CloseOverlay();
            }

            _askingAboutClose = false;
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

        // the game's page (open, or under another panel): Resume while its game runs, Play once it has closed
        if (DetailsView.DataContext is Game shown)
            DetailsView.SetPlayLabel(resume: ReferenceEquals(shown, running));
    }

    /// <summary>
    /// A game from GameDeck has ended (told by the main window the moment it happens, also while this screen isn't in front and
    /// its own once-a-second check is paused).
    /// </summary>
    public void GameEnded() => UpdateNowPlaying();

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
                $"Close {game.DisplayName}?", "Close game"),
            // a choice, not an action: each press shows the next one (the menu stays open)
            new PowerEntry($"When it closes:  ‹ {_gameActions.GetAfterGame().Label()} ›", () =>
            {
                _gameActions.SetAfterGame(_gameActions.GetAfterGame().Next());
                RenderPower();
            })
        ];
    }

    // ---------- after the game: sleep or turn off, with a countdown that can be cancelled ----------

    private DispatcherTimer? _countdownTimer;

    /// <summary>
    /// The game closed and "Put the PC to sleep" / "Turn off the PC" was chosen: the Power card counts down, with "now" and Cancel
    /// (Back cancels too). Closing this screen cancels it.
    /// </summary>
    public void StartAfterGameCountdown(AfterGame choice, int seconds)
    {
        StopCountdown();
        if (_closing || choice == AfterGame.ReturnToGameDeck) return;

        var left = seconds;

        _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdownTimer.Tick += (_, _) =>
        {
            // the card was closed some other way (this screen closed, another panel opened): that cancels it
            if (_closing || _overlay != Overlay.Power || _powerQuestion is null)
            {
                StopCountdown();
                return;
            }

            if (--left > 0)
            {
                // only the title changes (the answers and the selection stay as they are)
                var title = choice.Countdown(left);
                _powerQuestion = _powerQuestion.Value with { Title = title };
                PowerTitle.Text = title;
                return;
            }

            StopCountdown();
            _powerQuestion = null;
            CloseOverlay();
            choice.Run();
        };

        AskInPowerCard(choice.Countdown(left),
        [
            (choice.NowText(), () => { StopCountdown(); choice.Run(); }),
            ("Cancel", StopCountdown)
        ]);
        _powerIndex = 1;   // Cancel is selected: a press by chance doesn't turn the PC off
        RenderPower();
        _countdownTimer.Start();
    }

    private void StopCountdown()
    {
        _countdownTimer?.Stop();
        _countdownTimer = null;
    }

    /// <summary>Back to the game: this screen closes and the game's window comes to the front.</summary>
    private void ResumeGame(Game game) => CloseThen(() => _gameActions.Resume(game));

    // true while the "still running" question is on screen
    private bool _askingAboutClose;

    /// <summary>
    /// Asks the game to close the normal way (like its window's X: it may ask to save). The banner says "closing…" and goes
    /// away once the game has closed. If it is still running after 45 seconds, a card asks what to do: go back to the game,
    /// close it anyway, or keep waiting.
    /// </summary>
    private async Task CloseGameAsync(Game game)
    {
        _closingGame = game;
        _closeSlow = false;
        UpdateNowPlaying();

        var closed = await _gameActions.CloseGame(game);
        if (!closed)
            AskStillRunning(game);
    }

    /// <summary>"Keep waiting": another 45 seconds (without asking the game again), then the question once more.</summary>
    private async Task KeepWaitingAsync(Game game)
    {
        _closeSlow = false;
        UpdateNowPlaying();

        for (var waited = 0; waited < 45_000 && !_closing && ReferenceEquals(NowPlaying, game); waited += 500)
            await Task.Delay(500);

        AskStillRunning(game);
    }

    private void AskStillRunning(Game game)
    {
        // (it closed after all, another game runs now, or this screen is closing: nothing to ask)
        if (_closing || !ReferenceEquals(NowPlaying, game) || !ReferenceEquals(_closingGame, game)) return;

        _closeSlow = true;
        UpdateNowPlaying();

        WindowFocus.BringToFront(this);   // (the game may have the focus: the question needs the controller)
        _askingAboutClose = true;
        AskInPowerCard($"{game.DisplayName} is still running",
        [
            ($"Go back to {game.DisplayName}  (it may be asking you to save)", () => { _askingAboutClose = false; ResumeGame(game); }),
            ("Close anyway  (unsaved progress is lost)", () => _ = ForceCloseAsync(game)),
            ("Keep waiting", () => { _askingAboutClose = false; _ = KeepWaitingAsync(game); })
        ]);
    }

    /// <summary>"Close anyway": ends the game's programs. The banner goes away once they have ended.</summary>
    private async Task ForceCloseAsync(Game game)
    {
        _askingAboutClose = false;
        _closeSlow = false;
        UpdateNowPlaying();

        if (!await _gameActions.ForceCloseGame(game) && !_closing && ReferenceEquals(NowPlaying, game))
        {
            _closeSlow = true;   // Windows refused (e.g. it runs as administrator): the banner says it is still running
            UpdateNowPlaying();
        }
    }
}
