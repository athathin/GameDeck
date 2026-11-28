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
        SavePending();   // (a rating changed with left / right a moment ago)
        _menuPages.Clear();
        CloseOverlay();
    }

    /// <summary>The game menu's pages from the main list to the one open now, to come back to after typing or browsing.</summary>
    private MenuPage[] MenuSnapshot() => _menuPages.Reverse().ToArray();

    /// <summary>
    /// Opens the game menu again at the pages of <paramref name="pages"/> (each on the row it was on), drawn anew.
    /// <paramref name="reload"/>: something the row of games shows changed (a name: the order), so it is drawn again too.
    /// </summary>
    private void ReturnToMenu(MenuPage[] pages, bool reload = false)
    {
        _menuPages.Clear();
        foreach (var page in pages)
            _menuPages.Push(page);

        if (_menuPages.Count == 0)
        {
            CloseOverlay();
            return;
        }

        if (reload)
            ReloadLibrary();
        ShowOverlay(Overlay.GameMenu);
        RenderGameMenu();
    }

    private FilterRow? SelectedMenuRow =>
        _menuPages.Count > 0 && _menuPages.Peek().Index is var i && i >= 0 && i < _menuRows.Count ? _menuRows[i] : null;

    private void FillGameMenuHints()
    {
        var l = Labels();
        var hints = new List<(string, string)>();
        if (SelectedMenuRow?.Adjust is not null)
            hints.Add((ShownKind == ControllerKind.None ? "← →" : "◀ ▶", "Change"));
        hints.Add((l.Confirm, "Choose"));
        hints.Add((l.Back, _menuPages.Count > 1 ? "Back" : "Close"));
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
        }, (index, step) =>
        {
            page.Index = index;
            AdjustMenuRow(step);
        });
    }

    /// <summary>Left / right (or a click on its ‹ ›) on a row that has a value to change, like the rating.</summary>
    private void AdjustMenuRow(int step)
    {
        if (SelectedMenuRow?.Adjust is not { } adjust) return;

        adjust(step);      // (shown at once; saved once the presses stop, see SaveSoon)
        RenderGameMenu();
    }

    // ---- saving a value changed press by press (the rating with left / right) once the presses stop ----
    // Saving writes the data file and redraws both libraries: done on every press, holding left / right would lag.

    private System.Windows.Threading.DispatcherTimer? _saveTimer;
    private Action? _pendingSave;

    /// <summary>Saves with <paramref name="save"/> 0.6 s after the last change (a newer change replaces it), or when the menu closes.</summary>
    private void SaveSoon(Action save)
    {
        _pendingSave = save;

        _saveTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Tick -= SaveTimer_Tick;
        _saveTimer.Tick += SaveTimer_Tick;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveTimer_Tick(object? sender, EventArgs e) => SavePending();

    /// <summary>Saves what is waiting (if anything) and shows the library as it is now.</summary>
    private void SavePending()
    {
        _saveTimer?.Stop();
        if (_pendingSave is not { } save) return;

        _pendingSave = null;
        save();

        if (!_closing)
            ReloadLibrary();
    }

    private void HandleGameMenu(PadButton button)
    {
        var page = _menuPages.Peek();

        switch (button)
        {
            case PadButton.Up: MoveMenu(page, -1); break;
            case PadButton.Down: MoveMenu(page, 1); break;
            case PadButton.Left: AdjustMenuRow(-1); break;
            case PadButton.Right: AdjustMenuRow(1); break;
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

    // The same menu as the desktop's right-click menu (MainWindow.GameMenu.cs), with the desktop's Edit and Configure windows
    // as pages of this menu, so that everything can be done with the controller: text is typed with the on-screen keyboard,
    // a cover is chosen with the controller's file browser.

    /// <summary>
    ///   Play · Edit ›
    ///   Manage installation › (Configure ›, Create desktop shortcut, Uninstall, Remove from library)
    ///   Add to favorites · Categories › · Rate › · Status › · Hide game
    /// </summary>
    private List<FilterRow> MainMenuRows(Game game)
    {
        var actions = _gameActions;

        return
        [
            new(ReferenceEquals(game, NowPlaying) ? "▶   Resume" : "▶   Play", Activate: () =>
            {
                CloseGameMenu();
                Launch(game);
            }),
            new("Edit  ›", Activate: () => OpenSubmenu($"Editing \"{game.DisplayName}\"", () => EditRows(game))),
            new("Manage installation  ›", Activate: () => OpenSubmenu("Manage installation", () => ManageRows(game))),
            new(game.FavoriteMenuText, Activate: () =>
            {
                ToggleFavorite();   // (in the Favorites tab an un-favorited game leaves the row)
                CloseGameMenu();
            }),
            new($"Categories  ({game.Tags.Count})  ›", Activate: () => OpenSubmenu("Categories", () => CategoryRows(game))),
            new(game.HasRating ? $"Rate:  {game.RatingText}  ›" : "Rate  ›", Activate: () => OpenSubmenu("Rate", () => RateRows(game))),
            new(game.HasStatus ? $"Status:  {game.StatusText}  ›" : "Status  ›", Activate: () => OpenSubmenu("Status", () => StatusRows(game))),
            new(game.IsHidden ? "Unhide game" : "Hide game", Activate: () => GameAction(() => actions.ToggleHidden(game)))
        ];
    }

    private List<FilterRow> ManageRows(Game game)
    {
        var actions = _gameActions;

        var rows = new List<FilterRow>
        {
            new("Configure  ›", Activate: () => OpenSubmenu($"Configuring \"{game.DisplayName}\"", () => ConfigureRows(game))),
            new("Create desktop shortcut", Activate: () => GameAction(() => actions.CreateShortcut(game, this))),
            new("Uninstall", Activate: () => GameAction(() => actions.Uninstall(game)))
        };

        if (game.Source == ManualSource)   // scanned games leave the library by themselves once uninstalled
            rows.Add(new("Remove from library", Activate: () => GameAction(() => actions.RemoveFromLibrary(game, this))));

        return rows;
    }

    /// <summary>What has nothing in it yet shows this on its row.</summary>
    private static string OrNone(string? text, int max = 40)
    {
        if (string.IsNullOrWhiteSpace(text)) return "none";

        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length > max ? line[..max] + "…" : line;
    }

    /// <summary>"Edit": the desktop's Edit window (Info and Media) as rows: each text is typed with the on-screen keyboard.</summary>
    private List<FilterRow> EditRows(Game game)
    {
        var actions = _gameActions;

        var rows = new List<FilterRow>
        {
            new("INFO", IsHeader: true),
            new($"Title:  {game.DisplayName}", Activate: () =>
                TypeFromMenu("The name shown for this game (empty: its own name)", game.DisplayName, 100,
                             text => actions.SetName(game, text))),
            new($"Rating:  {(game.HasRating ? game.RatingText : "not rated")}",
                Activate: () => OpenSubmenu("Rate", () => RateRows(game)),
                Adjust: step =>
                {
                    game.Rating = Math.Clamp(game.Rating + step, 0, 5);   // shown at once...
                    SaveSoon(() => actions.SetRating(game, game.Rating));  // ...saved once the presses stop
                }),
            new($"Categories  ({game.Tags.Count})  ›", Activate: () => OpenSubmenu("Categories", () => CategoryRows(game))),
            new($"Where I left off:  {OrNone(game.LeftOff)}", Activate: () =>
            {
                var pages = MenuSnapshot();
                OpenNote(game, leftOff: true, after: () => ReturnToMenu(pages));
            }),
            new($"Notes:  {OrNone(game.Notes)}", Activate: () =>
            {
                var pages = MenuSnapshot();
                OpenNote(game, after: () => ReturnToMenu(pages));
            }),
            new("Hide from my games list", IsOn: () => game.IsHidden,
                Activate: () => GameAction(() => actions.ToggleHidden(game), closeMenu: false))
        };

        // from the Steam store or GOG: shown, not changed
        var facts = new List<FilterRow>();
        if (game.Genres.Count > 0) facts.Add(new($"Genres:  {string.Join(", ", game.Genres)}"));
        if (game.Info is { } info)
        {
            if (info.Developers.Count > 0) facts.Add(new($"Developer:  {string.Join(", ", info.Developers)}"));
            if (info.Publishers.Count > 0) facts.Add(new($"Publisher:  {string.Join(", ", info.Publishers)}"));
            if (info.Released is { } released) facts.Add(new($"Release date:  {released}"));
        }
        rows.InsertRange(2, facts);

        rows.Add(new("MEDIA", IsHeader: true));
        rows.Add(new(game.HasCustomCover ? "Cover:  your own picture. Choose another…" : "Cover:  found by GameDeck. Choose a picture…",
            Activate: () => ChooseCover(game)));
        if (game.HasCustomCover)
            rows.Add(new("Use the automatic cover again", Activate: () => GameAction(() => _ = actions.RemoveCover(game), closeMenu: false)));

        return rows;
    }

    /// <summary>The controller's file browser, choosing a picture as the game's cover; then back to the Edit page.</summary>
    private void ChooseCover(Game game)
    {
        var pages = MenuSnapshot();
        CloseGameMenu();

        OpenProgramBrowse(new ProgramChoice($"Cover for {game.DisplayName}: choose a picture",
            file => _gameActions.SetCover(game, file) ? null : "That file couldn't be read as a picture. Try a .jpg, .png or .bmp.",
            Done: () => ReturnToMenu(pages), Pictures: true));
    }

    /// <summary>"Configure": how the game is started (the desktop's Configure window as rows).</summary>
    private List<FilterRow> ConfigureRows(Game game)
    {
        var actions = _gameActions;
        var rows = new List<FilterRow> { new("LAUNCH", IsHeader: true) };

        if (game.SupportsArguments)
            rows.Add(new($"Launch arguments:  {OrNone(game.LaunchArguments)}", Activate: () =>
                TypeFromMenu(game.IsDirectExe ? "Arguments passed to the game when it starts" : "Arguments Steam passes to the game",
                             game.LaunchArguments ?? "", 300, text => actions.SetLaunchOptions(game, text, game.RunAsAdmin), capital: false)));
        else
            rows.Add(new($"{game.Source} games are started by their own launcher, which doesn't accept extra arguments."));

        if (game.IsDirectExe)
            rows.Add(new("Run as administrator", IsOn: () => game.RunAsAdmin,
                Activate: () => GameAction(() => actions.SetLaunchOptions(game, game.LaunchArguments, !game.RunAsAdmin), closeMenu: false)));

        rows.Add(new($"Launch routine:  {(game.Profile is { IsEmpty: false } ? "on" : "off")}  ›",
            Activate: () => OpenSubmenu("Launch routine", () => RoutineRows(game))));

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
        _freeSpaceAll = false;      // (the biggest few first, like the card)
        _menuPages.Clear();
        _menuPages.Push(_freeSpacePage = new MenuPage("Free up space: uninstall a game", FreeSpaceRows));

        ShowOverlay(Overlay.GameMenu);
        RenderGameMenu();
    }

    /// <summary>"Free up space" lists every game with a known size ("Show all N games" was chosen), not just the biggest few.</summary>
    private bool _freeSpaceAll;

    private List<FilterRow> FreeSpaceRows()
    {
        if (_diskCard is not { } card) return [];

        var rows = (_freeSpaceAll ? card.All : card.Lines).Select(line =>
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

        // the last row: the rest of the games, in this same list (the selection stays where it is: on the first of them)
        if (card.HasMore && !_freeSpaceAll)
            rows.Add(new FilterRow($"Show all {card.All.Count} games  ›", Activate: () =>
            {
                _freeSpaceAll = true;
                RenderGameMenu();
            }));

        return rows;
    }

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

    /// <summary>New category… first, then every category (ticked when the game is in it), then Manage (like the desktop menu).</summary>
    private List<FilterRow> CategoryRows(Game game)
    {
        var actions = _gameActions;
        var categories = actions.AllCategories();

        var rows = new List<FilterRow>
        {
            new("New category…", Activate: () =>
                TypeFromMenu($"Name the new category. {game.DisplayName} will be added to it", "", 30, text =>
                {
                    if (!string.IsNullOrWhiteSpace(text))
                        actions.AddToCategory(game, text.Trim());
                }))
        };

        rows.AddRange(categories.Select(c => new FilterRow(c, IsOn: () => InCategory(game, c),
            Activate: () => GameAction(() => actions.ToggleCategory(game, c), closeMenu: false))));

        if (categories.Count > 0)   // nothing to manage until a category exists
            rows.Add(new("Manage categories  ›", Activate: () => OpenSubmenu("Manage categories", ManageCategoryRows)));

        return rows;
    }

    /// <summary>Every category with how many games are in it; choosing one offers Rename and Delete.</summary>
    private List<FilterRow> ManageCategoryRows()
    {
        var categories = _gameActions.AllCategories();
        if (categories.Count == 0)
            return [new FilterRow("No categories left.")];

        return categories.Select(c =>
        {
            var count = _library.Count(g => InCategory(g, c));
            return new FilterRow($"{c}   ({count} {(count == 1 ? "game" : "games")})  ›",
                Activate: () => OpenSubmenu(c, () => CategoryActionRows(c)));
        }).ToList();
    }

    private List<FilterRow> CategoryActionRows(string category) =>
    [
        new("Rename…", Activate: () =>
        {
            _menuPages.Pop();   // (back on the list afterwards: this page's name would be the old one)
            TypeFromMenu($"A new name for {category}", category, 30, text =>
            {
                if (!string.IsNullOrWhiteSpace(text) && text.Trim() != category)
                    _gameActions.RenameCategory(category, text.Trim());
            });
        }),
        new($"Delete {category}  (the games stay, only the category goes)", Activate: () =>
        {
            _gameActions.DeleteCategory(category);
            ReloadLibrary();
            _menuPages.Pop();
            RenderGameMenu();
        })
    ];

    /// <summary>Five stars down to one, then No rating (like the desktop's Rate menu). Choosing one goes back a page.</summary>
    private List<FilterRow> RateRows(Game game)
    {
        void Choose(int stars)
        {
            _gameActions.SetRating(game, stars);
            ReloadLibrary();
            _menuPages.Pop();   // (the page before shows the new rating)
            RenderGameMenu();
        }

        var rows = Enumerable.Range(1, 5).Reverse()
            .Select(stars => new FilterRow(new string('★', stars), IsOn: () => game.Rating == stars, IsRadio: true,
                Activate: () => Choose(stars)))
            .ToList();

        rows.Add(new("No rating", IsOn: () => game.Rating == 0, IsRadio: true, Activate: () => Choose(0)));
        return rows;
    }

    private List<FilterRow> StatusRows(Game game) =>
        Enum.GetValues<GameStatus>()
            .Select(s => new FilterRow(s.Label(), IsOn: () => game.Status == s, IsRadio: true,
                Activate: () => GameAction(() => _gameActions.SetStatus(game, s))))
            .ToList();
}
