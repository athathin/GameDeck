using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GameDeck.Services;

namespace GameDeck.Views;

public partial class BigPictureWindow
{
    // ---------- the screen saver: after a while without input, covers moving on black (against burn-in on OLED screens) ----------

    private static readonly TimeSpan ScreenSaverStep = TimeSpan.FromSeconds(12);   // a new cover in a new place this often
    private const double ScreenSaverItemWidth = 300, ScreenSaverItemHeight = 470;   // the cover and the clock under it

    private readonly DispatcherTimer _saverTimer = new() { Interval = ScreenSaverStep };
    private long _lastInputAt = Environment.TickCount64;
    private bool _saverOn;
    private List<string> _saverCovers = [];
    private readonly Random _random = new();

    /// <summary>A button, a key or the mouse was used: the time without input starts again.</summary>
    private void NoteInput() => _lastInputAt = Environment.TickCount64;

    /// <summary>Input arrived: notes it, and ends the screen saver if it is on.</summary>
    /// <returns>True when the input only woke the screen (it must do nothing else: the user couldn't see what it would do).</returns>
    private bool WakeFromScreenSaver()
    {
        NoteInput();
        if (!_saverOn) return false;

        _saverOn = false;
        _saverTimer.Stop();
        Fade(ScreenSaverOverlay, to: 0, seconds: 0.4, then: () =>
        {
            if (_saverOn) return;   // (it started again meanwhile)

            ScreenSaverOverlay.Visibility = Visibility.Collapsed;
            ScreenSaverCover.Source = null;   // frees the picture
        });
        return true;
    }

    /// <summary>Called every second: starts the screen saver once the screen has been left alone long enough.</summary>
    private void CheckScreenSaver()
    {
        var minutes = SettingsService.ScreenSaverMinutes;
        if (_saverOn || _closing || minutes == 0 || Environment.TickCount64 - _lastInputAt < minutes * 60_000L) return;

        _saverOn = true;

        // the covers on this PC (looked for once: the screen saver may run for hours)
        _saverCovers = _library.Where(g => !g.IsHidden)
            .Select(CoverService.FindCachedCover)
            .OfType<string>()
            .ToList();

        ScreenSaverOverlay.Visibility = Visibility.Visible;
        Fade(ScreenSaverOverlay, to: 1, seconds: 2);
        NextScreenSaverCover();
        _saverTimer.Start();
    }

    private void ScreenSaverTimer_Tick(object? sender, EventArgs e) => NextScreenSaverCover();

    /// <summary>Another cover (and the clock) at another place, faded in.</summary>
    private void NextScreenSaverCover()
    {
        ScreenSaverCover.Source = _saverCovers.Count == 0 ? null : LoadCover(_saverCovers[_random.Next(_saverCovers.Count)]);
        ScreenSaverClock.Text = DateTime.Now.ToString("t");

        var room = (Width: Math.Max(0, ScreenSaverCanvas.ActualWidth - ScreenSaverItemWidth),
                    Height: Math.Max(0, ScreenSaverCanvas.ActualHeight - ScreenSaverItemHeight));
        Canvas.SetLeft(ScreenSaverItem, _random.NextDouble() * room.Width);
        Canvas.SetTop(ScreenSaverItem, _random.NextDouble() * room.Height);

        Fade(ScreenSaverItem, to: 1, seconds: 1.5, from: 0);
    }

    /// <summary>The picture at the size it is shown (not the full file); null when it can't be read.</summary>
    private static BitmapImage? LoadCover(string file)
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
        catch (Exception ex) when (ex is System.IO.IOException or NotSupportedException or UnauthorizedAccessException or ArgumentException)
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
