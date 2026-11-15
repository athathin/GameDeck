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
/// code: ThemeService.TextMuted). Changing the theme puts a new set of brushes under the same names: WPF freezes a brush once a
/// style uses it (so its color can't be edited afterwards), but screens that use {DynamicResource} pick up the replacement by
/// themselves. Parts drawn in code listen to <see cref="Changed"/> and redraw.
/// </summary>
public static class ThemeService
{
    private static Dictionary<string, SolidColorBrush> _brushes = [];
    private static Application? _app;

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

    /// <summary>Raised after the colors changed: parts of the screen that were painted in code redraw themselves (and the title bars repaint).</summary>
    public static event Action? Changed;

    /// <summary>Creates the brushes and puts them where XAML can find them. Call once, before any window is created.</summary>
    public static void Initialize(Application app, AppTheme theme)
    {
        _app = app;
        Current = theme;
        Publish();
    }

    public static void Apply(AppTheme theme)
    {
        if (theme == Current) return;

        Current = theme;
        Publish();
        Changed?.Invoke();
    }

    /// <summary>Builds the brushes of the current theme and replaces the old ones under the same names.</summary>
    private static void Publish()
    {
        var brushes = new Dictionary<string, SolidColorBrush>(Palette.Length);

        foreach (var (key, dark, light) in Palette)
        {
            var brush = new SolidColorBrush(Parse(Current == AppTheme.Dark ? dark : light));
            brush.Freeze();   // never edited: a new one replaces it
            brushes[key] = brush;
        }

        _brushes = brushes;

        // (replacing a resource tells every {DynamicResource} user to look it up again)
        foreach (var (key, brush) in brushes)
            _app!.Resources[key] = brush;
    }

    public static SolidColorBrush AppBg => _brushes["AppBg"];
    public static SolidColorBrush PanelBg => _brushes["PanelBg"];
    public static SolidColorBrush RaisedBg => _brushes["RaisedBg"];
    public static SolidColorBrush HoverBg => _brushes["HoverBg"];
    public static SolidColorBrush TextPrimary => _brushes["TextPrimary"];
    public static SolidColorBrush TextSecondary => _brushes["TextSecondary"];
    public static SolidColorBrush TextMuted => _brushes["TextMuted"];
    public static SolidColorBrush SelectBg => _brushes["SelectBg"];
    public static SolidColorBrush SelectFg => _brushes["SelectFg"];

    private static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);
}
