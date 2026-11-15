using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace GameDeck.Services;

/// <summary>
/// Paints a window's title bar in the same color as its background (and uses light title text / buttons).
/// Keeps the normal Windows title bar, so dragging, snapping and resizing all behave as usual.
/// The caption color needs Windows 11; on older Windows the title bar just turns dark.
/// </summary>
public static class TitleBarStyler
{
    private const int UseImmersiveDarkMode = 20;
    private const int CaptionColor = 35;

    /// <summary>Call from the window's constructor (the native window doesn't exist yet at that point).</summary>
    public static void Apply(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            Paint(window);

            // switching between the dark and the light theme repaints the title bar of every open window
            void Repaint() => window.Dispatcher.BeginInvoke(() => Paint(window));
            ThemeService.Changed += Repaint;
            window.Closed += (_, _) => ThemeService.Changed -= Repaint;
        };
    }

    private static void Paint(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;

            var dark = ThemeService.Current == AppTheme.Dark ? 1 : 0;   // light or dark title text and buttons
            DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref dark, sizeof(int));

            if (window.Background is SolidColorBrush { Color: var c })
            {
                var colorRef = c.R | (c.G << 8) | (c.B << 16);   // Windows wants 0x00BBGGRR
                DwmSetWindowAttribute(handle, CaptionColor, ref colorRef, sizeof(int));
            }
        }
        catch
        {
            // purely cosmetic: never let it break opening a window
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
