using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GameDeck.Models;
using GameDeck.Services;
using static GameDeck.Views.BigPictureLook;

namespace GameDeck.Views;

public partial class BigPictureWindow
{
    // ---------- the game menu (the Menu button: the right-click menu of the desktop library) ----------

    private readonly Stack<MenuPage> _menuPages = new();
    private List<FilterRow> _menuRows = [];

    private void Carousel_RightClick(object sender, MouseButtonEventArgs e)
    {
        // a right click on a tile selects it and opens its menu, like in the desktop library
        if (_overlay != Overlay.None || e.OriginalSource is not DependencyObject source ||
            ItemsControl.ContainerFromElement(Carousel, source) is not ListBoxItem { DataContext: Game game })
            return;

        Carousel.SelectedItem = game;
        OpenGameMenu();
        e.Handled = true;
    }

    private void OpenGameMenu()
    {
        if (_tab == StatsTab || Carousel.SelectedItem is not Game game) return;

        _menuPages.Clear();
        _menuPages.Push(new MenuPage(game.DisplayName, () => MainMenuRows(game)));

        ShowOverlay(Overlay.GameMenu);
        RenderGameMenu();
    }

    private void CloseGameMenu()
    {
        _menuPages.Clear();
        CloseOverlay();
    }

    private void FillGameMenuHints()
    {
        var l = Labels();
        FillHints(GameMenuHints, (l.Confirm, "Choose"), (l.Back, _menuPages.Count > 1 ? "Back" : "Close"));
    }

    private void RenderGameMenu()
    {
        var page = _menuPages.Peek();
        _menuRows = page.Build();
        page.Index = Math.Clamp(page.Index, 0, _menuRows.Count - 1);

        GameMenuTitle.Text = page.Title;
        FillGameMenuHints();

        RenderRows(GameMenuList, _menuRows, page.Index, index =>
        {
            page.Index = index;
            ActivateMenuRow();
        }, index =>
        {
            if (page.Index == index) return;

            page.Index = index;
            RenderOnHover(RenderGameMenu);
        });
    }

    private void HandleGameMenu(PadButton button)
    {
        var page = _menuPages.Peek();

        switch (button)
        {
            case PadButton.Up: MoveMenu(page, -1); break;
            case PadButton.Down: MoveMenu(page, 1); break;
            case PadButton.Confirm: ActivateMenuRow(); break;

            case PadButton.Back or PadButton.Start:
                if (_menuPages.Count > 1 && button == PadButton.Back)
                {
                    _menuPages.Pop();   // out of a submenu, back to the list
                    RenderGameMenu();
                }
                else
                {
                    CloseGameMenu();
                }
                break;
        }
    }

    private void MoveMenu(MenuPage page, int step)
    {
        var next = Math.Clamp(page.Index + step, 0, _menuRows.Count - 1);
        if (next == page.Index) return;

        page.Index = next;
        RenderGameMenu();
    }

    private void ActivateMenuRow()
    {
        var page = _menuPages.Peek();
        _menuRows[page.Index].Activate?.Invoke();
    }

    /// <summary>Runs one of the game's actions, then shows the library as it is now. The menu closes, or stays for ticks.</summary>
    private void GameAction(Action run, bool closeMenu = true)
    {
        run();

        if (_closing) return;   // (the action closed GameDeck)

        ReloadLibrary();

        if (closeMenu)
            CloseGameMenu();
        else
            RenderGameMenu();   // the ticks changed
    }

    private List<FilterRow> MainMenuRows(Game game)
    {
        var actions = _gameActions;

        var rows = new List<FilterRow>
        {
            new(game.FavoriteMenuText, Activate: () =>
            {
                ToggleFavorite();   // (in the Favorites tab an un-favorited game leaves the row)
                CloseGameMenu();
            }),
            new("Edit game…", Activate: () => GameAction(() => actions.Edit(game, this))),
            new(game.HasNotes ? "Edit notes…  (type with the on-screen keyboard)" : "Add a note…  (type with the on-screen keyboard)",
                Activate: () => { CloseGameMenu(); OpenNote(game); }),
            new(game.HasLeftOff ? "Where I left off: change…" : "Where I left off…",
                Activate: () => { CloseGameMenu(); OpenNote(game, leftOff: true); }),
            new("Launch routine…", Activate: () => GameAction(() => actions.EditRoutine(game, this))),
            new("Categories  ›", Activate: () => OpenSubmenu("Categories", () => CategoryRows(game))),
            new("Status  ›", Activate: () => OpenSubmenu("Status", () => StatusRows(game))),
            new(game.HideMenuText, Activate: () => GameAction(() => actions.ToggleHidden(game))),
            new("Add cover art…", Activate: () => GameAction(() => actions.AddCover(game, this)))
        };

        if (game.HasCustomCover)
            rows.Add(new("Remove cover art", Activate: () => GameAction(() => _ = actions.RemoveCover(game))));

        rows.Add(new("Create desktop shortcut", Activate: () => GameAction(() => actions.CreateShortcut(game, this))));

        rows.Add(new("Uninstall", Activate: () => GameAction(() => actions.Uninstall(game))));
        rows.Add(new("Remove from library", Activate: () => GameAction(() => actions.RemoveFromLibrary(game, this))));

        return rows;
    }

    private void OpenSubmenu(string title, Func<List<FilterRow>> build)
    {
        _menuPages.Push(new MenuPage(title, build));
        RenderGameMenu();
    }

    private static bool InCategory(Game game, string category) =>
        game.Tags.Any(t => t.Equals(category, StringComparison.CurrentCultureIgnoreCase));

    /// <summary>Every category, ticked when the game is in it, then New / Manage (like the desktop menu).</summary>
    private List<FilterRow> CategoryRows(Game game)
    {
        var actions = _gameActions;
        var categories = actions.AllCategories();

        var rows = categories
            .Select(c => new FilterRow(c, IsOn: () => InCategory(game, c),
                Activate: () => GameAction(() => actions.ToggleCategory(game, c), closeMenu: false)))
            .ToList();

        rows.Add(new("New category…", Activate: () => GameAction(() => actions.NewCategory(game, this), closeMenu: false)));

        if (categories.Count > 0)   // nothing to manage until a category exists
            rows.Add(new("Manage categories…", Activate: () => GameAction(() => actions.ManageCategories(this), closeMenu: false)));

        return rows;
    }

    private List<FilterRow> StatusRows(Game game) =>
        Enum.GetValues<GameStatus>()
            .Select(s => new FilterRow(s.Label(), IsOn: () => game.Status == s, IsRadio: true,
                Activate: () => GameAction(() => _gameActions.SetStatus(game, s))))
            .ToList();
}
