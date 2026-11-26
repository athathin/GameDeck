using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GameDeck.Models;

namespace GameDeck.Views;

/// <summary>
/// The Spotlight-style box opened by the global shortcut: type a few letters of a game, press Enter, it starts.
/// Closes itself when it loses focus. Empty search = the games played most recently.
/// </summary>
public partial class QuickLaunchWindow : Window
{
    private const int MaxResults = 8;

    private readonly List<Game> _games;
    private readonly Action<Game> _launch;
    private bool _closing;

    public QuickLaunchWindow(IEnumerable<Game> games, Action<Game> launch)
    {
        InitializeComponent();

        // hidden games stay out of the way
        _games = games.Where(g => !g.IsHidden && !g.IsRandomTile).ToList();
        _launch = launch;

        ShowResults("");
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // horizontally centered, a fifth of the way down the primary screen
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - ActualWidth) / 2;
        Top = area.Top + area.Height * 0.2;

        Activate();
        SearchBox.Focus();
    }

    // ---------- searching ----------

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ShowResults(SearchBox.Text.Trim());

    private void ShowResults(string query)
    {
        var results = Search(query);

        ResultList.ItemsSource = results;
        ResultList.SelectedIndex = results.Count > 0 ? 0 : -1;

        ResultList.Visibility = results.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Visibility = results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Best matches first: names that start with the text, then names with a word that starts with it,
    /// then names that contain it. Ties go to the game played most recently.
    /// </summary>
    private List<Game> Search(string query)
    {
        if (query.Length == 0)
        {
            return _games
                .OrderByDescending(g => g.LastPlayed ?? DateTime.MinValue)
                .ThenBy(g => g.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .Take(MaxResults)
                .ToList();
        }

        return _games
            .Select(g => (Game: g, Rank: Rank(g, query)))
            .Where(x => x.Rank < int.MaxValue)
            .OrderBy(x => x.Rank)
            .ThenByDescending(x => x.Game.LastPlayed ?? DateTime.MinValue)
            .ThenBy(x => x.Game.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Take(MaxResults)
            .Select(x => x.Game)
            .ToList();
    }

    private static int Rank(Game game, string query)
    {
        var best = int.MaxValue;

        foreach (var name in new[] { game.DisplayName, game.Name }.Distinct())
        {
            if (name.StartsWith(query, StringComparison.CurrentCultureIgnoreCase))
                return 0;

            if (name.Contains(" " + query, StringComparison.CurrentCultureIgnoreCase))
                best = Math.Min(best, 1);
            else if (name.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                best = Math.Min(best, 2);
        }

        return best;
    }

    // ---------- keyboard / mouse ----------

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                CloseBox();
                break;

            case Key.Enter:
                LaunchSelected();
                break;

            case Key.Down:
                MoveSelection(1);
                break;

            case Key.Up:
                MoveSelection(-1);
                break;

            default:
                return;
        }

        e.Handled = true;
    }

    private void MoveSelection(int step)
    {
        var count = ResultList.Items.Count;
        if (count == 0) return;

        ResultList.SelectedIndex = (ResultList.SelectedIndex + step + count) % count;
        ResultList.ScrollIntoView(ResultList.SelectedItem);
    }

    private void ResultList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // a click on an entry launches it (the selection follows the click first)
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(ResultList, source) is ListBoxItem item)
        {
            item.IsSelected = true;
            LaunchSelected();
            e.Handled = true;
        }
    }

    private void LaunchSelected()
    {
        if (ResultList.SelectedItem is not Game game) return;

        CloseBox();
        _launch(game);
    }

    private void Window_Deactivated(object? sender, EventArgs e) => CloseBox();

    private void CloseBox()
    {
        if (_closing) return;   // closing fires Deactivated again
        _closing = true;
        Close();
    }
}
