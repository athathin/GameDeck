using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GameDeck.Models;
using GameDeck.Services;

namespace GameDeck.Views;

public partial class BigPictureWindow
{
    // ---------- the game menu (the Menu button: the right-click menu of the desktop library) ----------

    /// <summary>One page of the game menu: the main list, or a submenu (Categories, Status).</summary>
    private sealed class MenuPage(string title, Func<List<FilterRow>> build)
    {
        public string Title { get; } = title;
        public Func<List<FilterRow>> Build { get; } = build;
        public int Index { get; set; }
    }

    private readonly Stack<MenuPage> _menuPages = new();
    private List<FilterRow> _menuRows = [];
    private Game? _menuGame;

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

        _menuGame = game;
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

        _ = RenderRows(GameMenuList, _menuRows, page.Index, index =>
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

    // ---------- power menu ----------

    // true while the "exit Big Picture?" question (from the Back button) is showing instead of the Power menu
    private bool _exitQuestion;

    private void AskToExit()
    {
        _exitQuestion = true;
        _powerConfirming = new PowerEntry("Exit Big Picture Mode", CloseScreen, "Exit Big Picture mode?", "Exit");
        _powerIndex = 0;
        ShowOverlay(Overlay.Power);
        RenderPower();
    }

    private void OpenPower()
    {
        _exitQuestion = false;
        _powerConfirming = null;
        _powerIndex = 0;
        ShowOverlay(Overlay.Power);
        RenderPower();
    }

    /// <summary>
    /// The menu, in groups like Steam's: the running game (Resume / Close) when there is one, the controller's shortcuts, the PC,
    /// GameDeck, and Cancel. Dangerous ones ask again first.
    /// </summary>
    private List<List<PowerEntry>> PowerMenu()
    {
        var groups = PowerMenuGroups();

        if (NowPlayingEntries() is { Count: > 0 } game)
            groups.Insert(0, game);

        return groups;
    }

    private List<List<PowerEntry>> PowerMenuGroups() =>
    [
        [
            new PowerEntry("Show Controller Shortcuts", OpenShortcuts)
        ],
        [
            new PowerEntry("Suspend System", () => { CloseOverlay(); SystemPower.Suspend(); }),
            new PowerEntry("Turn Off System", SystemPower.TurnOff, "Turn off this PC?", "Turn off now"),
            new PowerEntry("Restart System", SystemPower.Restart, "Restart this PC?", "Restart now")
        ],
        [
            new PowerEntry("Minimize GameDeck", () => CloseThen(_minimizeApp)),
            new PowerEntry("Exit Big Picture Mode", CloseScreen),
            new PowerEntry("Exit GameDeck", () => CloseThen(_exitApp), "Exit GameDeck?", "Exit GameDeck")
        ],
        [
            new PowerEntry("Cancel", CloseOverlay)
        ]
    ];

    private void RenderPower()
    {
        PowerList.Children.Clear();

        if (_powerConfirming is { } entry)
        {
            PowerTitle.Text = entry.ConfirmTitle ?? "Are you sure?";
            _powerGroups =
            [
                [(entry.ConfirmButton ?? "Yes", () => { CloseOverlay(); entry.Run(); })],
                [("Cancel", () =>
                {
                    if (_exitQuestion) { CloseOverlay(); return; }   // asked from Back: there is no menu to go back to

                    _powerConfirming = null;
                    _powerIndex = 0;
                    RenderPower();
                })]
            ];
        }
        else
        {
            PowerTitle.Text = "Power";
            _powerGroups = PowerMenu()
                .Select(group => group.Select(e => (e.Text, Run: e.ConfirmTitle is null
                    ? e.Run
                    : () => { _powerConfirming = e; _powerIndex = 0; RenderPower(); })).ToList())
                .ToList();
        }

        var index = 0;
        for (var g = 0; g < _powerGroups.Count; g++)
        {
            if (g > 0)
                PowerList.Children.Add(new Border { Height = 2, Background = Divider });

            foreach (var (text, _) in _powerGroups[g])
            {
                var selected = index == _powerIndex;
                var itemIndex = index;

                var cell = new Border
                {
                    Background = selected ? ThemeService.SelectBg : Brushes.Transparent,
                    Padding = new Thickness(24, 15, 24, 15),
                    Cursor = Cursors.Hand,
                    Child = new TextBlock { Text = text, FontSize = 19, Foreground = selected ? Dark : TextGrey }
                };

                cell.MouseEnter += (_, _) =>
                {
                    if (!PointerIsChoosing || _powerIndex == itemIndex) return;

                    _powerIndex = itemIndex;   // the mouse selects what it points at, like the stick does
                    RenderPower();
                };

                cell.MouseLeftButtonUp += (_, _) =>
                {
                    _powerIndex = itemIndex;
                    ActivatePower();
                };

                PowerList.Children.Add(cell);
                index++;
            }
        }
    }

    private void HandlePower(PadButton button)
    {
        var count = _powerGroups.Sum(g => g.Count);

        switch (button)
        {
            case PadButton.Up:
                _powerIndex = Math.Max(0, _powerIndex - 1);
                RenderPower();
                break;

            case PadButton.Down:
                _powerIndex = Math.Min(count - 1, _powerIndex + 1);
                RenderPower();
                break;

            case PadButton.Confirm:
                ActivatePower();
                break;

            case PadButton.Back or PadButton.Home:   // Home opens the menu, so it closes it again too
                if (_powerConfirming is not null && !_exitQuestion)
                {
                    _powerConfirming = null;   // back from "are you sure?" to the menu
                    _powerIndex = 0;
                    RenderPower();
                }
                else
                {
                    CloseOverlay();
                }
                break;
        }
    }

    private void ActivatePower() => _powerGroups.SelectMany(g => g).ElementAt(_powerIndex).Run();
}
