using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using GameDeck.Models;
using GameDeck.Services;

namespace GameDeck.Views;

/// <summary>
/// The page that opens when you click a game: cover, name, a Play button and the game's stats.
/// The main window starts the game (PlayRequested) and closes the page (BackRequested).
/// </summary>
public partial class GameDetailsView : UserControl
{
    public event EventHandler? BackRequested;
    public event EventHandler<Game>? PlayRequested;

    /// <summary>A screenshot was clicked: all of the game's screenshots, and the index of the one.</summary>
    public event EventHandler<(IReadOnlyList<string> Files, int Index)>? ScreenshotRequested;

    /// <summary>The screenshots shown now (the controller screen opens them full size).</summary>
    public IReadOnlyList<string> Shots { get; private set; } = [];

    /// <summary>The screenshots are found a moment after the page opens (the pictures are read in the background).</summary>
    public event EventHandler? ShotsChanged;

    public GameDetailsView()
    {
        InitializeComponent();

        // Until a game is opened, don't inherit the main window as DataContext: the bindings below
        // would look for Game properties on MainWindow and report "property not found".
        DataContext = null;
    }

    /// <summary>Shows one game. The playtime and last-played text keep updating by themselves (they bind to the game).</summary>
    public void Show(Game game, int rank, int playedCount, IReadOnlyList<SessionRow> sessions)
    {
        DataContext = game;

        SessionsList.ItemsSource = sessions;
        SessionsPanel.Visibility = sessions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        RankText.Text = game.PlaytimeSeconds > 0 ? $"#{rank} of {playedCount}" : "-";
        RatingStarsText.Text = game.RatingText;
        RatingStarsText.Visibility = game.HasRating ? Visibility.Visible : Visibility.Collapsed;

        NotesText.Text = game.Notes ?? "";
        NotesPanel.Visibility = game.HasNotes ? Visibility.Visible : Visibility.Collapsed;

        Shots = [];   // (the last game's pictures must not stay while this game's are being found)
        ShotsWrap.Children.Clear();
        ShotsHint.Visibility = Visibility.Visible;
        LoadShots(game);

        CategoriesText.Text = game.Tags.Count > 0 ? "Categories: " + string.Join(", ", game.Tags) : "";
        CategoriesText.Visibility = game.Tags.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- screenshots ----

    private async void LoadShots(Game game)
    {
        List<string> files;
        List<BitmapImage?> thumbnails;

        try
        {
            files = await Task.Run(() => ScreenshotService.Find(game));
            thumbnails = await Task.Run(() => files.Select(Thumbnail).ToList());   // (frozen pictures can be made on another thread)
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return;   // (an async void must not throw: with no screenshots found the page simply shows none)
        }

        if (!ReferenceEquals(DataContext, game)) return;   // another game's page was opened meanwhile

        Shots = files;
        ShotsWrap.Children.Clear();

        for (var i = 0; i < Shots.Count; i++)
        {
            var thumbnail = thumbnails[i];
            if (thumbnail is null) continue;

            var index = i;
            var border = new RoundedClipBorder
            {
                Width = 176, Height = 99, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 10, 10),
                Cursor = System.Windows.Input.Cursors.Hand, Background = (Brush)FindResource("PanelBg"),
                ToolTip = Path.GetFileName(Shots[i]),
                Child = new Image { Source = thumbnail, Stretch = Stretch.UniformToFill }
            };
            border.MouseLeftButtonUp += (_, _) => ScreenshotRequested?.Invoke(this, (Shots, index));
            border.ContextMenu = ShotMenu(Shots, index);
            ShotsWrap.Children.Add(border);
        }

        ShotsHint.Visibility = ShotsWrap.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShotsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A screenshot's right-click menu: open it, or show it in its folder.</summary>
    private ContextMenu ShotMenu(IReadOnlyList<string> files, int index)
    {
        var open = new MenuItem { Header = new TextBlock { Text = "Open" } };
        open.Click += (_, _) => ScreenshotRequested?.Invoke(this, (files, index));

        var folder = new MenuItem { Header = new TextBlock { Text = "Show in folder" } };
        folder.Click += (_, _) => ScreenshotService.ShowInFolder(files[index]);

        return new ContextMenu { Items = { open, folder } };
    }

    /// <summary>A small copy of the picture (a 4K screenshot is not decoded in full for a thumbnail); null when it can't be read.</summary>
    private static BitmapImage? Thumbnail(string file)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(file);
            image.DecodePixelWidth = 352;
            image.CacheOption = BitmapCacheOption.OnLoad;   // read now, so the file isn't kept open
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or FileFormatException)
        {
            return null;
        }
    }

    private void AddShots_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not Game game) return;

        var dialog = new OpenFileDialog
        {
            Title = "Select screenshots for " + game.DisplayName,
            Filter = "Pictures (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp",
            Multiselect = true
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) == true && ScreenshotService.Add(game, dialog.FileNames) > 0)
            LoadShots(game);
    }

    // ---- controller look (Big Picture): nobody can hover or focus the button, so it shows it is selected / pressed itself ----

    private static readonly Brush PlaySelected = FrozenBrush(0x9B, 0x66, 0xFF);
    private static readonly Brush PlayPressed = FrozenBrush(0x62, 0x24, 0xC9);

    private static Brush FrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    /// <summary>Draws the Play button as the selected one: a white glow and a slightly bigger, brighter button.</summary>
    public void SelectPlay()
    {
        Unselect(AddShotsButton);
        Select(PlayButton);
        PlayButton.Background = PlaySelected;
    }

    /// <summary>"Resume" on the button while this game is running (Big Picture), otherwise "Play".</summary>
    public void SetPlayLabel(bool resume) => PlayButton.Content = resume ? "▶  Resume" : "▶  Play";

    /// <summary>Draws "Add screenshots" as the selected button (the controller moved down to it) and scrolls it into sight.</summary>
    public void SelectAddShots()
    {
        Unselect(PlayButton);
        Select(AddShotsButton);
        AddShotsButton.Background = PlaySelected;
        AddShotsButton.Foreground = Brushes.White;
        AddShotsButton.BringIntoView();
    }

    /// <summary>Reloads the game's screenshots (after some were added from Big Picture).</summary>
    public void ReloadShots()
    {
        if (DataContext is Game game)
            LoadShots(game);
    }

    private static void Select(Button button)
    {
        button.RenderTransformOrigin = new Point(0.5, 0.5);
        button.RenderTransform = new ScaleTransform(1.06, 1.06);
        button.Effect = new DropShadowEffect { Color = ThemeService.Current == AppTheme.Dark ? Colors.White : Color.FromRgb(0x80, 0x37, 0xFE), BlurRadius = 22, ShadowDepth = 0, Opacity = 0.9 };
    }

    private static void Unselect(Button button)
    {
        button.ClearValue(RenderTransformProperty);
        button.ClearValue(BackgroundProperty);
        button.ClearValue(ForegroundProperty);
        button.ClearValue(EffectProperty);
    }

    /// <summary>Draws the Play button as pressed: darker and pushed in.</summary>
    public void PressPlay()
    {
        PlayButton.RenderTransform = new ScaleTransform(0.95, 0.95);
        PlayButton.Background = PlayPressed;
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);

    private void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is Game game)
            PlayRequested?.Invoke(this, game);
    }
}
