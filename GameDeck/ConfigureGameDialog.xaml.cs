using System.Windows;
using System.Windows.Input;
using GameDeck.Models;
using GameDeck.Services;

namespace GameDeck.Views;

/// <summary>
/// Right-click › Manage installation › "Configure…": how the game is started (launch arguments, run as administrator) and its
/// launch routine. ShowDialog() returns true when OK was pressed; the launch options are then in the properties below. The
/// routine has its own window with its own Save, and is saved from there (<c>saveRoutine</c>).
/// </summary>
public partial class ConfigureGameDialog : Window
{
    private readonly Game _game;
    private readonly Action<LaunchProfile?> _saveRoutine;

    public string Arguments => ArgsBox.Text;
    public bool RunAsAdmin => AdminCheckBox.IsChecked == true;

    public ConfigureGameDialog(Game game, Action<LaunchProfile?> saveRoutine)
    {
        InitializeComponent();
        TitleBarStyler.Apply(this);

        _game = game;
        _saveRoutine = saveRoutine;

        Title = $"Configure {game.DisplayName}";
        HeaderTitle.Text = $"Configuring \"{game.DisplayName}\"";
        HeaderSource.Text = game.SourceText;
        HeaderImage.Source = GameSettingsDialog.CurrentCover(game);

        ArgsBox.Text = game.LaunchArguments ?? "";
        AdminCheckBox.IsChecked = game.RunAsAdmin;

        // Only games that start from an .exe (or Steam) can take arguments; only .exe games can be elevated
        ArgsBox.IsEnabled = game.SupportsArguments;
        AdminCheckBox.IsEnabled = game.IsDirectExe;

        LaunchHint.Text = game.IsDirectExe
            ? "Arguments are passed to the game when it starts."
            : game.SupportsArguments
                ? "Steam passes these arguments to the game. Run as administrator isn't available for Steam games."
                : $"{game.Source} games are started by their own launcher, which doesn't accept extra arguments or administrator mode.";

        ShowRoutine();

        if (ArgsBox.IsEnabled)
        {
            ArgsBox.Focus();
            ArgsBox.SelectAll();
        }
    }

    /// <summary>One line saying what the routine does, e.g. "Starts 2 apps, high-performance power plan".</summary>
    private void ShowRoutine()
    {
        if (_game.Profile is not { IsEmpty: false } profile)
        {
            RoutineText.Text = "Off: the game just starts.";
            return;
        }

        static string Apps(int n) => n == 1 ? "1 app" : $"{n} apps";

        var parts = new List<string>();
        if (profile.StartApps.Count > 0) parts.Add("starts " + Apps(profile.StartApps.Count));
        if (profile.CloseApps.Count > 0) parts.Add("closes " + Apps(profile.CloseApps.Count));
        if (profile.HighPerformancePower) parts.Add("high-performance power plan");
        if (profile.Display is not null) parts.Add("changes the display");

        var text = string.Join(", ", parts);
        RoutineText.Text = char.ToUpper(text[0]) + text[1..];
    }

    private void RoutineButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new LaunchRoutineDialog(_game) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        _saveRoutine(dialog.Result);
        ShowRoutine();
    }

    private void OkButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            DialogResult = false;
    }
}
