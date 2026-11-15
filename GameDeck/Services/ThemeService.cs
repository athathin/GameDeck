using System.Windows;
using System.Windows.Media;

namespace GameDeck.Services;

/// <summary>The color schemes to choose from.</summary>
public enum AppTheme
{
    Dark,
    Light
}

/// <summary>
/// The colors of the whole app, in one place. Every screen refers to these brushes by name (XAML: {DynamicResource TextMuted},
/// code: ThemeService.TextMuted). Changing the theme only changes the color inside the same brush objects, so every screen that
/// is open (and every one opened later) switches at once, with nothing to rebuild.
/// </summary>
public static class ThemeService
{
    private static readonly Dictionary<string, SolidColorBrush> Brushes = [];

    // name -> dark color, light color
    private static readonly (string Key, string Dark, string Light)[] Palette =
    [
        ("AppBg", "#171A21", "#F2F4F8"),          // window background
        ("PanelBg", "#1E222B", "#FFFFFF"),        // cards and panels
        ("RaisedBg", "#2A2F3A", "#E4E8EF"),       // buttons, keys, borders
        ("HoverBg", "#3A4050", "#D3D9E3"),        // the same, under the mouse
        ("ScrollbarHover", "#4C566A", "#B7BFCC"),
        ("TextPrimary", "#FFFFFF", "#151922"),    // main text
        ("TextSecondary", "#C5CBD6", "#3A4352"),
        ("TextMuted", "#8A91A0", "#667085"),
        ("TextHint", "#6F7685", "#8A93A3"),
        ("TextDisabled", "#5A6070", "#A5ADBB"),
        ("SelectBg", "#FFFFFF", "#1E222B"),       // the selected row or key in Big Picture
        ("SelectFg", "#171A21", "#FFFFFF"),
        ("Scrim", "#B30E1016", "#B3E8ECF3"),      // what dims the screen behind a panel
        ("ScrimStrong", "#D90E1016", "#D9E8ECF3")
    ];

    public static AppTheme Current { get; private set; } = AppTheme.Dark;

    /// <summary>Raised after the colors changed (windows that paint things themselves, such as the title bar, listen).</summary>
    public static event Action? Changed;

    /// <summary>Creates the brushes and puts them where XAML can find them. Call once, before any window is created.</summary>
    public static void Initialize(Application app, AppTheme theme)
    {
        Current = theme;

        foreach (var (key, dark, light) in Palette)
        {
            var brush = new SolidColorBrush(Parse(theme == AppTheme.Dark ? dark : light));
            Brushes[key] = brush;
            app.Resources[key] = brush;   // (not frozen: the color changes later)
        }
    }

    public static void Apply(AppTheme theme)
    {
        if (theme == Current) return;
        Current = theme;

        foreach (var (key, dark, light) in Palette)
            Brushes[key].Color = Parse(theme == AppTheme.Dark ? dark : light);

        Changed?.Invoke();
    }

    public static SolidColorBrush AppBg => Brushes["AppBg"];
    public static SolidColorBrush PanelBg => Brushes["PanelBg"];
    public static SolidColorBrush RaisedBg => Brushes["RaisedBg"];
    public static SolidColorBrush HoverBg => Brushes["HoverBg"];
    public static SolidColorBrush TextPrimary => Brushes["TextPrimary"];
    public static SolidColorBrush TextSecondary => Brushes["TextSecondary"];
    public static SolidColorBrush TextMuted => Brushes["TextMuted"];
    public static SolidColorBrush SelectBg => Brushes["SelectBg"];
    public static SolidColorBrush SelectFg => Brushes["SelectFg"];

    private static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);
}
