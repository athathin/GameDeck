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
    public void Show(Game game, int rank, int playedCount, IReadOnlyList<SessionRow> sessions, GameHistory? history,
                     IReadOnlyList<Game> addOns)
    {
        AddOnsText.Text = addOns.Count == 0 ? ""
            : "DLC and soundtracks: " + string.Join(", ", addOns.Select(a => a.HasSize ? $"{a.DisplayName} ({a.SizeText})" : a.DisplayName));
        AddOnsText.Visibility = addOns.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        DataContext = game;

        SessionsList.ItemsSource = sessions;
        SessionsPanel.Visibility = sessions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // the play history (twenty weeks of little squares, each with its own tooltip) is drawn just after the page shows, so
        // the page comes up first
        HistoryGrid.Children.Clear();
        HistoryPanel.Visibility = history is null ? Visibility.Collapsed : Visibility.Visible;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            if (ReferenceEquals(DataContext, game))
                ShowHistory(history);
        });

        RankText.Text = game.PlaytimeSeconds > 0 ? $"#{rank} of {playedCount}" : "-";
        ShowRating(game);

        ShowLeftOff(game);

        NotesText.Text = game.Notes ?? "";
        NotesPanel.Visibility = game.HasNotes ? Visibility.Visible : Visibility.Collapsed;

        Shots = [];   // (the last game's pictures must not stay while this game's are being found)
        ShotsWrap.Children.Clear();
        ShotsHint.Visibility = Visibility.Visible;
        LoadShots(game);

        CategoriesText.Text = game.Tags.Count > 0 ? "Categories: " + string.Join(", ", game.Tags) : "";
        CategoriesText.Visibility = game.Tags.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        GenresText.Text = game.Genres.Count > 0 ? "Genres: " + string.Join(", ", game.Genres) : "";
        GenresText.Visibility = game.Genres.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        ShowAbout(game);
        ShowBackdrop(game);

        UpdateLayoutMode();     // (a game without history has nothing for the right-hand column)
        PageScroll.ScrollToTop();
    }

    // ---- the background art at the top ----

    /// <summary>Opacity of the background once shown: enough to give the page the game's colors, little enough to keep the text easy to read.</summary>
    private const double BackdropOpacity = 0.32;

    /// <summary>The game's wide background art behind the top of the page, fading in once it is read (or downloaded).</summary>
    private async void ShowBackdrop(Game game)
    {
        BackdropFrame.BeginAnimation(OpacityProperty, null);
        BackdropFrame.Opacity = 0;
        BackdropImage.Source = null;

        var image = await BackgroundService.LoadAsync(game);
        if (image is null || !ReferenceEquals(DataContext, game)) return;   // (none, or another game's page meanwhile)

        BackdropImage.Source = image;
        BackdropFrame.BeginAnimation(OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation(0, BackdropOpacity, TimeSpan.FromMilliseconds(350)));
    }

    // ---- about the game: from the Steam store or GOG ----

    /// <summary>
    /// The store's description, then small cards with the developer, publisher, release date and Metacritic score. A game
    /// neither store has says so; nothing is shown while its details may still come.
    /// </summary>
    private void ShowAbout(Game game)
    {
        AboutFacts.Children.Clear();

        if (game.Info is not { } info)
        {
            AboutPanel.Visibility = game.NoStoreInfo ? Visibility.Visible : Visibility.Collapsed;
            AboutText.Text = IgdbService.Configured
                ? "No details for this game: none of the stores GameDeck asks (Steam, GOG, Epic, Microsoft) or IGDB has it."
                : "No store details for this game: none of the stores GameDeck asks (Steam, GOG, Epic, Microsoft) has it. " +
                  "IGDB knows more games: add your own keys in Settings › Library.";
            AboutSource.Visibility = Visibility.Collapsed;
            return;
        }

        AboutPanel.Visibility = Visibility.Visible;
        AboutText.Text = info.Description ?? "";
        AboutText.Visibility = info.Description is null ? Visibility.Collapsed : Visibility.Visible;

        void Fact(string value, string label)
        {
            var card = new Border
            {
                CornerRadius = new CornerRadius(8), Padding = new Thickness(16, 10, 16, 10), Margin = new Thickness(0, 0, 10, 10),
                Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = value, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("TextPrimary"), MaxWidth = 320, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = value },
                        new TextBlock { Text = label, FontSize = 12, Foreground = (Brush)FindResource("TextMuted"), Margin = new Thickness(0, 2, 0, 0) }
                    }
                }
            };
            card.SetResourceReference(Border.BackgroundProperty, "PanelBg");
            AboutFacts.Children.Add(card);
        }

        if (info.Developers.Count > 0) Fact(string.Join(", ", info.Developers), info.Developers.Count == 1 ? "developer" : "developers");
        if (info.Publishers.Count > 0 && !info.Publishers.SequenceEqual(info.Developers, StringComparer.OrdinalIgnoreCase))
            Fact(string.Join(", ", info.Publishers), info.Publishers.Count == 1 ? "publisher" : "publishers");
        if (info.Released is { } released) Fact(released, "released");
        if (info.Metacritic is { } score) Fact(score.ToString(), "Metacritic");

        AboutFacts.Visibility = AboutFacts.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AboutSource.Text = info.Source switch
        {
            "GOG" => "From GOG",
            "Microsoft" => "From the Microsoft Store",
            "Epic" => "From the Epic Games Store",
            "EpicLauncher" => "From the Epic Games Launcher",
            "IGDB" => "From IGDB",
            _ => "From the Steam store"
        };
        AboutSource.Visibility = Visibility.Visible;
    }

    // ---- the rating: five stars to click ----

    /// <summary>A star was clicked: the game's new rating (0 = clicking the current rating again takes it away).</summary>
    public event EventHandler<(Game Game, int Rating)>? RatingRequested;

    /// <summary>The game's rating as five stars (gold up to the rating); clicking one rates the game, the same one again clears it.</summary>
    public void ShowRating(Game game)
    {
        if (!ReferenceEquals(DataContext, game)) return;

        RatingStars.Children.Clear();
        for (var i = 1; i <= 5; i++)
        {
            var star = i;
            var filled = star <= game.Rating;

            var text = new TextBlock
            {
                Text = filled ? "★" : "☆", FontSize = 22, Margin = new Thickness(2, 0, 2, 0), Cursor = System.Windows.Input.Cursors.Hand,
                Foreground = filled ? GameSettingsDialog.StarBrush : (Brush)FindResource("TextMuted"),
                Background = Brushes.Transparent,
                ToolTip = star == game.Rating ? "Click again to remove the rating" : $"Rate it {star} star{(star == 1 ? "" : "s")}"
            };
            text.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                RatingRequested?.Invoke(this, (game, game.Rating == star ? 0 : star));
                ShowRating(game);
            };

            RatingStars.Children.Add(text);
        }
    }

    // ---- layout: one column, or two on a wide screen ----

    /// <summary>From this width on, the play history and the sessions get a column of their own on the right.</summary>
    private const double WideWidth = 1240;
    private const double WideMaxWidth = 1640;

    /// <summary>The page in the middle of its space (Big Picture) instead of on the left (the desktop window).</summary>
    public bool Centered
    {
        get => PageRoot.HorizontalAlignment == HorizontalAlignment.Center;
        set => PageRoot.HorizontalAlignment = value ? HorizontalAlignment.Center : HorizontalAlignment.Left;
    }

    /// <summary>
    /// The visible width changed (window resized, scroll bar shown or hidden). Not SizeChanged: when that fires, ViewportWidth
    /// still holds the old width, so the page kept the layout of the window's previous size. (Only this scroller's own changes:
    /// the event also bubbles up from lists inside the page.)
    /// </summary>
    private void PageScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, PageScroll) && e.ViewportWidthChange != 0)
            UpdateLayoutMode();
    }

    /// <summary>
    /// Wide (and something to put there): the cover, the game's info and screenshots, then a column with the play history and the
    /// sessions. Otherwise everything in one column under the name, as before.
    /// </summary>
    private void UpdateLayoutMode()
    {
        var space = PageScroll.ViewportWidth > 0 ? PageScroll.ViewportWidth : PageScroll.ActualWidth;
        var sideHasContent = HistoryPanel.Visibility == Visibility.Visible || SessionsPanel.Visibility == Visibility.Visible;
        var wide = space >= WideWidth && sideHasContent;

        // (the page's width is set when wide: sized by its content, the two columns wouldn't share it evenly)
        PageRoot.MaxWidth = wide ? WideMaxWidth : 900;
        PageRoot.Width = wide ? Math.Min(space - 16, WideMaxWidth) : double.NaN;

        GapColumn.Width = new GridLength(wide ? 48 : 0);
        SideColumn.Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

        Grid.SetColumn(SidePanel, wide ? 3 : 1);
        Grid.SetRow(SidePanel, wide ? 0 : 1);
        SidePanel.Margin = new Thickness(0, wide ? -22 : 0, 0, 0);   // (level with the name: each section has room above it)
    }

    // ---- scrolling with a controller (Big Picture) ----

    /// <summary>The page's scroller (Big Picture scrolls it with the controller).</summary>
    public ScrollViewer Scroller => PageScroll;

    /// <summary>True when "Add screenshots" is fully on screen (going up from it selects Play again only then).</summary>
    public bool AddShotsInView
    {
        get
        {
            if (!AddShotsButton.IsVisible) return false;

            var top = AddShotsButton.TransformToAncestor(PageScroll).Transform(new Point(0, 0)).Y;
            return top >= 0 && top + AddShotsButton.ActualHeight <= PageScroll.ViewportHeight;
        }
    }

    /// <summary>Back to the top of the page (Play selected again).</summary>
    public void ScrollToTop() => PageScroll.ScrollToTop();

    /// <summary>"Where you left off" (shown again after it was written in Big Picture while the page is open).</summary>
    public void ShowLeftOff(Game game)
    {
        if (!ReferenceEquals(DataContext, game)) return;

        LeftOffPanel.Visibility = game.HasLeftOff ? Visibility.Visible : Visibility.Collapsed;
        LeftOffText.Text = game.LeftOff ?? "";
        LeftOffHeader.Text = game.LeftOffUtc is { } when
            ? "WHERE YOU LEFT OFF  ·  " + when.ToLocalTime().ToString("d MMM yyyy").ToUpperInvariant()
            : "WHERE YOU LEFT OFF";
    }

    // ---- play history ----

    private const double DayCell = 14;
    private const double DayGap = 3;

    /// <summary>A line of numbers, then a square per day (a column per week, Monday at the top), darker the more was played.</summary>
    private void ShowHistory(GameHistory? history)
    {
        HistoryGrid.Children.Clear();
        HistoryGrid.ColumnDefinitions.Clear();
        HistoryGrid.RowDefinitions.Clear();

        if (history is null)
        {
            HistoryPanel.Visibility = Visibility.Collapsed;
            return;
        }

        HistoryPanel.Visibility = Visibility.Visible;

        var sessions = history.Sessions == 1 ? "1 session" : $"{history.Sessions} sessions";
        var days = history.DaysPlayed == 1 ? "1 day" : $"{history.DaysPlayed} days";
        HistorySummary.Text = history.Sessions == 1
            ? $"{sessions}  ·  {Game.FormatPlaytime(history.LongestSeconds)}"
            : $"{sessions} on {days}  ·  average {Game.FormatPlaytime(history.AverageSeconds)}  ·  longest {Game.FormatPlaytime(history.LongestSeconds)}";

        var weeks = history.Days.Count / 7;
        for (var w = 0; w < weeks; w++)
            HistoryGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(DayCell + DayGap) });
        for (var d = 0; d < 7; d++)
            HistoryGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(DayCell + DayGap) });

        var today = DateOnly.FromDateTime(DateTime.Now);
        var most = Math.Max(1, history.Days.Max(d => d.Seconds));
        var accent = (Brush)FindResource("Accent");
        var empty = (Brush)FindResource("RaisedBg");

        for (var i = 0; i < history.Days.Count; i++)
        {
            var (day, seconds) = history.Days[i];
            if (day > today) continue;   // (the rest of this week)

            var cell = new Border
            {
                Width = DayCell, Height = DayCell, CornerRadius = new CornerRadius(3),
                Background = seconds > 0 ? accent : empty,
                Opacity = seconds > 0 ? 0.35 + 0.65 * seconds / most : 1,
                ToolTip = $"{day:ddd d MMM yyyy}: {(seconds > 0 ? Game.FormatPlaytime(seconds) : "not played")}"
            };

            Grid.SetColumn(cell, i / 7);
            Grid.SetRow(cell, i % 7);
            HistoryGrid.Children.Add(cell);
        }

        HistoryRange.Text = $"The last {weeks} weeks, from {history.Days[0].Day:d MMM}. Each column is a week, Monday at the top.";
    }

    // ---- screenshots ----

    private async void LoadShots(Game game)
    {
        List<string> files;
        List<BitmapImage?> thumbnails;

        try
        {
            var library = ScreenshotService.Library?.Invoke().ToList();   // (a copy, taken here: the folders are read in the background)
            files = await Task.Run(() => ScreenshotService.Find(game, library));
            thumbnails = await Task.Run(() => files.Select(f => ScreenshotService.Thumbnail(f)).ToList());   // (frozen pictures can be made on another thread)
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
                Child = new Image { Source = thumbnail, Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
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
        button.Effect = new DropShadowEffect { Color = ThemeService.Current == AppTheme.Dark ? Colors.White : ThemeService.AccentColor, BlurRadius = 22, ShadowDepth = 0, Opacity = 0.9 };
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
