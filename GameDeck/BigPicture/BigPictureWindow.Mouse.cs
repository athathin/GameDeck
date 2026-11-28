using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using GameDeck.Services;
using static GameDeck.Views.BigPictureLook;

namespace GameDeck.Views;

public partial class BigPictureWindow
{
    // ---------- the mouse everywhere: nothing on this screen needs a controller or a keyboard ----------
    //  - every hint (the bar at the bottom, the hints under each panel, the LB / RB beside the tabs, the text cursor's LT / RT)
    //    can be clicked: it does what its button does (a HintKey on the element, see BigPictureLook)
    //  - a right click, or the mouse's own Back button, is Back: out of a submenu, a panel, the keyboard
    //  - a click on the dimmed screen around a panel closes the panel

    /// <summary>A button pressed with the mouse (a hint was clicked): the same as on the controller, but the pointer stays.</summary>
    private void PressFromMouse(PadButton button)
    {
        if (WakeFromScreenSaver()) return;

        SetPadInUse(false);
        Handle(button, fromMouse: true);
    }

    /// <summary>The controller button a clicked hint stands for (by its name as shown: "A", "Esc", "LB"...).</summary>
    private PadButton? ButtonOf(HintKey key)
    {
        if (key.Button is { } button) return button;

        var l = Labels();
        var label = key.Label;

        if (label == l.Confirm) return PadButton.Confirm;
        if (label == l.Back) return PadButton.Back;
        if (label == l.X) return PadButton.X;
        if (label == l.Y) return PadButton.Y;
        if (label == l.Start) return PadButton.Start;
        if (label == l.Select) return PadButton.Select;
        if (label == l.Home) return PadButton.Home;
        if (label == l.Left) return PadButton.LeftShoulder;
        if (label == l.Right) return PadButton.RightShoulder;
        if (label == l.LeftTrigger) return PadButton.LeftTrigger;
        if (label == l.RightTrigger) return PadButton.RightTrigger;

        return label switch { "←" or "◀" => PadButton.Left, "→" or "▶" => PadButton.Right, "Del" => PadButton.X, _ => null };
    }

    /// <summary>A left click that nothing else took: on a hint it presses that hint's button; on the dimmed screen around a panel it closes the panel.</summary>
    private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;

        // a hint, or something inside one
        for (var element = source; element is not null && !ReferenceEquals(element, this); element = ParentOf(element))
        {
            if (element is FrameworkElement { Tag: HintKey key })
            {
                if (ButtonOf(key) is { } button)
                    PressFromMouse(button);

                e.Handled = true;
                return;
            }
        }

        // the dimmed screen itself (not the panel on it)
        if (ReferenceEquals(source, OverlayGrid(_overlay)))
        {
            e.Handled = true;

            if (_overlay == Overlay.GameMenu)
                CloseGameMenu();   // all of it, not just one submenu
            else if (_overlay == Overlay.Browse)
                CloseBrowse();     // (Back would go up a folder)
            else
                PressFromMouse(PadButton.Back);
        }
    }

    /// <summary>The element behind a panel (dimmed): clicking on it closes the panel. Null for full-screen pages (settings, a game's page).</summary>
    private FrameworkElement? OverlayGrid(Overlay overlay) => overlay switch
    {
        Overlay.Search => SearchOverlay,
        Overlay.Filters => FilterOverlay,
        Overlay.GameMenu => GameMenuOverlay,
        Overlay.Browse => BrowseOverlay,
        Overlay.Power => PowerOverlay,
        Overlay.Shortcuts => ShortcutsOverlay,
        _ => null
    };

    private static DependencyObject? ParentOf(DependencyObject element) =>
        element is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(element)
            : LogicalTreeHelper.GetParent(element);   // (text inside a TextBlock)

    /// <summary>A right click with a panel open, or the mouse's Back button anywhere: Back.</summary>
    private void Window_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        var back = e.ChangedButton == MouseButton.XButton1 ||
                   (e.ChangedButton == MouseButton.Right && _overlay != Overlay.None);   // (on the row of games it opens the game menu)
        if (!back) return;

        e.Handled = true;   // (and not also the game menu of the tile under the pointer)
        PressFromMouse(PadButton.Back);
    }
}
