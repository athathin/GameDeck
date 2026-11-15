using GameDeck.Services;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GameDeck.Views;

/// <summary>The small window shown while a new version downloads, in the colors of the rest of the app.</summary>
public sealed class UpdateWindow : Window
{
    private readonly ProgressBar _bar;
    private readonly TextBlock _status;

    public UpdateWindow(string version)
    {
        Title = "GameDeck";
        Width = 420;
        Height = 150;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        Background = ThemeService.AppBg;

        _status = new TextBlock
        {
            Text = "0%", Foreground = ThemeService.TextMuted, FontSize = 13,
            Margin = new Thickness(0, 8, 0, 0)
        };

        _bar = new ProgressBar
        {
            Height = 8, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 18, 0, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(0x80, 0x37, 0xFE)),
            Background = ThemeService.RaisedBg, BorderThickness = new Thickness(0)
        };

        Content = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x80, 0x37, 0xFE)), BorderThickness = new Thickness(1),
            Padding = new Thickness(28, 24, 28, 20),
            Child = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = "Updating GameDeck…", Foreground = ThemeService.TextPrimary, FontSize = 20, FontWeight = FontWeights.SemiBold },
                    new TextBlock
                    {
                        Text = $"Version {version}", Foreground = ThemeService.TextSecondary,
                        FontSize = 14, Margin = new Thickness(0, 4, 0, 0)
                    },
                    _bar,
                    _status
                }
            }
        };
    }

    /// <summary>0 to 1.</summary>
    public void SetProgress(double fraction)
    {
        _bar.Value = fraction;
        _status.Text = fraction >= 1 ? "Installing…" : $"{fraction:P0}";
    }
}
