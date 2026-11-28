using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GameDeck.Models;
using GameDeck.Services;
using GameDeck.Views;

namespace GameDeck;

public partial class MainWindow
{
    // ---------- right-click menu ----------

    /// <summary>
    /// Builds a tile's right-click menu for its game, each time it opens (so it only holds what applies to this game):
    ///   Play · Edit…
    ///   Manage installation › Configure…, Create desktop shortcut, Uninstall, Remove from library (games added by hand)
    ///   Add to favorites · Categories › · Rate › · Status › · Hide game
    /// </summary>
    private void FillGameMenu(ContextMenu menu, Game game)
    {
        menu.Items.Clear();

        menu.Items.Add(MenuEntry(PlayHeader(), () => _ = LaunchGameAsync(game)));
        menu.Items.Add(MenuEntry("Edit…", false, () => EditGame(game, this)));
        menu.Items.Add(new Separator());

        var manage = Submenu(new TextBlock { Text = "Manage installation" });
        manage.Items.Add(MenuEntry("Configure…", false, () => ConfigureGame(game, this)));
        manage.Items.Add(MenuEntry("Create desktop shortcut", false, () => CreateShortcut(game, this)));
        manage.Items.Add(new Separator());
        manage.Items.Add(MenuEntry("Uninstall", false, () => ProviderActions.Uninstall(game)));
        if (game.Source == ManualSource)   // scanned games leave the library by themselves once uninstalled
            manage.Items.Add(MenuEntry("Remove from library", false, () => RemoveFromLibrary(game, this)));
        menu.Items.Add(manage);
        menu.Items.Add(new Separator());

        menu.Items.Add(MenuEntry(game.FavoriteMenuText, false, () => ToggleFavorite(game)));
        menu.Items.Add(CategoriesMenu(game));
        menu.Items.Add(RateMenu(game));
        menu.Items.Add(StatusMenu(game));
        menu.Items.Add(MenuEntry(game.IsHidden ? "Unhide game" : "Hide game", false, () => ToggleHidden(game)));
    }

    /// <summary>A menu entry whose header is more than plain text (a play icon, gold stars, a count).</summary>
    private static MenuItem MenuEntry(FrameworkElement header, Action onClick, bool isChecked = false)
    {
        var item = new MenuItem { Header = header, IsChecked = isChecked };
        item.Click += (_, _) => onClick();
        return item;
    }

    /// <summary>An entry that opens a submenu (the arrow shows by itself once it holds entries).</summary>
    private static MenuItem Submenu(FrameworkElement header) => new() { Header = header };

    /// <summary>"▶ Play", a little stronger than the rest, like the first entry of a launcher's menu.</summary>
    private static FrameworkElement PlayHeader()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TextBlock
        {
            Text = "",   // Segoe MDL2 "Play"
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 1, 10, 0)
        });
        panel.Children.Add(new TextBlock { Text = "Play", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }

    /// <summary>A submenu's name with a small count on its right ("Categories  [2]": how many the game is in).</summary>
    private static FrameworkElement HeaderWithCount(string text, int count)
    {
        var panel = new DockPanel();

        var badge = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 0, 6, 1),
            Margin = new Thickness(14, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = count.ToString(), FontSize = 11 }
        };
        badge.SetResourceReference(Border.BackgroundProperty, "AppBg");   // (darker than the menu, also while the entry is highlighted)
        DockPanel.SetDock(badge, Dock.Right);

        panel.Children.Add(badge);
        panel.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }

    private void ToggleFavorite(Game game)
    {
        _userData.SetFavorite(game, !game.IsFavorite);

        if (_view == "Favorites")
            Refresh();   // un-favorited games leave this view
    }

    // The game actions below are used by the right-click menu here and by the Menu button's game menu in Big Picture.
    // Each takes the window its dialogs belong to.

    /// <summary>"Edit…": the game's details (Info tab) and cover (Media tab). Nothing changes unless OK is pressed.</summary>
    private void EditGame(Game game, Window owner)
    {
        var dialog = new GameSettingsDialog(game) { Owner = owner };
        if (dialog.ShowDialog() != true) return;

        _userData.SaveGameSettings(game, dialog.GameName, dialog.Tags, dialog.Rating, dialog.Notes);

        // only when it was changed, so an untouched note keeps the date it was written
        if (dialog.LeftOff != (game.LeftOff ?? "").Trim())
            SetLeftOff(game, dialog.LeftOff);

        if (dialog.Hidden != game.IsHidden)
            _userData.SetHidden(game, dialog.Hidden);

        if (dialog.RemoveCover)
            _ = CoverService.RemoveCustomCoverAsync(game);
        else if (dialog.NewCoverFile is { } file && !CoverService.SetCustomCover(game, file))
            MessageDialog.Show(owner, "That file couldn't be read as an image. Try a .jpg, .png or .bmp.", "Couldn't use that image");

        Refresh();   // the new name can change the order, new categories show up in the Category menu, hidden games leave
    }

    /// <summary>Manage installation › "Configure…": launch arguments, run as administrator, and the launch routine.</summary>
    private void ConfigureGame(Game game, Window owner)
    {
        var dialog = new ConfigureGameDialog(game, profile => _userData.SetProfile(game, profile)) { Owner = owner };
        if (dialog.ShowDialog() != true) return;

        _userData.SetLaunchOptions(game, dialog.Arguments, dialog.RunAsAdmin);
    }

    /// <summary>Asked by a launch routine before it closes programs. Yes = close and start, No = start without closing, Cancel = don't start.</summary>
    private MessageBoxResult ConfirmClosing(Game game, IReadOnlyList<string> apps)
    {
        var text = $"Before starting {game.DisplayName}, GameDeck will ask these apps to close:\n\n" +
                   string.Join("\n", apps.Select(a => "•  " + a)) +
                   "\n\nThey can offer to save your work first, and apps that don't close are left running." +
                   "\n\nClose & start closes them, then starts the game. Don't close starts the game and leaves them open.";

        // (the dialog copes with this window being hidden in the tray: it then opens centered on screen)
        return MessageDialog.Choose(this, "Close these apps?", text, "Close & start", "Don't close") switch
        {
            MessageChoice.Yes => MessageBoxResult.Yes,
            MessageChoice.No => MessageBoxResult.No,
            _ => MessageBoxResult.Cancel
        };
    }

    // ---------- categories (right-click > Categories) ----------

    private List<string> AllCategories() => _library.Games
        .SelectMany(g => g.Tags)
        .Distinct(StringComparer.CurrentCultureIgnoreCase)
        .OrderBy(t => t, StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    /// <summary>The Categories submenu: New category… first, then every category (ticked if the game is in it), then Manage.</summary>
    private MenuItem CategoriesMenu(Game game)
    {
        var parent = Submenu(HeaderWithCount("Categories", game.Tags.Count));

        parent.Items.Add(MenuEntry("New category…", false, () => AddToNewCategory(game, this)));

        var categories = AllCategories();
        if (categories.Count == 0)
            return parent;   // nothing to tick or manage until a category exists

        parent.Items.Add(new Separator());

        foreach (var category in categories)
        {
            var chosen = category;
            parent.Items.Add(MenuEntry(chosen, HasTag(game, chosen), () => ToggleCategory(game, chosen)));
        }

        parent.Items.Add(new Separator());
        parent.Items.Add(MenuEntry("Manage categories…", false, () => ManageCategories(this)));
        return parent;
    }

    private void ToggleCategory(Game game, string category)
    {
        var tags = game.Tags.ToList();
        var existing = tags.FirstOrDefault(t => t.Equals(category, StringComparison.CurrentCultureIgnoreCase));

        if (existing is not null)
            tags.Remove(existing);
        else
            tags.Add(category);

        _userData.SetTags(game, tags);
        Refresh();
    }

    private void AddToNewCategory(Game game, Window owner)
    {
        var dialog = new TextPromptDialog("New category",
            $"Name the new category. \"{game.DisplayName}\" will be added to it.", "", "Create") { Owner = owner };
        if (dialog.ShowDialog() == true)
            AddToCategory(game, dialog.Value);
    }

    /// <summary>Puts the game in a category, made for it if there is none of that name yet.</summary>
    private void AddToCategory(Game game, string typed)
    {
        // typing the name of an existing category just adds the game to it
        var name = AllCategories().FirstOrDefault(c => c.Equals(typed, StringComparison.CurrentCultureIgnoreCase)) ??
                   (typed.Length > 30 ? typed[..30] : typed);

        if (!HasTag(game, name))
        {
            var tags = game.Tags.ToList();
            tags.Add(name);
            _userData.SetTags(game, tags);
        }

        Refresh();
    }

    private void ManageCategories(Window owner)
    {
        var dialog = new ManageCategoriesDialog(
            load: () => AllCategories()
                .Select(c => (c, _library.Games.Count(g => HasTag(g, c))))
                .ToList(),
            rename: RenameCategory,
            delete: DeleteCategory)
        {
            Owner = owner
        };

        dialog.ShowDialog();
    }

    private void RenameCategory(string oldName, string newName)
    {
        _userData.RenameCategory(oldName, newName);
        if (_categoryFilters.Remove(oldName))
            _categoryFilters.Add(newName);   // keep filtering by it under its new name
        Refresh();
    }

    private void DeleteCategory(string name)
    {
        _userData.DeleteCategory(name);
        Refresh();   // a filter on a deleted category resets itself here
    }

    // ---------- rating (right-click > Rate) ----------

    /// <summary>The Rate submenu: five stars down to one, then No rating; the game's current rating is ticked.</summary>
    private MenuItem RateMenu(Game game)
    {
        var parent = Submenu(new TextBlock { Text = "Rate" });

        for (var stars = 5; stars >= 1; stars--)
        {
            var chosen = stars;
            var header = new TextBlock { Text = new string('★', stars), Foreground = GameSettingsDialog.StarBrush, FontSize = 15 };
            parent.Items.Add(MenuEntry(header, () => SetRating(game, chosen), game.Rating == chosen));
        }

        parent.Items.Add(new Separator());
        parent.Items.Add(MenuEntry("No rating", game.Rating == 0, () => SetRating(game, 0)));
        return parent;
    }

    private void SetRating(Game game, int rating)
    {
        _userData.SetRating(game, rating);
        Refresh();   // sorted by rating, the game moves
    }

    // ---------- status (right-click > Status) ----------

    /// <summary>The Status submenu: No status, then each status, ticked for the game's current one.</summary>
    private MenuItem StatusMenu(Game game)
    {
        var parent = Submenu(new TextBlock { Text = "Status" });

        foreach (var status in Enum.GetValues<GameStatus>())
        {
            var chosen = status;
            parent.Items.Add(MenuEntry(chosen.Label(), game.Status == chosen, () =>
            {
                _userData.SetStatus(game, chosen);
                Refresh();   // the game may no longer match a status filter
            }));

            if (chosen == GameStatus.None)
                parent.Items.Add(new Separator());
        }

        return parent;
    }

    private void ToggleHidden(Game game)
    {
        _userData.SetHidden(game, !game.IsHidden);
        Refresh();
    }

    // ---------- desktop shortcuts ----------

    /// <summary>Puts a shortcut on the desktop that starts the game through GameDeck (playtime, launch routine and all).</summary>
    private void CreateShortcut(Game game, Window owner)
    {
        if (ShortcutService.CreateDesktopShortcut(game) is { } file)
            GameToastWindow.ShowMessage("Shortcut created", game.DisplayName, "On the desktop: " + Path.GetFileName(file));
        else
            MessageDialog.Show(owner, $"The shortcut for {game.DisplayName} couldn't be put on the desktop.", "Couldn't create the shortcut");
    }

    /// <summary>A game's desktop shortcut was opened: start that game, wherever GameDeck is (tray, window, Big Picture).</summary>
    public void LaunchFromShortcut(string launchTarget)
    {
        var game = _library.Games.FirstOrDefault(g => g.LaunchTarget.Equals(launchTarget, StringComparison.OrdinalIgnoreCase));

        if (game is null)
        {
            MessageDialog.Show(IsVisible ? this : null,
                "This shortcut's game isn't in the GameDeck library anymore (it may have been uninstalled or removed).\n\n" +
                "You can delete the shortcut, or scan for games and make a new one from the game's menu.",
                "Game not found");
            return;
        }

        _ = LaunchGameAsync(game);
    }

    private void RemoveFromLibrary(Game game, Window owner)
    {
        if (game.Source != ManualSource)
        {
            MessageDialog.Show(owner, "Only games you added manually can be removed.\n" +
                                      "Scanned games disappear on their own when they're uninstalled.\n" +
                                      "To just get a game out of the way, use \"Hide game\".", "Can't remove this game");
            return;
        }

        _library.Remove(game);
        Refresh();
    }

    /// <summary>What the game menu in Big Picture (the controller's Menu button) can ask of the main window.</summary>
    private BigPictureGameActions BigPictureGameMenu() => new(
        SetName: (game, name) =>
        {
            _userData.SetCustomName(game, name);
            Refresh();   // the new name can change the order
        },
        SetLaunchOptions: (game, arguments, admin) => _userData.SetLaunchOptions(game, arguments, admin),
        SaveRoutine: (game, profile) => _userData.SetProfile(game, profile),
        AllCategories: AllCategories,
        ToggleCategory: ToggleCategory,
        AddToCategory: AddToCategory,
        RenameCategory: RenameCategory,
        DeleteCategory: DeleteCategory,
        SetStatus: (game, status) =>
        {
            _userData.SetStatus(game, status);
            Refresh();
        },
        SetRating: SetRating,
        SetNotes: (game, notes) => _userData.SetNotes(game, notes),
        ToggleHidden: ToggleHidden,
        SetCover: CoverService.SetCustomCover,
        RemoveCover: game => CoverService.RemoveCustomCoverAsync(game),
        CreateShortcut: CreateShortcut,
        Uninstall: ProviderActions.Uninstall,
        RemoveFromLibrary: RemoveFromLibrary,
        RunningGame: () => _tracker.RunningGame,
        Resume: ResumeRunningGame,
        CloseGame: game => game.TrackingFolder is { } folder ? RunningGame.CloseAsync(folder) : Task.FromResult(false),
        ForceCloseGame: game => game.TrackingFolder is { } folder ? RunningGame.ForceCloseAsync(folder) : Task.FromResult(false),
        SetLeftOff: SetLeftOff,
        GetAfterGame: () => _afterGame,
        SetAfterGame: choice => _afterGame = choice);
}
