using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GameDeck.Services;
using GameDeck.Views;

namespace GameDeck;

public partial class MainWindow
{
    // ---------- the Media tab: every game's screenshots, newest first ----------

    private const int MediaPageSize = 60;   // pictures added at a time ("Show more"): a few MB of thumbnails each time

    private List<ScreenshotService.MediaShot> _mediaAll = [];   // every picture found
    private List<ScreenshotService.MediaShot> _media = [];      // the ones shown (the game filter)
    private string? _mediaFilter;                               // the game shown (ScreenshotService.GroupKey); null = all
    private int _mediaShown;      // how many of them are on screen
    private bool _mediaLoaded;    // the tab has been filled (switching back to it doesn't read the folders again)
    private int _mediaVersion;    // raised on every new fill: an older fill that is still loading stops

    /// <summary>Fills the Media tab (once each time it is opened): the folders are read and the thumbnails made in the background.</summary>
    private async void ShowMedia()
    {
        if (_mediaLoaded) return;

        _mediaLoaded = true;
        var version = ++_mediaVersion;

        MediaWrap.Children.Clear();
        _mediaShown = 0;
        MediaMoreButton.Visibility = Visibility.Collapsed;
        MediaGameButton.Visibility = Visibility.Collapsed;
        MediaHeader.Text = "Looking for screenshots…";

        var games = _library.Games.ToList();
        List<ScreenshotService.MediaShot> found;
        try
        {
            found = await Task.Run(() => ScreenshotService.FindAll(games));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            found = [];   // (an async void must not throw: the tab then shows none)
        }

        if (version != _mediaVersion) return;   // the tab was left (or filled again) meanwhile

        _mediaAll = found;
        await ShowFilteredMediaAsync();
    }

    /// <summary>Shows the pictures of the chosen game (or all), from the start, without reading the folders again.</summary>
    private async Task ShowFilteredMediaAsync()
    {
        var version = ++_mediaVersion;
        MediaWrap.Children.Clear();
        _mediaShown = 0;
        MediaMoreButton.Visibility = Visibility.Collapsed;
        MediaPanel.ScrollToTop();

        var group = ScreenshotService.GroupsOf(_mediaAll)
            .FirstOrDefault(g => string.Equals(g.Key, _mediaFilter, StringComparison.OrdinalIgnoreCase));
        if (group is null) _mediaFilter = null;   // (that game has no pictures any more)

        _media = ScreenshotService.Filter(_mediaAll, _mediaFilter);
        StatusText.Text = _media.Count == 1 ? "1 screenshot" : $"{_media.Count} screenshots";

        MediaGameButton.Content = (group is null ? "Showing: all games" : $"Showing: {group.Name}") + "  ▾";
        MediaGameButton.Visibility = _mediaAll.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        MediaHeader.Text = _mediaAll.Count == 0
            ? "No screenshots yet. Game Bar captures (Win + Alt + PrtScn), the controller's Home + RT, and the pictures added on a " +
              "game's page show up here."
            : "Newest first. Click one to open it; right-click for more.";

        await AddMediaPageAsync(version);
    }

    /// <summary>The game filter: a menu of every game that has pictures, with how many.</summary>
    private void MediaGameButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = MediaGameButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };

        void Add(string? key, string text) => menu.Items.Add(MenuEntry(text, _mediaFilter == key, () =>
        {
            _mediaFilter = key;
            _ = ShowFilteredMediaAsync();
        }));

        Add(null, $"All games  ({_mediaAll.Count})");
        menu.Items.Add(new Separator());
        foreach (var group in ScreenshotService.GroupsOf(_mediaAll))
            Add(group.Key, $"{group.Name}  ({group.Count})");

        menu.IsOpen = true;
    }

    /// <summary>Empties the tab and lets go of its thumbnails (it was left, or GameDeck went to the tray).</summary>
    private void ClearMedia()
    {
        if (!_mediaLoaded) return;

        _mediaLoaded = false;
        _mediaVersion++;
        _mediaAll = [];
        _media = [];
        _mediaShown = 0;
        MediaWrap.Children.Clear();
        MediaMoreButton.Visibility = Visibility.Collapsed;
        MediaGameButton.Visibility = Visibility.Collapsed;
    }

    private async void MediaMoreButton_Click(object sender, RoutedEventArgs e) => await AddMediaPageAsync(_mediaVersion);

    /// <summary>Adds the next pictures (their thumbnails are made in the background first).</summary>
    private async Task AddMediaPageAsync(int version)
    {
        var first = _mediaShown;
        var page = _media.Skip(first).Take(MediaPageSize).ToList();
        _mediaShown += page.Count;
        MediaMoreButton.Visibility = Visibility.Collapsed;

        var thumbnails = await Task.Run(() => page.Select(s => ScreenshotService.Thumbnail(s.File, 320)).ToList());
        if (version != _mediaVersion) return;

        for (var i = 0; i < page.Count; i++)
            MediaWrap.Children.Add(MediaCell(page[i], thumbnails[i]));

        var left = _media.Count - _mediaShown;
        if (left > 0)
        {
            MediaMoreButton.Content = $"Show more  ({left} more)";
            MediaMoreButton.Visibility = Visibility.Visible;
        }
    }

    private const double MediaMinCellWidth = 220;   // the pictures are at least this wide; they stretch to fill the row
    private const double MediaCellGap = 14;

    /// <summary>The width that makes the pictures of a row fill it (with the gap after each), each at least the minimum.</summary>
    private double MediaCellWidth
    {
        get
        {
            var width = MediaWrap.ActualWidth;
            if (width <= 0) return MediaMinCellWidth;

            var columns = Math.Max(1, (int)((width + 1) / (MediaMinCellWidth + MediaCellGap)));
            return Math.Floor((width - 1) / columns - MediaCellGap);
        }
    }

    /// <summary>The window's width changed: the pictures stretch again to fill the rows.</summary>
    private void MediaWrap_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged) return;

        var width = MediaCellWidth;
        foreach (var cell in MediaWrap.Children.OfType<StackPanel>())
        {
            cell.Width = width;
            if (cell.Children.Count > 0 && cell.Children[0] is FrameworkElement picture)
            {
                picture.Width = width;
                picture.Height = width * 9 / 16;
            }
        }
    }

    /// <summary>One picture: the thumbnail, then the game's name and the date. Click: open it. Right-click: open, folder, game page.</summary>
    private FrameworkElement MediaCell(ScreenshotService.MediaShot shot, ImageSource? thumbnail)
    {
        var width = MediaCellWidth;
        var picture = new RoundedClipBorder
        {
            Width = width, Height = width * 9 / 16, CornerRadius = new CornerRadius(6), Background = (Brush)FindResource("PanelBg"),
            Child = thumbnail is null
                ? new TextBlock
                {
                    Text = "Can't be shown", Foreground = ThemeService.TextMuted, FontSize = 12,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                }
                : new Image { Source = thumbnail, Stretch = Stretch.UniformToFill }
        };

        var cell = new StackPanel
        {
            Width = width, Margin = new Thickness(0, 0, MediaCellGap, 18), Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = Path.GetFileName(shot.File),
            Children =
            {
                picture,
                new TextBlock
                {
                    Text = shot.Game?.DisplayName ?? "Other", Foreground = ThemeService.TextPrimary, FontSize = 13,
                    FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 7, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis
                },
                new TextBlock
                {
                    Text = shot.TakenUtc.ToLocalTime().ToString("d MMM yyyy, HH:mm"), Foreground = ThemeService.TextMuted, FontSize = 12,
                    Margin = new Thickness(2, 2, 0, 0)
                }
            }
        };

        cell.MouseLeftButtonUp += (_, _) => OpenPicture(shot.File);
        cell.ContextMenu = MediaMenu(shot);
        return cell;
    }

    private ContextMenu MediaMenu(ScreenshotService.MediaShot shot)
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuEntry("Open", false, () => OpenPicture(shot.File)));
        menu.Items.Add(MenuEntry("Show in folder", false, () =>
        {
            if (!ScreenshotService.ShowInFolder(shot.File))
                StatusText.Text = "Couldn't open that picture's folder.";
        }));

        if (shot.Game is { } game)
            menu.Items.Add(MenuEntry($"Go to {game.DisplayName}", false, () => OpenDetails(game)));

        return menu;
    }

    /// <summary>Opens the picture in the viewer Windows uses for it.</summary>
    private void OpenPicture(string file)
    {
        try
        {
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException or InvalidOperationException)
        {
            StatusText.Text = "Couldn't open that picture.";
        }
    }
}
