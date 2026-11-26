using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GameDeck.Services;

namespace GameDeck.Views;

/// <summary>
/// "The PC goes to sleep in 60 seconds" after a game closed (see <see cref="AfterGame"/>), on top of everything, with Cancel and
/// "Sleep now". Used when Big Picture isn't open (Big Picture counts down in its own Power card, for the controller).
/// </summary>
public sealed class PowerCountdownWindow : Window
{
    public const int Seconds = 60;

    private readonly AfterGame _choice;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly TextBlock _heading;
    private int _left = Seconds;
    private bool _done;

    private PowerCountdownWindow(AfterGame choice, string gameName)
    {
        _choice = choice;

        Title = AppInfo.DisplayName;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        ShowInTaskbar = true;   // (easy to find again if another window covers it)
        SetResourceReference(BackgroundProperty, "AppBg");
        TitleBarStyler.Apply(this);

        _heading = new TextBlock { FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        _heading.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");

        var message = new TextBlock
        {
            FontSize = 15, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0),
            Text = $"{gameName} has closed, and you chose \"{choice.Label()}\" for when it does. Programs are asked to close normally first."
        };
        message.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");

        var cancel = new Button { Content = "Cancel", Style = (Style)FindResource("DialogButton"), IsCancel = true };
        cancel.SetResourceReference(BackgroundProperty, "RaisedBg");
        cancel.SetResourceReference(ForegroundProperty, "TextPrimary");
        cancel.Click += (_, _) => Close();

        var now = new Button { Content = choice.NowText(), Style = (Style)FindResource("DialogButton"), Foreground = Brushes.White };
        now.SetResourceReference(BackgroundProperty, "Accent");
        now.Click += (_, _) => Finish();

        Content = new StackPanel
        {
            Margin = new Thickness(28, 24, 28, 22), Width = 420,
            Children =
            {
                _heading, message,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 26, 0, 0),
                    Children = { cancel, now }
                }
            }
        };

        UpdateHeading();
        _timer.Tick += (_, _) =>
        {
            if (--_left <= 0)
                Finish();
            else
                UpdateHeading();
        };

        Loaded += (_, _) =>
        {
            cancel.Focus();   // Enter / Space cancels: a key pressed by chance must not turn the PC off
            _timer.Start();
        };
        Closed += (_, _) => _timer.Stop();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
    }

    /// <summary>Shows the countdown; when it reaches zero (or "now" is pressed) the PC sleeps or turns off. Cancel stops it.</summary>
    public static void Start(AfterGame choice, string gameName)
    {
        if (choice == AfterGame.ReturnToGameDeck) return;

        var window = new PowerCountdownWindow(choice, gameName);
        window.Show();
        window.Activate();
    }

    private void UpdateHeading() => _heading.Text = _choice.Countdown(_left);

    private void Finish()
    {
        if (_done) return;
        _done = true;

        _timer.Stop();
        Close();
        _choice.Run();
    }
}
