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
    // ---------- right-click menu ----------

    private void FavoriteMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: Game game }) return;

        _userData.SetFavorite(game, !game.IsFavorite);

        if (_view == "Favorites")
            Refresh();   // un-favorited games leave this view
    }

    // The game actions below are used by the right-click menu here and by the Menu button's game menu in Big Picture.
    // Each takes the window its dialogs belong to.

    private void EditGame(Game game, Window owner)
    {
        var dialog = new GameSettingsDialog(game) { Owner = owner };
        if (dialog.ShowDialog() != true) return;

        _userData.SaveGameSettings(game, dialog.GameName, dialog.Arguments, dialog.RunAsAdmin, dialog.Tags, dialog.Rating, dialog.Notes);
        Refresh();   // the new name can change the order, new categories show up in the Category menu
    }

    private void EditMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: Game game })
            EditGame(game, this);
    }

    private void EditLaunchRoutine(Game game, Window owner)
    {
        var dialog = new LaunchRoutineDialog(game) { Owner = owner };
        if (dialog.ShowDialog() != true) return;

        _userData.SetProfile(game, dialog.Result);
    }

    private void RoutineMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: Game game })
            EditLaunchRoutine(game, this);
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

    /// <summary>Fills the Categories submenu: every category (ticked if the game is in it), then New / Manage.</summary>
    private void CategoriesMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, sender) || sender is not MenuItem { DataContext: Game game } parent) return;

        parent.Items.Clear();

        var categories = AllCategories();
        foreach (var category in categories)
        {
            var chosen = category;
            parent.Items.Add(MenuEntry(chosen, HasTag(game, chosen), () => ToggleCategory(game, chosen)));
        }

        if (categories.Count > 0)
            parent.Items.Add(new Separator());

        parent.Items.Add(MenuEntry("New category…", false, () => AddToNewCategory(game, this)));

        // nothing to manage until at least one category exists, so the option isn't shown at all
        if (categories.Count > 0)
            parent.Items.Add(MenuEntry("Manage categories…", false, () => ManageCategories(this)));
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
        if (dialog.ShowDialog() != true) return;

        // typing the name of an existing category just adds the game to it
        var name = AllCategories().FirstOrDefault(c => c.Equals(dialog.Value, StringComparison.CurrentCultureIgnoreCase)) ?? dialog.Value;

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
            rename: (oldName, newName) =>
            {
                _userData.RenameCategory(oldName, newName);
                if (_categoryFilters.Remove(oldName))
                    _categoryFilters.Add(newName);   // keep filtering by it under its new name
                Refresh();
            },
            delete: name =>
            {
                _userData.DeleteCategory(name);
                Refresh();   // a filter on a deleted category resets itself here
            })
        {
            Owner = owner
        };

        dialog.ShowDialog();
    }

    // ---------- status (right-click > Status) ----------

    /// <summary>Fills the Status submenu: None, then each status, ticked for the game's current one.</summary>
    private void StatusMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, sender) || sender is not MenuItem { DataContext: Game game } parent) return;

        parent.Items.Clear();

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
    }

    private void ToggleHidden(Game game)
    {
        _userData.SetHidden(game, !game.IsHidden);
        Refresh();
    }

    private void HideMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: Game game })
            ToggleHidden(game);
    }

    private void AddCover(Game game, Window owner)
    {
        var dialog = new OpenFileDialog
        {
            Title = $"Choose cover art for {game.DisplayName}",
            Filter = "Images (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp"
        };

        if (dialog.ShowDialog() != true) return;

        if (!CoverService.SetCustomCover(game, dialog.FileName))
            MessageDialog.Show(owner, "That file couldn't be read as an image. Try a .jpg, .png or .bmp.", "Couldn't use that image");
    }

    private void AddCoverMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: Game game })
            AddCover(game, this);
    }

    private async void RemoveCoverMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: Game game })
            await CoverService.RemoveCustomCoverAsync(game);
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

    private void ShortcutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: Game game })
            CreateShortcut(game, this);
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

    private void UninstallMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: Game game })
            ProviderActions.Uninstall(game);
    }

    private void RemoveFromLibrary(Game game, Window owner)
    {
        if (game.Source != "Manual")
        {
            MessageDialog.Show(owner, "Only games you added manually can be removed.\n" +
                                      "Scanned games disappear on their own when they're uninstalled.\n" +
                                      "To just get a game out of the way, use \"Hide from library\".", "Can't remove this game");
            return;
        }

        _library.Remove(game);
        Refresh();
    }

    private void RemoveMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: Game game })
            RemoveFromLibrary(game, this);
    }

    /// <summary>What the game menu in Big Picture (the controller's Menu button) can ask of the main window.</summary>
    private BigPictureGameActions BigPictureGameMenu() => new(
        Edit: EditGame,
        EditRoutine: EditLaunchRoutine,
        AllCategories: AllCategories,
        ToggleCategory: ToggleCategory,
        NewCategory: AddToNewCategory,
        ManageCategories: ManageCategories,
        SetStatus: (game, status) =>
        {
            _userData.SetStatus(game, status);
            Refresh();
        },
        SetNotes: (game, notes) => _userData.SetNotes(game, notes),
        ToggleHidden: ToggleHidden,
        AddCover: AddCover,
        RemoveCover: game => CoverService.RemoveCustomCoverAsync(game),
        CreateShortcut: CreateShortcut,
        Uninstall: ProviderActions.Uninstall,
        RemoveFromLibrary: RemoveFromLibrary,
        RunningGame: () => _tracker.RunningGame,
        Resume: ResumeRunningGame,
        CloseGame: game => game.TrackingFolder is { } folder ? RunningGame.CloseAsync(folder) : Task.FromResult(false),
        ForceCloseGame: game => game.TrackingFolder is { } folder ? RunningGame.ForceCloseAsync(folder) : Task.FromResult(false));
}
