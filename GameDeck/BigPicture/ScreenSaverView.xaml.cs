using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace GameDeck.Views;

/// <summary>
/// Big Picture's screen saver, against burn-in on OLED screens: black, with one game cover and the clock that fade in at another
/// place every few seconds, so nothing stays in one spot. Big Picture decides when it starts (no input for a while) and stops it
/// on any input.
/// </summary>
public partial class ScreenSaverView : UserControl
{
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(12);   // a new cover in a new place this often
    private const double ItemWidth = 300, ItemHeight = 470;             // the cover and the clock under it

    private readonly DispatcherTimer _timer = new() { Interval = Step };
    private readonly Random _random = new();
    private IReadOnlyList<string> _covers = [];

    public ScreenSaverView()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => Next();
    }

    /// <summary>True while it shows (from the moment it starts fading in until it has faded out).</summary>
    public bool IsOn { get; private set; }

    /// <summary>Fades in and starts moving through the covers (picture files; none: the clock alone).</summary>
    public void Start(IReadOnlyList<string> covers)
    {
        if (IsOn) return;

        IsOn = true;
        _covers = covers;
        Visibility = Visibility.Visible;
        Fade(this, to: 1, seconds: 2);
        Next();
        _timer.Start();
    }

    /// <summary>Fades out and lets go of the picture.</summary>
    public void Stop()
    {
        if (!IsOn) return;

        IsOn = false;
        _timer.Stop();
        Fade(this, to: 0, seconds: 0.4, then: () =>
        {
            if (IsOn) return;   // (it started again meanwhile)

            Visibility = Visibility.Collapsed;
            Cover.Source = null;   // frees the picture
        });
    }

    /// <summary>Another cover (and the clock) at another place, faded in.</summary>
    private void Next()
    {
        Cover.Source = _covers.Count == 0 ? null : Load(_covers[_random.Next(_covers.Count)]);
        Clock.Text = DateTime.Now.ToString("t");

        Canvas.SetLeft(Item, _random.NextDouble() * Math.Max(0, Area.ActualWidth - ItemWidth));
        Canvas.SetTop(Item, _random.NextDouble() * Math.Max(0, Area.ActualHeight - ItemHeight));

        Fade(Item, to: 1, seconds: 1.5, from: 0);
    }

    /// <summary>The picture at the size it is shown (not the full file); null when it can't be read.</summary>
    private static BitmapImage? Load(string file)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(file);
            image.DecodePixelWidth = 600;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Animates the opacity (from where it is, or from <paramref name="from"/>), then runs <paramref name="then"/>.</summary>
    private static void Fade(UIElement element, double to, double seconds, Action? then = null, double? from = null)
    {
        var animation = new DoubleAnimation(to, TimeSpan.FromSeconds(seconds)) { From = from };
        if (then is not null)
            animation.Completed += (_, _) => then();

        element.BeginAnimation(OpacityProperty, animation);
    }
}
