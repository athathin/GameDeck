using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameDeck.Models;
using GameDeck.Services;
using Microsoft.Win32;

namespace GameDeck.Views;

/// <summary>
/// "Edit…": the game's own details in two tabs. Info: the shown name, rating, categories, where I left off, notes and whether it
/// is hidden. Media: its cover. (Launch options are in "Configure…", under Manage installation.)
/// ShowDialog() returns true when OK was pressed; the values are then in the properties below, nothing is saved before that.
/// </summary>
public partial class GameSettingsDialog : Window
{
    private readonly Game _game;

    public string GameName => NameBox.Text;
    public int Rating { get; private set; }
    public string Notes => NotesBox.Text;
    public string LeftOff => LeftOffBox.Text.Trim();
    public bool Hidden => HiddenCheckBox.IsChecked == true;

    /// <summary>An image chosen on the Media tab, to become the cover (null = none chosen).</summary>
    public string? NewCoverFile { get; private set; }

    /// <summary>True when "Use automatic cover" was pressed: the user's own cover is to be removed.</summary>
    public bool RemoveCover { get; private set; }

    /// <summary>The categories typed in, trimmed, without empty or repeated entries.</summary>
    public List<string> Tags => TagsBox.Text
        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Select(t => t.Length > 30 ? t[..30] : t)
        .Distinct(StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    public GameSettingsDialog(Game game)
    {
        InitializeComponent();
        TitleBarStyler.Apply(this);

        _game = game;
        Title = $"Edit {game.DisplayName}";
        HeaderTitle.Text = $"Editing \"{game.DisplayName}\"";
        HeaderSource.Text = SourceName(game);
        HeaderImage.Source = CurrentCover(game);

        NameBox.Text = game.DisplayName;
        TagsBox.Text = string.Join(", ", game.Tags);
        NotesBox.Text = game.Notes ?? "";
        LeftOffBox.Text = game.LeftOff ?? "";
        HiddenCheckBox.IsChecked = game.IsHidden;
        Rating = game.Rating;
        BuildStars();

        // genres come from the Steam store and can't be changed: shown only when there are some
        if (game.Genres.Count > 0)
            GenresText.Text = string.Join(", ", game.Genres);
        else
            GenresLabel.Visibility = GenresText.Visibility = Visibility.Collapsed;

        ShowCover();

        NameBox.Focus();
        NameBox.SelectAll();
    }

    /// <summary>The launcher's name under the title ("Steam"); games added by hand say so.</summary>
    internal static string SourceName(Game game) => game.Source == "Manual" ? "Added by you" : game.Source;

    /// <summary>The cover shown now, or the one already on this PC when the tile hasn't loaded it (null when there is none).</summary>
    internal static ImageSource? CurrentCover(Game game) =>
        game.Cover ?? (CoverService.FindCachedCover(game) is { } file ? LoadImage(file) : null);

    /// <summary>Reads a picture without keeping the file open (so it can still be replaced or deleted). Null when it can't be read.</summary>
    private static ImageSource? LoadImage(string file)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.DecodePixelWidth = 320;
            image.UriSource = new Uri(file);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (InfoPage is null || MediaPage is null) return;   // (the first tab is checked while the window is still being built)

        var media = MediaTab.IsChecked == true;
        InfoPage.Visibility = media ? Visibility.Collapsed : Visibility.Visible;
        MediaPage.Visibility = media ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- rating ----------

    /// <summary>Five stars to click: the one clicked becomes the rating, and clicking the current rating again clears it.</summary>
    private void BuildStars()
    {
        StarsPanel.Children.Clear();

        for (var i = 1; i <= 5; i++)
        {
            var star = i;
            var filled = star <= Rating;

            var text = new TextBlock
            {
                Text = filled ? "★" : "☆",
                FontSize = 24,
                Margin = new Thickness(0, 0, 4, 0),
                Cursor = Cursors.Hand,
                ToolTip = star == Rating ? "Click again to remove the rating" : $"{star} star{(star == 1 ? "" : "s")}",
                Foreground = filled ? StarBrush : ThemeService.TextMuted
            };

            text.MouseLeftButtonUp += (_, _) =>
            {
                Rating = Rating == star ? 0 : star;
                BuildStars();
            };

            StarsPanel.Children.Add(text);
        }
    }

    /// <summary>The gold of a filled star (also used by the right-click Rate menu).</summary>
    internal static readonly SolidColorBrush StarBrush = MakeStarBrush();

    private static SolidColorBrush MakeStarBrush()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0x57));
        brush.Freeze();
        return brush;
    }

    // ---------- cover (Media tab) ----------

    /// <summary>Shows the cover as it will be after OK: the image just chosen, the automatic one, or the current one.</summary>
    private void ShowCover()
    {
        ImageSource? image;
        string hint;

        if (NewCoverFile is { } file)
        {
            image = LoadImage(file);
            hint = "Your image. It becomes the cover when you press OK.";
        }
        else if (RemoveCover)
        {
            image = null;
            hint = "GameDeck finds the cover by itself again once you press OK.";
        }
        else
        {
            image = CurrentCover(_game);
            hint = _game.HasCustomCover ? "Your own image." : "Found by GameDeck. Choose an image to use your own instead.";
        }

        CoverImage.Source = image;
        CoverPlaceholder.Text = image is null ? (RemoveCover ? "Automatic cover" : _game.DisplayName) : "";
        CoverHint.Text = hint;

        // "automatic" only means something while an own cover is (or is about to be) used
        ResetCoverButton.Visibility = NewCoverFile is not null || (_game.HasCustomCover && !RemoveCover)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ChooseCover()
    {
        var dialog = new OpenFileDialog
        {
            Title = $"Choose cover art for {_game.DisplayName}",
            Filter = "Images (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp"
        };

        if (dialog.ShowDialog(this) != true) return;

        if (LoadImage(dialog.FileName) is null)
        {
            MessageDialog.Show(this, "That file couldn't be read as an image. Try a .jpg, .png or .bmp.", "Couldn't use that image");
            return;
        }

        NewCoverFile = dialog.FileName;
        RemoveCover = false;
        ShowCover();
    }

    private void ChooseCover_Click(object sender, RoutedEventArgs e) => ChooseCover();

    private void CoverPreview_Click(object sender, MouseButtonEventArgs e) => ChooseCover();

    private void ResetCover_Click(object sender, RoutedEventArgs e)
    {
        NewCoverFile = null;
        RemoveCover = _game.HasCustomCover;   // (an image chosen just now is simply dropped)
        ShowCover();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            DialogResult = false;
    }
}
