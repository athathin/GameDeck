using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace GameDeck.Views;

/// <summary>
/// A small notice in the bottom-right corner when a game starts ("Game on!", or "PC optimized" when the game's launch
/// routine really did something). It never takes focus from the game, fades away by itself and closes on a click.
/// </summary>
public partial class GameToastWindow : Window
{
    private static readonly TimeSpan Visible = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Fade = TimeSpan.FromMilliseconds(350);

    private static GameToastWindow? _current;   // a new toast replaces the one still on screen

    private readonly DispatcherTimer _timer = new() { Interval = Visible };
    private readonly string _gameName;
    private readonly DateTime _shownAt = DateTime.UtcNow;
    private bool _closing;

    private GameToastWindow(string headline, string game, string? detail, TimeSpan? visible = null)
    {
        InitializeComponent();
        _gameName = game;
        _timer.Interval = visible ?? Visible;

        HeadlineText.Text = headline;
        GameText.Text = game;
        DetailText.Text = detail ?? "";
        DetailText.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;

        // Put the window in its corner BEFORE it is ever shown. Left unset, Windows places a new window wherever it likes
        // (next to the program in front), which made the notice flash up there first and then jump to the corner.
        var content = (FrameworkElement)Content;
        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var area = SystemParameters.WorkArea;

        // A fixed size and position, set before the first Show: with SizeToContent the window resized itself while
        // showing, and Windows could draw it next to the game first and then move it to the corner.
        Width = Math.Ceiling(content.DesiredSize.Width);
        Height = Math.Ceiling(content.DesiredSize.Height);
        Left = area.Right - Width - 8;
        Top = area.Bottom - Height - 8;

        // a tool window that is never activated: the game keeps the keyboard and stays in front
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowLong(handle, GwlExStyle, GetWindowLong(handle, GwlExStyle) | WsExNoActivate | WsExToolWindow);
        };

        Loaded += (_, _) =>
        {
            BeginAnimation(OpacityProperty, new DoubleAnimation(1, Fade));
            _timer.Start();
        };

        _timer.Tick += (_, _) => FadeOutAndClose();
    }

    /// <summary>Shows the notice. <paramref name="optimizedSummary"/> is what the launch routine did (null = nothing).</summary>
    /// <param name="inTray">GameDeck waits in the tray while the game runs (only then does the notice say so).</param>
    public static void ShowFor(string gameName, string? optimizedSummary, bool inTray)
    {
        // the same game announced twice within a moment (a double press, two launch paths): show it once
        if (_current is { _closing: false } current && current._gameName == gameName &&
            DateTime.UtcNow - current._shownAt < TimeSpan.FromSeconds(3))
            return;

        _current?.CloseNow();

        var toast = optimizedSummary is null
            ? new GameToastWindow(RandomHeadline(), gameName, inTray ? "GameDeck is running quietly in the system tray." : null)
            : new GameToastWindow("PC optimized", gameName, optimizedSummary);

        _current = toast;
        toast.Show();
    }

    /// <summary>Shows a short notice that isn't about a game starting ("Screenshot saved"), the same way.</summary>
    /// <param name="visible">How long it stays (5 seconds when not given).</param>
    public static void ShowMessage(string headline, string text, string? detail = null, TimeSpan? visible = null)
    {
        _current?.CloseNow();
        _current = new GameToastWindow(headline, text, detail, visible);
        _current.Show();
    }

    private static string RandomHeadline()
    {
        string[] lines = ["Game on!", "Have fun!", "Good luck out there!", "Let's play!", "Enjoy the game!"];
        return lines[Random.Shared.Next(lines.Length)];
    }

    private void FadeOutAndClose()
    {
        if (_closing) return;
        _closing = true;
        _timer.Stop();

        var fade = new DoubleAnimation(0, Fade);
        fade.Completed += (_, _) => CloseNow();
        BeginAnimation(OpacityProperty, fade);
    }

    private void CloseNow()
    {
        _closing = true;
        _timer.Stop();

        if (ReferenceEquals(_current, this))
            _current = null;

        Close();
    }

    private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => FadeOutAndClose();

    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr window, int index, int value);
}
