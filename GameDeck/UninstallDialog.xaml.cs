using System.Windows;
using System.Windows.Input;
using GameDeck.Services;

namespace GameDeck.Views;

/// <summary>
/// "Uninstall GameDeck" pop-up. ShowDialog() returns true when one of the two uninstall buttons was used
/// (see <see cref="KeepData"/>); Cancel, X and Esc return false.
/// </summary>
public partial class UninstallDialog : Window
{
    /// <summary>True = remove the program only; false = remove everything, including the saved data.</summary>
    public bool KeepData { get; private set; }

    public UninstallDialog()
    {
        InitializeComponent();
        TitleBarStyler.Apply(this);
    }

    private void KeepButton_Click(object sender, RoutedEventArgs e) => Choose(keepData: true);

    private void RemoveAllButton_Click(object sender, RoutedEventArgs e) => Choose(keepData: false);

    private void Choose(bool keepData)
    {
        KeepData = keepData;
        DialogResult = true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            DialogResult = false;
    }
}
