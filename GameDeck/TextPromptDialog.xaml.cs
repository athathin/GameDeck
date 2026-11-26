using System.Text.RegularExpressions;
using System.Windows;
using GameDeck.Services;

namespace GameDeck.Views;

/// <summary>
/// A small pop-up asking for one line of text (a category name, where you left off). ShowDialog() returns true when confirmed;
/// the cleaned-up text is then in <see cref="Value"/>.
/// </summary>
public partial class TextPromptDialog : Window
{
    public string Value { get; private set; } = "";

    private readonly bool _freeText;

    /// <param name="freeText">
    /// A sentence rather than a name: up to <paramref name="maxLength"/> characters, commas allowed, and an empty box can be
    /// confirmed (it clears the text). Off: a category name (30 characters, no commas, not empty).
    /// </param>
    public TextPromptDialog(string heading, string prompt, string initialText, string confirmText, bool freeText = false, int maxLength = 200,
                            string? cancelText = null)
    {
        InitializeComponent();
        TitleBarStyler.Apply(this);

        _freeText = freeText;
        HeadingText.Text = heading;
        PromptText.Text = prompt;
        ConfirmButton.Content = confirmText;
        if (cancelText is not null)
            CancelButton.Content = cancelText;
        if (freeText)
            InputBox.MaxLength = maxLength;
        InputBox.Text = initialText;

        InputBox.Focus();
        InputBox.SelectAll();
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        if (_freeText)
        {
            Value = Regex.Replace(InputBox.Text, @"\s+", " ").Trim();
            DialogResult = true;
            return;
        }

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
