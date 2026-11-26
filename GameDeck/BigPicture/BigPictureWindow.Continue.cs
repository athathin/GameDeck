using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GameDeck.Models;
using GameDeck.Services;

namespace GameDeck.Views;

public partial class BigPictureWindow
{
    // ---------- "Continue playing": the games played last, as a small row above the tiles ----------

    private const int ContinueCount = 4;
    private const double ContinueRoom = 750;   // the middle of the screen must be this tall (the row sits above the tiles, which stay put)

    private readonly ObservableCollection<Game> _continueItems = [];
    private bool _stripFocus;
    private int _stripIndex;

    private void InitContinue()
    {
        ContinueList.ItemsSource = _continueItems;
        MiddleArea.SizeChanged += (_, _) => UpdateContinue();
    }

    /// <summary>Fills the row (only on the plain "All games" list, and only when the screen is tall enough) and shows or hides it.</summary>
    private void UpdateContinue()
    {
        var wanted = _tab == 0 && !HasActiveFilters && _query.Length == 0 && MiddleArea.ActualHeight >= ContinueRoom;

        var games = wanted
            ? _library.Where(g => !g.IsHidden && g.LastPlayed is not null)
                      .OrderByDescending(g => g.LastPlayed).Take(ContinueCount).ToList()
            : [];

        if (!games.SequenceEqual(_continueItems))
        {
            _continueItems.Clear();
            foreach (var game in games)
                _continueItems.Add(game);
        }

        var show = _continueItems.Count > 0;
        ContinuePanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

        if (!show && _stripFocus)
            FocusStrip(false);
    }

    private bool StripAvailable => ContinuePanel.Visibility == Visibility.Visible && _continueItems.Count > 0;

    /// <summary>Puts the stick's focus on the row (on) or back on the tiles (off).</summary>
    private void FocusStrip(bool on)
    {
        _stripFocus = on && StripAvailable;
        if (_stripFocus)
        {
            _topFocus = false;
            UpdateTopBar();
            _stripIndex = Math.Clamp(_stripIndex, 0, _continueItems.Count - 1);
        }

        ContinueList.SelectedIndex = _stripFocus ? _stripIndex : -1;
        HideTileRing(_stripFocus);   // (the selected tile has no ring while the row is the selected one)
        BuildMainHints();
    }

    private FrameworkElement? _hiddenRing;

    /// <summary>
    /// While the row is selected, the selected tile must not show its ring too (two rings look like two selections).
    /// The ring is hidden by a value set on the ring itself, which wins over the template's own "selected" rule; clearing it brings the rule back.
    /// </summary>
    private void HideTileRing(bool hide)
    {
        _hiddenRing?.ClearValue(VisibilityProperty);
        _hiddenRing = null;

        if (hide && Carousel.ItemContainerGenerator.ContainerFromItem(Carousel.SelectedItem) is ListBoxItem tile &&
            tile.Template.FindName("Ring", tile) is FrameworkElement ring)
        {
            ring.Visibility = Visibility.Collapsed;
            _hiddenRing = ring;
        }
    }

    /// <summary>Returns true when the button was used up here.</summary>
    private bool HandleStrip(PadButton button)
    {
        if (_continueItems.Count == 0) return false;
        _stripIndex = Math.Clamp(_stripIndex, 0, _continueItems.Count - 1);   // (the row may have got shorter since)

        switch (button)
        {
            case PadButton.Left: _stripIndex = Math.Max(0, _stripIndex - 1); ContinueList.SelectedIndex = _stripIndex; return true;
            case PadButton.Right: _stripIndex = Math.Min(_continueItems.Count - 1, _stripIndex + 1); ContinueList.SelectedIndex = _stripIndex; return true;
            case PadButton.Up: FocusTopBar(true); return true;                       // on to the icons at the top
            case PadButton.Down or PadButton.Back: FocusStrip(false); return true;   // back to the tiles
            case PadButton.Confirm: OpenContinue(_continueItems[_stripIndex]); return true;
            case PadButton.X or PadButton.Start: return true;                        // (those belong to the selected tile, not to this row)
            default: return false;                                                   // tabs, search, Home... keep working from here
        }
    }

    /// <summary>Opens the game's page, and selects the game among the tiles too (so Back returns to it).</summary>
    private void OpenContinue(Game game)
    {
        FocusStrip(false);

        if (_items.Contains(game))
            Carousel.SelectedItem = game;

        OpenDetails();
    }

    // a click on a game of the row
    private void ContinueItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_overlay == Overlay.None && ((FrameworkElement)sender).DataContext is Game game)
            OpenContinue(game);
    }

    // the row's covers: loaded like the tiles' (see Tile_Loaded), and kept while the row shows them
    private void ContinueItem_Loaded(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not Game game) return;

        _touchedCovers.TryAdd(game, game.CoverWanted);
        game.CoverWanted = true;

        if (game.Cover is null)
            _ = CoverService.LoadCoverAsync(game);
    }
}
