using System.Windows;

namespace GameDeck.Views;

/// <summary>
/// A small dark pop-up with a heading, a message and two buttons.
/// ShowDialog() returns true when the confirm button (Launch) is pressed, false for Cancel / Esc / closing.
/// </summary>
public partial class LaunchWarningDialog : Window
{
    public LaunchWarningDialog(string heading, string message, string confirmText, string cancelText)
    {
        InitializeComponent();

        HeadingText.Text = heading;
        MessageText.Text = message;
        LaunchButton.Content = confirmText;
        CancelButton.Content = cancelText;
    }

    private void LaunchButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
