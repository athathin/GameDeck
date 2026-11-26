using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using GameDeck.Models;
using GameDeck.Services;

namespace GameDeck;

public partial class MainWindow
{
    // ---------- "Continue playing": the games played last, as a small row above the library ----------

    private const int ContinueCount = 4;

    private readonly ObservableCollection<Game> _continueGames = [];

    /// <summary>Shows the row on the plain All games list (no search, no filters); hides it everywhere else.</summary>
    private void UpdateContinue(bool wanted)
    {
        var games = wanted
            ? _library.Games.Where(g => !g.IsHidden && g.LastPlayed is not null)
                            .OrderByDescending(g => g.LastPlayed).Take(ContinueCount).ToList()
            : [];

        if (ContinueList.ItemsSource is null)
            ContinueList.ItemsSource = _continueGames;

        if (!games.SequenceEqual(_continueGames))
        {
            _continueGames.Clear();
            foreach (var game in games)
                _continueGames.Add(game);
        }

        ContinueBar.Visibility = _continueGames.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // a click opens the game's page, like a tile does
    private void ContinueCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is Game game)
            OpenDetails(game);
    }

    // the row's covers: loaded like the tiles' (see Tile_Loaded)
    private void ContinueCard_Loaded(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not Game game) return;

        game.CoverWanted = true;
        if (game.Cover is null)
            _ = CoverService.LoadCoverAsync(game);
    }
}
