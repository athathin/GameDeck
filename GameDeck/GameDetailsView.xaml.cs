using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
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
        CategoriesText.Text = game.Tags.Count > 0 ? "Categories: " + string.Join(", ", game.Tags) : "";
        CategoriesText.Visibility = game.Tags.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
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
        PlayButton.RenderTransformOrigin = new Point(0.5, 0.5);
        PlayButton.RenderTransform = new ScaleTransform(1.06, 1.06);
        PlayButton.Background = PlaySelected;
        PlayButton.Effect = new DropShadowEffect { Color = Colors.White, BlurRadius = 22, ShadowDepth = 0, Opacity = 0.9 };
    }

    /// <summary>Draws the Play button as pressed: darker and pushed in.</summary>
    public void PressPlay()
    {
        PlayButton.RenderTransform = new ScaleTransform(0.95, 0.95);
        PlayButton.Background = PlayPressed;
    }

    /// <summary>Back to the normal look (the desktop page never calls <see cref="SelectPlay"/>).</summary>
    public void ResetPlay()
    {
        PlayButton.ClearValue(BackgroundProperty);
        PlayButton.ClearValue(EffectProperty);
        PlayButton.RenderTransform = Transform.Identity;
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);

    private void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is Game game)
            PlayRequested?.Invoke(this, game);
    }
}
