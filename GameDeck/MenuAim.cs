using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace GameDeck;

/// <summary>
/// Right-click menus keep a submenu open while the pointer heads for it. WPF closes an open submenu the moment the pointer
/// leaves its entry for anything but the submenu itself, so on the way from an entry to its submenu (down and to the right,
/// to "Uninstall") it closed before it could be reached; only a very fast swipe got there.
/// Here, while a submenu is open and the pointer moves towards it (inside the triangle from the pointer to the submenu's
/// near edge), the pointer stays with the open entry. Everything is as usual again as soon as the pointer stops (a short
/// pause), moves away from the submenu, reaches the submenu, or the menu closes. One handler for every context menu.
/// </summary>
internal static class MenuAim
{
    /// <summary>A pause this long on another entry means the pointer stopped there: that entry gets it after all.</summary>
    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(250);

    private static ContextMenu? _menu;       // the menu whose entries are ignoring the pointer now
    private static Point? _last;              // the pointer's last position (screen pixels)
    private static DispatcherTimer? _timer;

    public static void Register()
    {
        EventManager.RegisterClassHandler(typeof(ContextMenu), UIElement.PreviewMouseMoveEvent,
            new MouseEventHandler(OnMove), handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.ClosedEvent,
            new RoutedEventHandler((sender, _) => { if (ReferenceEquals(sender, _menu)) Release(); _last = null; }), handledEventsToo: true);
    }

    private static void OnMove(object sender, MouseEventArgs e)
    {
        if (sender is not ContextMenu menu) return;

        var now = menu.PointToScreen(e.GetPosition(menu));
        var before = _last;
        _last = now;

        // the entry whose submenu is open, and that submenu (on screen)
        var open = menu.Items.OfType<MenuItem>().FirstOrDefault(i => i.IsSubmenuOpen);
        if (open is null || SubmenuBounds(open) is not { } submenu || before is not { } from)
        {
            Release();
            return;
        }

        // on the submenu already, or back on its own entry going up / down / left: everything as usual
        if (submenu.Contains(now) || (ScreenBounds(open).Contains(now) && now.X <= from.X))
        {
            Release();
            return;
        }

        // heading for the submenu: moving towards its side, inside the triangle (where the pointer was, the submenu's two near corners)
        var towards = submenu.Left >= from.X
            ? now.X > from.X && InTriangle(now, from, submenu.TopLeft, submenu.BottomLeft)
            : now.X < from.X && InTriangle(now, from, submenu.TopRight, submenu.BottomRight);   // (a submenu opened to the left)

        if (towards)
            Hold(menu, open);
        else
            Release();
    }

    private static IInputElement? _capturedBefore;   // who had the mouse before the hold (the menu itself): it gets it back

    /// <summary>
    /// The pointer stays with the open entry (and its submenu) until it pauses: WPF closes a submenu the moment the pointer
    /// leaves its entry for anything but the submenu, even the menu's empty space. Capturing the mouse on the entry (its whole
    /// subtree, the submenu included) means the pointer never "leaves" it on the way.
    /// </summary>
    private static void Hold(ContextMenu menu, MenuItem open)
    {
        if (!ReferenceEquals(Mouse.Captured, open))
        {
            _capturedBefore = Mouse.Captured;
            Mouse.Capture(open, CaptureMode.SubTree);
        }

        _menu = menu;

        _timer ??= new DispatcherTimer(DispatcherPriority.Input) { Interval = Pause };
        _timer.Tick -= OnPause;
        _timer.Tick += OnPause;
        _timer.Stop();
        _timer.Start();
    }

    private static void OnPause(object? sender, EventArgs e) => Release();

    /// <summary>Every entry reacts to the pointer again; the one under a resting pointer is highlighted at once.</summary>
    private static void Release()
    {
        _timer?.Stop();
        if (_menu is not { } menu) return;

        _menu = null;

        // the mouse back to the menu (it closes itself if it loses the mouse to anything outside it)
        var back = _capturedBefore ?? menu;
        _capturedBefore = null;
        if (menu.IsOpen)
            Mouse.Capture(back, CaptureMode.SubTree);

        Mouse.Synchronize();   // (the pointer may be resting on an entry: it gets the pointer now, not at the next move)
    }

    /// <summary>Where an entry's open submenu is on screen (its popup's content), or null.</summary>
    private static Rect? SubmenuBounds(MenuItem item)
    {
        if (item.Template?.FindName("PART_Popup", item) is not Popup { Child: FrameworkElement content } || !content.IsVisible)
            return null;

        return ScreenBounds(content);
    }

    private static Rect ScreenBounds(FrameworkElement element)
    {
        var topLeft = element.PointToScreen(new Point(0, 0));
        var bottomRight = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
        return new Rect(topLeft, bottomRight);
    }

    /// <summary>True when <paramref name="p"/> is inside the triangle a, b, c (or on its edge).</summary>
    private static bool InTriangle(Point p, Point a, Point b, Point c)
    {
        static double Side(Point p1, Point p2, Point p3) => (p1.X - p3.X) * (p2.Y - p3.Y) - (p2.X - p3.X) * (p1.Y - p3.Y);

        var d1 = Side(p, a, b);
        var d2 = Side(p, b, c);
        var d3 = Side(p, c, a);
        var negative = d1 < 0 || d2 < 0 || d3 < 0;
        var positive = d1 > 0 || d2 > 0 || d3 > 0;
        return !(negative && positive);
    }
}
