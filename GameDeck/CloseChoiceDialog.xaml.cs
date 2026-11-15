using System.Windows;
using System.Windows.Input;
using GameDeck.Services;

namespace GameDeck.Views;

/// <summary>
/// "Minimize to tray or close?" pop-up. ShowDialog() returns true when one of the two buttons was used
/// (see <see cref="Choice"/>); closing the pop-up with X / Esc returns false, meaning "stay open".
/// </summary>
public partial class CloseChoiceDialog : Window
{
    /// <summary>What the user picked: <see cref="CloseAction.MinimizeToTray"/> or <see cref="CloseAction.Exit"/>.</summary>
    public CloseAction Choice { get; private set; }

    public bool DontAskAgain => DontAskCheckBox.IsChecked == true;

    public CloseChoiceDialog()
    {
        InitializeComponent();
        TitleBarStyler.Apply(this);
    }

    private void TrayButton_Click(object sender, RoutedEventArgs e) => Choose(CloseAction.MinimizeToTray);

    private void ExitButton_Click(object sender, RoutedEventArgs e) => Choose(CloseAction.Exit);

    private void Choose(CloseAction action)
    {
        Choice = action;
        DialogResult = true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            DialogResult = false;
    }
}
