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
        var hints = new List<(string, string)> { (l.Confirm, "Choose"), (l.Back, _menuPages.Count > 1 ? "Back" : "Close") };
        AddScrollHint(hints, Scrollable(ScrollOf(GameMenuList)));
        FillHints(GameMenuHints, hints.ToArray());
    }

    private void RenderGameMenu()
    {
        var page = _menuPages.Peek();
        _menuRows = page.Build();
        page.Index = Math.Clamp(page.Index, 0, _menuRows.Count - 1);

        // never on a heading (the launch routine has some): the next row that can be chosen, or the one before
        if (_menuRows[page.Index].IsHeader)
        {
            var below = _menuRows.FindIndex(page.Index, r => !r.IsHeader);
            page.Index = below >= 0 ? below : Math.Max(0, _menuRows.FindLastIndex(page.Index, r => !r.IsHeader));
        }

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
            case PadButton.LeftTrigger or PadButton.RightTrigger:
                PageThrough(() => page.Index, step => MoveMenu(page, step), TriggerDirection(button));
                break;
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
        var next = page.Index;
        do
        {
            next += step;
            if (next < 0 || next >= _menuRows.Count) return;   // at the end: stay where we are
        }
        while (_menuRows[next].IsHeader);

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
            new("Launch routine  ›", Activate: () => OpenSubmenu("Launch routine", () => RoutineRows(game))),
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

    // ---------- "Free up space" (the Stats tab's A button, or a click on its Space on disk card) ----------

    /// <summary>The games whose uninstall was started from "Free up space" (their launch links): marked in the list until they're gone.</summary>
    private readonly HashSet<string> _uninstallStarted = new(StringComparer.OrdinalIgnoreCase);

    private MenuPage? _freeSpacePage;

    /// <summary>
    /// The biggest games of the Space on disk card in the game menu's panel: choosing one uninstalls it, the same as the game
    /// menu's Uninstall (its launcher or uninstaller takes over). The list stays open and marks the game "uninstall started";
    /// once the automatic scan finds it gone, it leaves the list (<see cref="RefreshFreeSpace"/>).
    /// </summary>
    private void OpenFreeSpace()
    {
        if (_diskCard is not { Lines.Count: > 0 }) return;

        DiskCard.BringIntoView();   // (the card A acts on comes on screen, behind the list)
        _menuPages.Clear();
        _menuPages.Push(_freeSpacePage = new MenuPage("Free up space: uninstall a game", FreeSpaceRows));

        ShowOverlay(Overlay.GameMenu);
        RenderGameMenu();
    }

    private List<FilterRow> FreeSpaceRows() =>
        (_diskCard?.Lines ?? []).Select(line =>
        {
            var started = _uninstallStarted.Contains(line.Game.LaunchTarget);
            return new FilterRow(started ? line.Text + "  ·  uninstall started" : line.Text, Activate: () =>
            {
                // (chosen again, e.g. after cancelling the launcher's confirmation: its uninstaller opens again)
                _uninstallStarted.Add(line.Game.LaunchTarget);
                _gameActions.Uninstall(line.Game);
                RenderGameMenu();
            });
        }).ToList();

    /// <summary>The library changed while "Free up space" is open: its list follows (a game uninstalled leaves it).</summary>
    private void RefreshFreeSpace()
    {
        if (_overlay != Overlay.GameMenu || _menuPages.Count == 0 || !ReferenceEquals(_menuPages.Peek(), _freeSpacePage)) return;

        if (_diskCard is { Lines.Count: > 0 })
            RenderGameMenu();
        else
            CloseGameMenu();   // nothing left to uninstall
    }

    private void DiskPanel_Click(object sender, MouseButtonEventArgs e) => OpenFreeSpace();

    /// <summary>
    /// The Space on disk card is ringed in the accent color, like a selected tile, while A opens "Free up space": on the Stats
    /// tab, with games on the card, and the stick not on the icons at the top.
    /// </summary>
    private void UpdateDiskRing() =>
        DiskCard.BorderBrush = _tab == StatsTab && !_topFocus && _diskCard is { Lines.Count: > 0 } ? Accent : System.Windows.Media.Brushes.Transparent;

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
