using System.Windows;
using System.Windows.Input;
using GameDeck.Services;

namespace GameDeck.Views;

/// <summary>
/// The settings in a window of their own (the gear in the library header opens it), laid out like Big Picture's settings
/// (<see cref="SettingsView"/>). It stays above the main window, which can still be used meanwhile; Esc closes it.
/// </summary>
public class SettingsWindow : Window
{
    public SettingsView View { get; } = new();

    public SettingsWindow(Window owner)
    {
        Owner = owner;
        Title = $"{AppInfo.DisplayName} Settings";
        Width = 1000;
        Height = 700;
        MinWidth = 720;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "AppBg");
        Content = View;

        TitleBarStyler.Apply(this);
        View.Reload();

        PreviewKeyDown += (_, e) =>
        {
            // (while a new quick launch shortcut is being typed, Esc only cancels that)
            if (e.Key == Key.Escape && !View.IsCapturingShortcut)
            {
                Close();
                e.Handled = true;
            }
        };
    }
}
