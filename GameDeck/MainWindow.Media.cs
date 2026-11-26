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

    private List<ScreenshotService.MediaShot> _media = [];
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

        _media = found;
        StatusText.Text = found.Count == 1 ? "1 screenshot" : $"{found.Count} screenshots";
        MediaHeader.Text = found.Count == 0
            ? "No screenshots yet. Game Bar captures (Win + Alt + PrtScn), the controller's Home + RT, and the pictures added on a " +
              "game's page show up here."
            : "Every game's screenshots, newest first. Click one to open it; right-click for more.";

        await AddMediaPageAsync(version);
    }

    /// <summary>Empties the tab and lets go of its thumbnails (it was left, or GameDeck went to the tray).</summary>
    private void ClearMedia()
    {
        if (!_mediaLoaded) return;

        _mediaLoaded = false;
        _mediaVersion++;
        _media = [];
        _mediaShown = 0;
        MediaWrap.Children.Clear();
        MediaMoreButton.Visibility = Visibility.Collapsed;
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

    /// <summary>One picture: the thumbnail, then the game's name and the date. Click: open it. Right-click: open, folder, game page.</summary>
    private FrameworkElement MediaCell(ScreenshotService.MediaShot shot, ImageSource? thumbnail)
    {
        var picture = new RoundedClipBorder
        {
            Width = 220, Height = 124, CornerRadius = new CornerRadius(6), Background = (Brush)FindResource("PanelBg"),
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
            Width = 220, Margin = new Thickness(0, 0, 14, 18), Cursor = System.Windows.Input.Cursors.Hand,
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
