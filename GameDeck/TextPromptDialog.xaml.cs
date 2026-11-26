using System.Text.RegularExpressions;
using System.Windows;
using GameDeck.Services;

namespace GameDeck.Views;

/// <summary>
/// A small pop-up asking for one line of text (a category name). ShowDialog() returns true when confirmed;
/// the cleaned-up text is then in <see cref="Value"/>.
/// </summary>
public partial class TextPromptDialog : Window
{
    public string Value { get; private set; } = "";

    public TextPromptDialog(string heading, string prompt, string initialText, string confirmText)
    {
        InitializeComponent();
        TitleBarStyler.Apply(this);

        HeadingText.Text = heading;
        PromptText.Text = prompt;
        ConfirmButton.Content = confirmText;
        InputBox.Text = initialText;

        InputBox.Focus();
        InputBox.SelectAll();
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        // Commas separate categories in "Edit game", so they can't be part of a name
        var cleaned = Regex.Replace(InputBox.Text.Replace(',', ' '), @"\s+", " ").Trim();
        if (cleaned.Length == 0)
        {
            InputBox.Focus();
            return;   // nothing typed: stay open
        }

        Value = cleaned;
        DialogResult = true;
    }
}
