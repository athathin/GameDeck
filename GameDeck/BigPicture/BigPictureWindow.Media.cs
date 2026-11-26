using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GameDeck.Services;
using static GameDeck.Views.BigPictureLook;

namespace GameDeck.Views;

public partial class BigPictureWindow
{
    // ---------- the Media tab: every game's screenshots in a grid ----------

    private const int MediaTab = 4;
    private const int MediaPageSize = 60;     // pictures added at a time: more come as the selection nears the end
    private const double MediaMinCellWidth = 300;   // the pictures are at least this wide; they stretch to fill the row
    private const double MediaCellGap = 18;

    private List<ScreenshotService.MediaShot> _mediaAll = [];   // every picture found
    private List<ScreenshotService.MediaShot> _media = [];      // the ones shown (the game filter)
    private string? _mediaFilter;                               // the game shown (ScreenshotService.GroupKey); null = all
    private readonly List<(Border Picture, FrameworkElement Cell)> _mediaCells = [];
    private int _mediaIndex;
    private int _mediaVersion;      // raised on every new fill: an older fill that is still loading stops
    private bool _mediaLoaded;      // the tab has been filled (a library reload doesn't read the folders again)
    private bool _mediaPaging;      // a page of thumbnails is being made

    /// <summary>The tabs that show a row of games (not Stats, not Media).</summary>
    private bool IsGamesTab => _tab < StatsTab;

    /// <summary>Fills the tab, once each time it is opened: the folders are read and the thumbnails made in the background.</summary>
    private async void BuildMedia()
    {
        if (_mediaLoaded) return;

        _mediaLoaded = true;
        var version = ++_mediaVersion;
        _mediaCells.Clear();
        _mediaRinged = null;
        MediaWrap.Children.Clear();
        _mediaIndex = 0;
        MediaHeader.Text = "Looking for screenshots…";

        var games = _library.ToList();
        List<ScreenshotService.MediaShot> found;
        try
        {
            found = await Task.Run(() => ScreenshotService.FindAll(games));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            found = [];   // (an async void must not throw: the tab then shows none)
        }

        if (version != _mediaVersion) return;   // the tab was left meanwhile

        _mediaAll = found;
        await ShowFilteredMediaAsync();
    }

    /// <summary>Shows the pictures of the chosen game (or all), from the start, without reading the folders again.</summary>
    private async Task ShowFilteredMediaAsync()
    {
        var version = ++_mediaVersion;
        _mediaCells.Clear();
        _mediaRinged = null;
        MediaWrap.Children.Clear();
        _mediaIndex = 0;
        MediaScroll.ScrollToTop();

        var groups = ScreenshotService.GroupsOf(_mediaAll);
        var group = groups.FirstOrDefault(g => string.Equals(g.Key, _mediaFilter, StringComparison.OrdinalIgnoreCase));
        if (group is null) _mediaFilter = null;   // (that game has no pictures any more)

        _media = ScreenshotService.Filter(_mediaAll, _mediaFilter);
        MediaHeader.Text = _mediaAll.Count == 0
            ? "No screenshots yet. Game Bar captures, Home + " + Labels().RightTrigger +
              " on the controller, and the pictures added on a game's page show up here."
            : (group is null ? $"{_media.Count} screenshots of {groups.Count} games, newest first" : $"{group.Name}: {_media.Count} screenshots") +
              $"   ·   {Labels().Select}: choose a game";

        await AddMediaPageAsync(version);
    }

    private void MediaHeader_Click(object sender, MouseButtonEventArgs e)
    {
        if (_overlay == Overlay.None)
            OpenMediaFilter();
    }

    /// <summary>The game filter (the View button): every game that has pictures, with how many, in the game menu's panel.</summary>
    private void OpenMediaFilter()
    {
        if (_mediaAll.Count == 0) return;

        var groups = ScreenshotService.GroupsOf(_mediaAll);

        List<FilterRow> Rows()
        {
            var rows = new List<FilterRow> { Choice(null, $"All games  ({_mediaAll.Count})") };
            rows.AddRange(groups.Select(g => Choice(g.Key, $"{g.Name}  ({g.Count})")));
            return rows;
        }

        FilterRow Choice(string? key, string text) => new(text, IsOn: () => _mediaFilter == key, IsRadio: true, Activate: () =>
        {
            _mediaFilter = key;
            CloseGameMenu();
            _ = ShowFilteredMediaAsync();
        });

        _menuPages.Clear();
        _menuPages.Push(new MenuPage("Show the screenshots of", Rows));
        ShowOverlay(Overlay.GameMenu);
        RenderGameMenu();
    }

    /// <summary>Empties the tab and lets go of its thumbnails (another tab was chosen).</summary>
    private void ClearMedia()
    {
        if (!_mediaLoaded) return;

        _mediaLoaded = false;
        _mediaVersion++;
        _mediaAll = [];
        _media = [];
        _mediaCells.Clear();
        _mediaRinged = null;
        MediaWrap.Children.Clear();
    }

    /// <summary>Adds the next pictures (their thumbnails are made in the background first).</summary>
    private async Task AddMediaPageAsync(int version)
    {
        if (_mediaPaging || _mediaCells.Count >= _media.Count) return;

        _mediaPaging = true;
        try
        {
            var first = _mediaCells.Count;
            var page = _media.Skip(first).Take(MediaPageSize).ToList();
            var thumbnails = await Task.Run(() => page.Select(s => ScreenshotService.Thumbnail(s.File, 400)).ToList());
            if (version != _mediaVersion) return;

            for (var i = 0; i < page.Count; i++)
                AddMediaCell(page[i], thumbnails[i], first + i);

            UpdateMediaSelection(scroll: first == 0);
        }
        finally
        {
            _mediaPaging = false;
        }
    }

    private void AddMediaCell(ScreenshotService.MediaShot shot, ImageSource? thumbnail, int index)
    {
        var width = MediaCellWidth;
        var picture = new Border
        {
            Width = width, Height = width * 9 / 16, CornerRadius = new CornerRadius(9),
            BorderThickness = new Thickness(3), BorderBrush = Brushes.Transparent, Background = KeyBackground,
            Child = new RoundedClipBorder
            {
                CornerRadius = new CornerRadius(6),
                Child = thumbnail is null
                    ? new TextBlock
                    {
                        Text = "Can't be shown", Foreground = Muted, FontSize = 15,
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                    }
                    : new Image { Source = thumbnail, Stretch = Stretch.UniformToFill }
            }
        };

        var cell = new StackPanel
        {
            Width = width, Margin = new Thickness(0, 0, MediaCellGap, 22), Cursor = Cursors.Hand,
            Children =
            {
                picture,
                new TextBlock
                {
                    Text = shot.Game?.DisplayName ?? "Other", Foreground = ThemeService.TextPrimary, FontSize = 17,
                    FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 8, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis
                },
                new TextBlock
                {
                    Text = shot.TakenUtc.ToLocalTime().ToString("d MMM yyyy, HH:mm"), Foreground = TextGrey, FontSize = 14,
                    Margin = new Thickness(4, 2, 0, 0)
                }
            }
        };

        // the mouse: pointing selects (like the stick), a click opens the picture full screen
        cell.MouseEnter += (_, _) =>
        {
            if (PointerIsChoosing && _overlay == Overlay.None && _mediaIndex != index)
                SelectMedia(index, scroll: false);
        };
        cell.MouseLeftButtonUp += (_, _) =>
        {
            if (_overlay != Overlay.None) return;

            SelectMedia(index, scroll: false);
            OpenMediaShot();
        };

        _mediaCells.Add((picture, cell));
        MediaWrap.Children.Add(cell);
    }

    /// <summary>How many pictures fit in one row of the grid (each at least <see cref="MediaMinCellWidth"/> wide).</summary>
    private int MediaColumns => Math.Max(1, (int)((MediaWrap.ActualWidth + 1) / (MediaMinCellWidth + MediaCellGap)));

    /// <summary>The width that makes the pictures of a row fill it, with the gap after each.</summary>
    private double MediaCellWidth => MediaWrap.ActualWidth > 0
        ? Math.Floor((MediaWrap.ActualWidth - 1) / MediaColumns - MediaCellGap)
        : MediaMinCellWidth;

    /// <summary>The screen's width changed (another scale, another monitor): the pictures stretch again to fill the rows.</summary>
    private void MediaWrap_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged) return;

        var width = MediaCellWidth;
        foreach (var (picture, cell) in _mediaCells)
        {
            picture.Width = width;
            picture.Height = width * 9 / 16;
            cell.Width = width;
        }
    }

    private void SelectMedia(int index, bool scroll = true)
    {
        if (_mediaCells.Count == 0) return;

        _mediaIndex = Math.Clamp(index, 0, _mediaCells.Count - 1);
        UpdateMediaSelection(scroll);

        // near the end of what is shown: the next pictures are added in time
        if (_mediaIndex >= _mediaCells.Count - MediaColumns * 2)
            _ = AddMediaPageAsync(_mediaVersion);
    }

    private Border? _mediaRinged;   // the picture that has the ring now (only it and the new one change on a move)

    /// <summary>The selected picture gets the accent ring (none while an icon at the top is the selected one).</summary>
    private void UpdateMediaSelection(bool scroll)
    {
        var wanted = _mediaIndex < _mediaCells.Count && !_topFocus ? _mediaCells[_mediaIndex].Picture : null;
        if (!ReferenceEquals(wanted, _mediaRinged))
        {
            if (_mediaRinged is not null) _mediaRinged.BorderBrush = Brushes.Transparent;
            if (wanted is not null) wanted.BorderBrush = Accent;
            _mediaRinged = wanted;
        }

        if (scroll && _mediaIndex < _mediaCells.Count)
            _mediaCells[_mediaIndex].Cell.BringIntoView();
    }

    /// <summary>The buttons on the Media tab. Returns false for the ones it leaves alone (tabs, Home, Back...).</summary>
    private bool HandleMedia(PadButton button)
    {
        var columns = MediaColumns;

        switch (button)
        {
            case PadButton.Left: SelectMedia(_mediaIndex - 1); return true;
            case PadButton.Right: SelectMedia(_mediaIndex + 1); return true;
            case PadButton.Down: SelectMedia(Math.Min(_mediaIndex + columns, _mediaCells.Count - 1)); return true;

            case PadButton.Up:
                if (_mediaIndex < columns || _mediaCells.Count == 0)
                {
                    MediaScroll.ScrollToTop();
                    FocusTopBar(true);   // the top row: up goes on to the icons, like on the other tabs
                    UpdateMediaSelection(scroll: false);
                }
                else
                {
                    SelectMedia(_mediaIndex - columns);
                }
                return true;

            case PadButton.Confirm: OpenMediaShot(); return true;
            case PadButton.X: ShowMediaInFolder(); return true;
            case PadButton.Y: OpenMediaGame(); return true;

            case PadButton.Select: OpenMediaFilter(); return true;   // the game filter

            // nothing to search or open a game menu for here
            case PadButton.Start or PadButton.RightTrigger: return true;

            default: return false;
        }
    }

    private ScreenshotService.MediaShot? SelectedShot =>
        _mediaIndex >= 0 && _mediaIndex < _mediaCells.Count && _mediaIndex < _media.Count ? _media[_mediaIndex] : null;

    /// <summary>The picture full screen; left / right go on through every picture of the tab.</summary>
    private void OpenMediaShot()
    {
        if (SelectedShot is null) return;

        OpenShots(_media.Select(s => s.File).ToList(), _mediaIndex, fromMedia: true);
    }

    private void ShowMediaInFolder()
    {
        if (SelectedShot is { } shot && !ScreenshotService.ShowInFolder(shot.File))
            MediaHeader.Text = "Couldn't open that picture's folder.";
    }

    /// <summary>The page of the game the picture belongs to (Back comes back here).</summary>
    private void OpenMediaGame()
    {
        if (SelectedShot is { Game: { } game })
            OpenDetails(game);
    }

    /// <summary>The full-screen picture was closed: the tab shows the picture it ended on.</summary>
    private void ReturnToMedia(int shotIndex)
    {
        CloseOverlay();

        if (shotIndex < _mediaCells.Count)
            SelectMedia(shotIndex);
    }
}
