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

    /// <summary>Shows or hides the banner under the tabs. Called when the screen opens and every second with the clock.</summary>
    private void UpdateNowPlaying()
    {
        if (NowPlaying is { } game)
        {
            NowPlayingText.Text = game.DisplayName;
            NowPlayingHint.Text = (ShownKind == ControllerKind.None ? "P" : "Home") + ":  resume or close it";
            NowPlayingBanner.Visibility = Visibility.Visible;
        }
        else
        {
            NowPlayingBanner.Visibility = Visibility.Collapsed;
        }
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
    /// Asks the game to close the normal way (like its window's X: it may ask to save). If it is still running after a few
    /// seconds, says so: it is probably asking something, and Resume shows it.
    /// </summary>
    private async Task CloseGameAsync(Game game)
    {
        NowPlayingHint.Text = "closing…";

        if (await _gameActions.CloseGame(game) || _closing) return;   // (the banner goes away once it has closed)

        UpdateNowPlaying();
        MessageDialog.Show(this,
            $"{game.DisplayName} is still running. It may be asking you something (to save, or to confirm), or it doesn't close " +
            "when asked.\n\nChoose Resume to go back to it and close it from the game itself. GameDeck never forces a game to close.",
            "The game didn't close");
    }
}
