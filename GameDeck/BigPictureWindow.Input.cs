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
    // ---------- what the buttons do ----------

    private void Handle(PadButton button)
    {
        if (Mouse.OverrideCursor is null)
            HideCursor();   // a button or a key was used: no pointer needed

        NavSounds.Play(button switch
        {
            PadButton.Confirm or PadButton.Start or PadButton.Y or PadButton.RightTrigger => NavSound.Select,
            PadButton.Back => NavSound.Back,
            _ => NavSound.Move
        });

        // an open menu / keyboard / panel takes all the input
        if (_overlay != Overlay.None)
        {
            HandleOverlay(button);
            return;
        }

        // the "Continue playing" row takes the directions while it is selected
        if (_stripFocus && HandleStrip(button))
            return;

        // the icons at the top (search, filters, settings, power) are selected with the stick: they take the directions
        if (_topFocus && HandleTopBar(button))
            return;

        switch (button)
        {
            case PadButton.Left: Move(-1); break;
            case PadButton.Right: Move(1); break;
            case PadButton.Up: UpOrDown(-1); break;      // up to the icons at the top; scrolls the Stats tab
            case PadButton.Down: UpOrDown(1); break;
            case PadButton.Confirm: OpenDetails(); break;   // like desktop mode: the game's page first, Play from there
            case PadButton.Back: AskToExit(); break;   // not instantly: it could be a stray press
            case PadButton.X: if (_tab == StatsTab) RefreshStats(); else ToggleFavorite(); break;
            case PadButton.Y: OpenSearch(); break;
            case PadButton.Select: OpenFilters(); break;
            case PadButton.Start: OpenGameMenu(); break;   // the right-click menu of the desktop library
            case PadButton.Home: OpenPower(); break;
            case PadButton.RightTrigger: PickRandom(); break;
            case PadButton.LeftShoulder: SwitchTab(-1); break;
            case PadButton.RightShoulder: SwitchTab(1); break;
        }
    }

    private void HandleOverlay(PadButton button)
    {
        switch (_overlay)
        {
            case Overlay.Search: HandleSearch(button); break;
            case Overlay.Filters: HandleFilters(button); break;
            case Overlay.Power: HandlePower(button); break;
            case Overlay.Details: HandleDetails(button); break;
            case Overlay.Settings: HandleSettings(button); break;
            case Overlay.GameMenu: HandleGameMenu(button); break;
            case Overlay.Browse: HandleBrowse(button); break;
            case Overlay.Shots: HandleShots(button); break;
        }
    }

    private void HandleDetails(PadButton button)
    {
        switch (button)
        {
            case PadButton.Confirm: _ = PressPlayThenLaunch(); break;
            case PadButton.Y when DetailsView.Shots.Count > 0: OpenShots(DetailsView.Shots, 0); break;
            case PadButton.Back: CloseOverlay(); break;
        }
    }

    private bool _pressingPlay;

    /// <summary>Shows the Play button being pressed for a moment, then starts the game.</summary>
    private async Task PressPlayThenLaunch()
    {
        if (_pressingPlay || _closing) return;

        _pressingPlay = true;
        DetailsView.PressPlay();
        await Task.Delay(180);
        _pressingPlay = false;

        if (_overlay == Overlay.Details)
            LaunchSelected();
    }

    private void OpenDetails()
    {
        if (Carousel.SelectedItem is not Game game) return;

        var ranked = _library.Where(g => !g.IsHidden && g.PlaytimeSeconds > 0).OrderByDescending(g => g.PlaytimeSeconds).ToList();
        DetailsView.Show(game, ranked.IndexOf(game) + 1, ranked.Count, _sessions.Recent(8, game));
        DetailsView.SelectPlay();   // the controller has no pointer: show Play as the selected button

        FillDetailsHints();
        ShowOverlay(Overlay.Details);
    }

    private void FillDetailsHints()
    {
        var l = Labels();
        if (DetailsView.Shots.Count > 0)
            FillHints(DetailsHints, (l.Confirm, "Play"), (l.Y, "Screenshots"), (l.Back, "Back"));
        else
            FillHints(DetailsHints, (l.Confirm, "Play"), (l.Back, "Back"));
    }

    /// <summary>The input device changed while a panel was open: its hints show the other device's buttons now.</summary>
    private void RefreshOpenPanelHints()
    {
        switch (_overlay)
        {
            case Overlay.Settings: FillSettingsHints(); break;
            case Overlay.GameMenu when _menuPages.Count > 0: FillGameMenuHints(); break;
            case Overlay.Details: FillDetailsHints(); break;
            case Overlay.Browse: FillBrowseHints(); break;
            case Overlay.Shots: FillShotHints(); break;
        }
    }

    private void DetailsView_BackRequested(object? sender, EventArgs e) => CloseOverlay();

    private void DetailsView_PlayRequested(object? sender, Game game) => _ = PressPlayThenLaunch();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        SetPadInUse(false);

        // choosing a new quick launch shortcut: every key press is part of it
        if (_capturingShortcut && _overlay == Overlay.Settings)
        {
            CaptureShortcut(e);
            e.Handled = true;
            return;
        }

        // typing in the search box: letters arrive as text (Window_PreviewTextInput); only the keys below are handled here
        PadButton? pad = e.Key switch
        {
            Key.Left => PadButton.Left,
            Key.Right => PadButton.Right,
            Key.Up => PadButton.Up,
            Key.Down => PadButton.Down,
            Key.Enter => PadButton.Confirm,
            Key.Escape => PadButton.Back,
            Key.Space when _overlay != Overlay.Search => PadButton.Confirm,
            Key.S when _overlay == Overlay.Details => PadButton.Y,      // the game's screenshots
            Key.Back when _overlay == Overlay.Search => PadButton.X,    // backspace
            Key.Tab => PadButton.Select,
            _ => null
        };

        // the letter shortcuts only exist while no menu is open (they would clash with typing)
        if (pad is null && _overlay == Overlay.None)
        {
            pad = e.Key switch
            {
                Key.F => PadButton.X,
                Key.S or Key.OemQuestion => PadButton.Y,
                Key.P => PadButton.Home,
                Key.M or Key.Apps => PadButton.Start,
                Key.R => PadButton.RightTrigger,
                Key.PageUp => PadButton.LeftShoulder,
                Key.PageDown => PadButton.RightShoulder,
                _ => null
            };
        }

        if (pad is null) return;

        Handle(pad.Value);
        e.Handled = true;
    }

    /// <summary>A real keyboard can type into the search box too.</summary>
    private void Window_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (_overlay != Overlay.Search) return;

        foreach (var c in e.Text)
        {
            if (char.IsLetterOrDigit(c) || " '-.,:".Contains(c))
                TypeText(_noteGame is not null ? c.ToString() : char.ToLowerInvariant(c).ToString());   // (a note keeps the capitals typed)
        }

        e.Handled = true;
    }

    private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        SetPadInUse(false);

        if (_overlay == Overlay.None)
        {
            if (_tab == StatsTab)
                ScrollStats(e.Delta > 0 ? -1 : 1);
            else
                Move(e.Delta > 0 ? -1 : 1);

            e.Handled = true;
        }

        // (with a panel open the wheel is left alone: the list under the pointer scrolls by itself)
    }

    private void SearchButton_Click(object sender, MouseButtonEventArgs e) => OpenSearch();

    private void FilterButton_Click(object sender, MouseButtonEventArgs e) => OpenFilters();

    private void PowerButton_Click(object sender, MouseButtonEventArgs e) => OpenPower();

    private void SettingsButton_Click(object sender, MouseButtonEventArgs e) => OpenSettings();

    // ---------- the mouse on the row of games ----------

    private Game? _pressedTile;
    private bool _hoverSelecting;   // the mouse chose the tile: the row must not glide to center it under the pointer

    private Game? TileUnder(object source) =>
        source is DependencyObject element &&
        ItemsControl.ContainerFromElement(Carousel, element) is ListBoxItem { DataContext: Game game }
            ? game
            : null;

    /// <summary>The tile under the mouse becomes the selected one, as with the stick (the row only follows when you move the mouse).</summary>
    private void Carousel_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        // only a real move of a visible pointer chooses a tile (not tiles gliding under the hidden one)
        if (!PointerIsChoosing) return;

        if (_overlay != Overlay.None || TileUnder(e.OriginalSource) is not { } game ||
            ReferenceEquals(game, Carousel.SelectedItem))
            return;

        if (_topFocus)
            FocusTopBar(false);
        if (_stripFocus)
            FocusStrip(false);

        _hoverSelecting = true;
        try { Carousel.SelectedItem = game; }
        finally { _hoverSelecting = false; }
    }

    // A tile can't take the keyboard focus, and a list only selects an item it can focus: so a click is handled here.
    // Like in the desktop library, a click on a tile opens the game's page.
    private void Carousel_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        _pressedTile = TileUnder(e.OriginalSource);

    private void Carousel_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var tile = TileUnder(e.OriginalSource);
        var pressed = _pressedTile;
        _pressedTile = null;

        // (pressed and released on the same tile; not on empty space, not behind an open menu)
        if (_overlay != Overlay.None || tile is null || !ReferenceEquals(tile, pressed))
            return;

        Carousel.SelectedItem = tile;
        OpenDetails();
    }

    private void Move(int step)
    {
        if (_items.Count == 0) return;

        Carousel.SelectedIndex = Math.Clamp(Math.Max(Carousel.SelectedIndex, 0) + step, 0, _items.Count - 1);
    }

    private void UpOrDown(int direction)
    {
        if (_tab == StatsTab)
        {
            // scroll the stats; at the very top, up goes on to the icons
            if (direction < 0 && StatsScroll.VerticalOffset <= 0)
                FocusTopBar(true);
            else
                ScrollStats(direction);
        }
        else if (direction < 0)
        {
            if (StripAvailable) FocusStrip(true);   // the row of last played games sits above the tiles
            else FocusTopBar(true);
        }
    }

    // ---------- the icons at the top, reachable with the stick ----------

    private bool _topFocus;
    private int _topIndex;

    private Border[]? _topIcons;

    private Border[] TopIcons => _topIcons ??= [SearchButton, FilterButton, SettingsButton, PowerButton];

    private void FocusTopBar(bool on)
    {
        _topFocus = on;
        if (on)
        {
            _topIndex = Math.Clamp(_topIndex, 0, TopIcons.Length - 1);
            _stripFocus = false;
            ContinueList.SelectedIndex = -1;
            HideTileRing(false);
        }

        UpdateTopBar();
        BuildMainHints();
    }

    /// <summary>Selected icon: white with a dark symbol, like the selected key or row elsewhere in this screen.</summary>
    private void UpdateTopBar()
    {
        var icons = TopIcons;
        for (var i = 0; i < icons.Length; i++)
        {
            var selected = _topFocus && i == _topIndex;
            var glyph = (TextBlock)icons[i].Child;

            if (selected)
            {
                icons[i].Background = ThemeService.SelectBg;
                glyph.Foreground = Dark;
            }
            else
            {
                icons[i].ClearValue(BackgroundProperty);   // back to the style's colors (including the mouse hover)
                glyph.Foreground = ThemeService.TextPrimary;
            }
        }
    }

    /// <summary>Returns true when the button was used up here.</summary>
    private bool HandleTopBar(PadButton button)
    {
        switch (button)
        {
            case PadButton.Left: _topIndex = Math.Max(0, _topIndex - 1); UpdateTopBar(); return true;
            case PadButton.Right: _topIndex = Math.Min(TopIcons.Length - 1, _topIndex + 1); UpdateTopBar(); return true;
            case PadButton.Up: return true;
            case PadButton.Down when StripAvailable: FocusStrip(true); return true;   // on to the row of last played games
            case PadButton.Down or PadButton.Back: FocusTopBar(false); return true;   // back to the games

            case PadButton.Confirm:
                switch (_topIndex)
                {
                    case 0: OpenSearch(); break;
                    case 1: OpenFilters(); break;
                    case 2: OpenSettings(); break;
                    default: OpenPower(); break;
                }
                return true;

            default: return false;   // tabs, Home, favorite... keep working from up here
        }
    }

    private void ScrollStats(int direction) =>
        StatsScroll.ScrollToVerticalOffset(Math.Max(0, StatsScroll.VerticalOffset + direction * 240));

    private void LaunchSelected()
    {
        if (_closing || Carousel.SelectedItem is not Game game) return;

        LaunchedGame = true;
        CloseScreen();
        _launch(game);   // after closing, so the game isn't started behind a full-screen window
    }

    private void CloseScreen()
    {
        if (_closing) return;

        _closing = true;
        Close();
    }

    /// <summary>Closes this screen, then does something in the main window (minimize, exit...).</summary>
    private void CloseThen(Action after, bool keepMainHidden = true)
    {
        KeepMainHidden = keepMainHidden;
        CloseScreen();
        after();
    }

    private void ToggleFavorite()
    {
        if (Carousel.SelectedItem is not Game game) return;

        var index = Carousel.SelectedIndex;
        _toggleFavorite(game);

        // in the Favorites tab an un-favorited game leaves the list
        if (_tab == 1 && !game.IsFavorite)
        {
            _items.Remove(game);
            EmptyText.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            Carousel.SelectedIndex = _items.Count == 0 ? -1 : Math.Min(index, _items.Count - 1);
        }
    }

    private void PickRandom()
    {
        if (_items.Count == 0) return;

        var index = Random.Shared.Next(_items.Count);
        if (_items.Count > 1 && index == Carousel.SelectedIndex)
            index = (index + 1) % _items.Count;

        Carousel.SelectedIndex = index;
    }
}
