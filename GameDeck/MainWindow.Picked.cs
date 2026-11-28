using System.Windows.Controls;
using GameDeck.Models;
using GameDeck.Views;

namespace GameDeck;

public partial class MainWindow
{
    // ---------- several games at once: Ctrl / Shift + click tiles, then right-click one of them ----------

    /// <summary>
    /// The games the right-click menu works on: every selected game when <paramref name="clicked"/> is one of them (and more than
    /// one is selected), otherwise just the clicked game.
    /// </summary>
    private List<Game> PickedGames(Game clicked)
    {
        var selected = GameList.SelectedItems.OfType<Game>().Where(g => !g.IsRandomTile).ToList();
        return selected.Count > 1 && selected.Contains(clicked) ? selected : [clicked];
    }

    private string? _pickedCountText;   // the count as last shown: a change of selection clears the line only while it still says this

    /// <summary>
    /// The status line says how many games are selected (and what to do with them) while more than one is; once that's over,
    /// the line is cleared (only if it still shows the count: another message stays).
    /// </summary>
    private void ShowPickedCount()
    {
        var count = GameList.SelectedItems.OfType<Game>().Count(g => !g.IsRandomTile);

        if (count > 1)
        {
            StatusText.Text = _pickedCountText =
                $"{count} games selected. Right-click one of them to change them all, or click an empty spot to stop.";
        }
        else if (_pickedCountText is not null)
        {
            if (StatusText.Text == _pickedCountText)
                StatusText.Text = "";
            _pickedCountText = null;
        }
    }

    /// <summary>The selection changed any way at all (a click, Ctrl / Shift + click, an empty spot, a game leaving the list).</summary>
    private void GameList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // "Pick a game for me" is no game: never part of several selected ones
        if (GameList.SelectedItems.Count > 1 && GameList.SelectedItems.OfType<Game>().FirstOrDefault(g => g.IsRandomTile) is { } random)
        {
            GameList.SelectedItems.Remove(random);   // (raises this again, which shows the count)
            return;
        }

        ShowPickedCount();
    }

    /// <summary>Changes every picked game, writing the data file once, then shows the library as it is now.</summary>
    private void ChangeAll(List<Game> games, Action<Game> change)
    {
        _userData.Batch(() =>
        {
            foreach (var game in games)
                change(game);
        });

        Refresh();
        ShowPickedCount();
    }

    /// <summary>
    ///   N games selected
    ///   Add all to favorites · Categories › · Rate › · Status › · Hide all
    ///   Clear selection
    /// </summary>
    private void FillPickedMenu(ContextMenu menu, List<Game> games)
    {
        var count = games.Count;

        menu.Items.Add(new MenuItem
        {
            Header = new TextBlock { Text = $"{count} games selected", FontWeight = System.Windows.FontWeights.SemiBold },
            IsEnabled = false
        });
        menu.Items.Add(new Separator());

        var allFavorite = games.All(g => g.IsFavorite);
        menu.Items.Add(WithIcon(MenuEntry(allFavorite ? "Remove all from favorites" : "Add all to favorites", false,
            () => ChangeAll(games, g => _userData.SetFavorite(g, !allFavorite))), allFavorite ? UnfavoriteIcon : FavoriteIcon));

        // categories: ticked when every picked game is in it; choosing one puts them all in (or, when all are, takes them all out)
        var categories = WithIcon(Submenu(new TextBlock { Text = "Categories" }), CategoryIcon);
        categories.Items.Add(MenuEntry("New category…", false, () =>
        {
            var dialog = new TextPromptDialog("New category", $"Name the new category. The {count} selected games will be added to it.",
                                              "", "Create") { Owner = this };
            if (dialog.ShowDialog() != true) return;

            var name = AllCategories().FirstOrDefault(c => c.Equals(dialog.Value, StringComparison.CurrentCultureIgnoreCase)) ??
                       (dialog.Value.Length > 30 ? dialog.Value[..30] : dialog.Value);
            ChangeAll(games, g => SetInCategory(g, name, true));
        }));

        var all = AllCategories();
        if (all.Count > 0)
            categories.Items.Add(new Separator());

        foreach (var category in all)
        {
            var chosen = category;
            var everyone = games.All(g => HasTag(g, chosen));
            categories.Items.Add(MenuEntry(chosen, everyone, () => ChangeAll(games, g => SetInCategory(g, chosen, !everyone))));
        }
        menu.Items.Add(categories);

        // rating and status: ticked when all picked games have the same one
        var rate = WithIcon(Submenu(new TextBlock { Text = "Rate" }), RateIcon);
        var sameRating = games.Select(g => g.Rating).Distinct().Count() == 1 ? games[0].Rating : -1;
        for (var stars = 5; stars >= 1; stars--)
        {
            var chosen = stars;
            var header = new TextBlock { Text = new string('★', stars), Foreground = GameSettingsDialog.StarBrush, FontSize = 15 };
            rate.Items.Add(MenuEntry(header, () => ChangeAll(games, g => _userData.SetRating(g, chosen)), sameRating == chosen));
        }
        rate.Items.Add(new Separator());
        rate.Items.Add(MenuEntry("No rating", sameRating == 0, () => ChangeAll(games, g => _userData.SetRating(g, 0))));
        menu.Items.Add(rate);

        var status = WithIcon(Submenu(new TextBlock { Text = "Status" }), StatusIcon);
        var sameStatus = games.Select(g => g.Status).Distinct().Count() == 1 ? games[0].Status : (GameStatus?)null;
        foreach (var value in Enum.GetValues<GameStatus>())
        {
            var chosen = value;
            status.Items.Add(MenuEntry(chosen.Label(), sameStatus == chosen, () => ChangeAll(games, g => _userData.SetStatus(g, chosen))));
            if (chosen == GameStatus.None)
                status.Items.Add(new Separator());
        }
        menu.Items.Add(status);

        var allHidden = games.All(g => g.IsHidden);
        menu.Items.Add(WithIcon(MenuEntry(allHidden ? $"Unhide all {count}" : $"Hide all {count}", false,
            () => ChangeAll(games, g => _userData.SetHidden(g, !allHidden))), allHidden ? UnhideIcon : HideIcon));

        menu.Items.Add(new Separator());
        menu.Items.Add(WithIcon(MenuEntry("Clear selection", false, () =>
        {
            GameList.SelectedItem = null;
            ShowPickedCount();
        }), ClearIcon));
    }

    /// <summary>Puts the game in the category (or takes it out), without saving the other games' categories.</summary>
    private void SetInCategory(Game game, string category, bool inIt)
    {
        if (HasTag(game, category) == inIt) return;

        var tags = game.Tags.ToList();
        if (inIt)
            tags.Add(category);
        else
            tags.RemoveAll(t => t.Equals(category, StringComparison.CurrentCultureIgnoreCase));

        _userData.SetTags(game, tags);
    }
}
