using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GameDeck.Services;

namespace GameDeck.Views;

public enum MessageChoice
{
    Ok,
    Yes,
    No,
    Cancel
}

/// <summary>
/// The app's own message pop-up (dark, like the other dialogs) instead of the plain Windows message box.
/// Use the static helpers: <see cref="Show"/> for a notice, <see cref="Ask"/> for a yes / no question,
/// <see cref="Choose"/> for three answers.
/// </summary>
public partial class MessageDialog : Window
{
    private readonly MessageChoice _escapeChoice;

    /// <summary>The button that was pressed (Esc or closing the window counts as the Cancel / No button).</summary>
    public MessageChoice Choice { get; private set; }

    /// <param name="buttons">In display order, left to right. The last one is the default (purple).</param>
    private MessageDialog(string heading, string message, (string Text, MessageChoice Choice)[] buttons)
    {
        InitializeComponent();
        TitleBarStyler.Apply(this);

        HeadingText.Text = heading;
        MessageText.Text = message;

        var choices = buttons.Select(b => b.Choice).ToList();
        _escapeChoice = choices.Contains(MessageChoice.Cancel) ? MessageChoice.Cancel
                      : choices.Contains(MessageChoice.No) ? MessageChoice.No
                      : choices[^1];
        Choice = _escapeChoice;

        for (var i = 0; i < buttons.Length; i++)
        {
            var (text, choice) = buttons[i];
            var primary = i == buttons.Length - 1;

            var button = new Button
            {
                Content = text,
                Style = (Style)FindResource("DialogButton"),
                Background = new SolidColorBrush(primary ? Color.FromRgb(0x80, 0x37, 0xFE) : Color.FromRgb(0x2A, 0x2F, 0x3A)),
                Foreground = Brushes.White,
                IsDefault = primary
            };

            button.Click += (_, _) =>
            {
                Choice = choice;
                DialogResult = true;
            };

            ButtonRow.Children.Add(button);
        }
    }

    // ---------- helpers ----------

    /// <summary>A notice with one OK button. Pass a heading, or just the message.</summary>
    public static void Show(Window? owner, string message, string heading = "GameDeck") =>
        Open(owner, heading, message, [("OK", MessageChoice.Ok)]);

    /// <summary>A question with two buttons. True when <paramref name="confirmText"/> was pressed.</summary>
    public static bool Ask(Window? owner, string heading, string message, string confirmText, string cancelText = "Cancel") =>
        Open(owner, heading, message, [(cancelText, MessageChoice.Cancel), (confirmText, MessageChoice.Yes)]) == MessageChoice.Yes;

    /// <summary>A question with three answers (shown as cancel, no, yes from left to right).</summary>
    public static MessageChoice Choose(Window? owner, string heading, string message, string yesText, string noText, string cancelText = "Cancel") =>
        Open(owner, heading, message, [(cancelText, MessageChoice.Cancel), (noText, MessageChoice.No), (yesText, MessageChoice.Yes)]);

    private static MessageChoice Open(Window? owner, string heading, string message, (string Text, MessageChoice Choice)[] buttons)
    {
        var dialog = new MessageDialog(heading, message, buttons);

        // A window that was never shown, or is hidden in the tray, can't own a dialog: then show it centered on screen, on top
        if (owner is { IsVisible: true })
        {
            dialog.Owner = owner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.Topmost = true;
        }

        dialog.ShowDialog();
        return dialog.Choice;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;

        Choice = _escapeChoice;
        DialogResult = false;
        e.Handled = true;
    }
}
