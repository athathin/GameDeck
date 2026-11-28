using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameDeck.Services;

namespace GameDeck;

/// <summary>
/// GameDeck's own title bar (MainWindow.xaml: WindowChrome + TitleBar), see-through so a game's picture shows behind it, like
/// in GOG Galaxy. Windows still drags the window by the top strip, maximizes it on a double-click, snaps it and resizes it by
/// its borders; this part draws the icon, the title and the three buttons, keeps everything on screen while maximized, and
/// tells Windows where the maximize button is, so hovering it still offers Windows 11's snap layouts.
/// </summary>
public partial class MainWindow
{
    private const int WmNcHitTest = 0x0084;
    private const int WmNcMouseMove = 0x00A0;
    private const int WmNcLButtonDown = 0x00A1;
    private const int WmNcLButtonUp = 0x00A2;
    private const int WmNcMouseLeave = 0x02A2;
    private const int HtMaxButton = 9;

    private static readonly Brush MaxHover = Frozen(Color.FromArgb(0x26, 0x80, 0x80, 0x80));
    private static readonly Brush MaxPressed = Frozen(Color.FromArgb(0x40, 0x80, 0x80, 0x80));

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);   // (WindowChrome hooks the window here: this hook comes after it, so it is asked first)
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(TitleBarHook);

        TitleIcon.Source = SmallIcon();
        UpdateTitleBar();
        Activated += (_, _) => TitleBar.Opacity = 1;
        Deactivated += (_, _) => TitleBar.Opacity = 0.6;   // (like Windows' own: a window in the background has a paler title)
    }

    /// <summary>The 16-pixel picture of the app's icon (the .ico holds several sizes: the one that fits, not a shrunk big one).</summary>
    private ImageSource? SmallIcon()
    {
        try
        {
            var wanted = 16 * VisualTreeHelper.GetDpi(this).DpiScaleX;
            var frames = BitmapDecoder.Create(new Uri(AppInfo.IconUri), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames;
            return frames.OrderBy(f => f.PixelWidth < wanted ? 1 : 0).ThenBy(f => Math.Abs(f.PixelWidth - wanted)).First();
        }
        catch (Exception ex) when (ex is System.IO.IOException or NotSupportedException or InvalidOperationException)
        {
            return null;   // (just no icon in the title bar)
        }
    }

    private void Window_StateChanged(object? sender, EventArgs e) => UpdateTitleBar();

    /// <summary>
    /// A maximized window reaches a few pixels past the screen's edges on every side (where its resize borders would be): the
    /// content keeps clear of them then. The maximize button shows "restore" while maximized.
    /// </summary>
    private void UpdateTitleBar()
    {
        if (WindowState == WindowState.Maximized)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var frameX = (GetSystemMetrics(SmCxSizeFrame) + GetSystemMetrics(SmCxPaddedBorder)) / dpi.DpiScaleX;
            var frameY = (GetSystemMetrics(SmCySizeFrame) + GetSystemMetrics(SmCxPaddedBorder)) / dpi.DpiScaleY;
            WindowRoot.Margin = new Thickness(frameX, frameY, frameX, frameY);
        }
        else
        {
            WindowRoot.Margin = new Thickness(0);
        }

        var maximized = WindowState == WindowState.Maximized;
        MaximizeButton.Content = maximized ? "" : "";
        MaximizeButton.ToolTip = maximized ? "Restore Down" : "Maximize";
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void MaximizeButton_Click(object sender, RoutedEventArgs e) => ToggleMaximized();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);   // (as Windows' own: Closing decides)

    private void ToggleMaximized()
    {
        if (WindowState == WindowState.Maximized)
            SystemCommands.RestoreWindow(this);
        else
            SystemCommands.MaximizeWindow(this);
    }

    /// <summary>
    /// The maximize button is Windows' "maximize button" (HTMAXBUTTON): hovering it opens the snap layouts, as with Windows' own
    /// title bar. Windows then sends its mouse messages as non-client ones, so its hover look and its click are handled here.
    /// </summary>
    private IntPtr TitleBarHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (message)
        {
            case WmNcHitTest when OverMaximizeButton(lParam):
                if (MaximizeButton.Background != MaxPressed) MaximizeButton.Background = MaxHover;
                handled = true;
                return HtMaxButton;

            case WmNcLButtonDown when wParam == HtMaxButton:
                MaximizeButton.Background = MaxPressed;
                handled = true;
                break;

            case WmNcLButtonUp when wParam == HtMaxButton:
                MaximizeButton.ClearValue(BackgroundProperty);
                ToggleMaximized();
                handled = true;
                break;

            case WmNcMouseMove when wParam != HtMaxButton:
            case WmNcMouseLeave:
                MaximizeButton.ClearValue(BackgroundProperty);
                break;
        }

        return IntPtr.Zero;
    }

    private bool OverMaximizeButton(IntPtr lParam)
    {
        if (!MaximizeButton.IsVisible) return false;

        var x = unchecked((short)(long)lParam);
        var y = unchecked((short)((long)lParam >> 16));
        try
        {
            var point = MaximizeButton.PointFromScreen(new Point(x, y));
            return point.X >= 0 && point.Y >= 0 && point.X < MaximizeButton.ActualWidth && point.Y < MaximizeButton.ActualHeight;
        }
        catch (InvalidOperationException)
        {
            return false;   // (not connected to the screen at this moment)
        }
    }

    private const int SmCxSizeFrame = 32;
    private const int SmCySizeFrame = 33;
    private const int SmCxPaddedBorder = 92;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
