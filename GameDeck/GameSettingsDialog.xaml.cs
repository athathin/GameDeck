using System.Windows;
using System.Windows.Input;
using GameDeck.Models;
using GameDeck.Services;

namespace GameDeck.Views;

/// <summary>
/// "Edit game": the displayed name, the user's categories and the launch options.
/// ShowDialog() returns true when Save was pressed; the values are then in the properties below.
/// </summary>
public partial class GameSettingsDialog : Window
{
    public string GameName => NameBox.Text;
    public string Arguments => ArgsBox.Text;
    public bool RunAsAdmin => AdminCheckBox.IsChecked == true;
    public int Rating { get; private set; }
    public string Notes => NotesBox.Text;

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

        NameBox.Text = game.DisplayName;
        TagsBox.Text = string.Join(", ", game.Tags);
        ArgsBox.Text = game.LaunchArguments ?? "";
        AdminCheckBox.IsChecked = game.RunAsAdmin;
        NotesBox.Text = game.Notes ?? "";
        Rating = game.Rating;
        BuildStars();

        // Only games that start from an .exe (or Steam) can take arguments; only .exe games can be elevated
        ArgsBox.IsEnabled = game.SupportsArguments;
        AdminCheckBox.IsEnabled = game.IsDirectExe;

        LaunchHint.Text = game.IsDirectExe
            ? "Arguments are passed to the game when it starts."
            : game.SupportsArguments
                ? "Steam passes these arguments to the game. Run as administrator isn't available for Steam games."
                : $"{game.Source} games are started by their own launcher, which doesn't accept extra arguments or administrator mode.";

        NameBox.Focus();
        NameBox.SelectAll();
    }

    /// <summary>Five stars to click: the one clicked becomes the rating, and clicking the current rating again clears it.</summary>
    private void BuildStars()
    {
        StarsPanel.Children.Clear();

        for (var i = 1; i <= 5; i++)
        {
            var star = i;
            var filled = star <= Rating;

            var text = new System.Windows.Controls.TextBlock
            {
                Text = filled ? "★" : "☆",
                FontSize = 26,
                Margin = new Thickness(0, 0, 6, 0),
                Cursor = Cursors.Hand,
                Foreground = filled ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xC8, 0x57))
                                    : ThemeService.TextMuted
            };

            text.MouseLeftButtonUp += (_, _) =>
            {
                Rating = Rating == star ? 0 : star;
                BuildStars();
            };

            StarsPanel.Children.Add(text);
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            DialogResult = false;
    }
}
