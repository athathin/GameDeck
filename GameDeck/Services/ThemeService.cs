using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace GameDeck.Services;

/// <summary>The color schemes to choose from.</summary>
public enum AppTheme
{
    Dark,
    Light,
    System   // whatever Windows is set to (and it follows when Windows changes)
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
    private static bool _watchingWindows;

    // name -> dark color, light color
    private static readonly (string Key, string Dark, string Light)[] Palette =
    [
        ("AppBg", "#171A21", "#F2F4F8"),          // window background
        ("PanelBg", "#1E222B", "#FFFFFF"),        // cards and panels
        ("RaisedBg", "#2A2F3A", "#E4E8EF"),       // buttons, keys, borders
        ("HoverBg", "#3A4050", "#D3D9E3"),        // the same, under the mouse
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

    /// <summary>The accent colors to choose from (Settings › Appearance): the first is the default. White text reads well on each.</summary>
    public static readonly (string Name, string Hex)[] Accents =
    [
        ("Purple", "#8037FE"), ("Blue", "#2F6FED"), ("Teal", "#0E9488"), ("Green", "#1E9E57"),
        ("Orange", "#E8701A"), ("Red", "#D93B3B"), ("Pink", "#D63A84")
    ];

    /// <summary>The accent in use ("#8037FE"): a choice of <see cref="Accents"/>, the same in both themes.</summary>
    public static string AccentHex { get; private set; } = Accents[0].Hex;

    /// <summary>What the user chose (Dark, Light or System).</summary>
    public static AppTheme Setting { get; private set; } = AppTheme.Dark;

    /// <summary>The colors in use right now: always Dark or Light (System has been resolved).</summary>
    public static AppTheme Current { get; private set; } = AppTheme.Dark;

    /// <summary>Raised after the colors changed: parts of the screen that were painted in code redraw themselves (and the title bars repaint).</summary>
    public static event Action? Changed;

    /// <summary>Creates the brushes and puts them where XAML can find them. Call once, before any window is created.</summary>
    public static void Initialize(Application app, AppTheme theme, string? accent = null)
    {
        _app = app;
        Setting = theme;
        Current = Resolve(theme);
        AccentHex = KnownAccent(accent);
        Publish();

        // "follow Windows": when the user changes Windows' own light / dark setting, the colors follow
        if (!_watchingWindows)
        {
            _watchingWindows = true;

            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (Setting == AppTheme.System && e.Category == UserPreferenceCategory.General)
                    app.Dispatcher.BeginInvoke(() => Apply(AppTheme.System));
            };
        }
    }

    public static void Apply(AppTheme theme)
    {
        Setting = theme;

        var colors = Resolve(theme);
        if (colors == Current) return;

        Current = colors;
        Publish();
        Changed?.Invoke();
    }

    /// <summary>Changes the accent color everywhere (one of <see cref="Accents"/>, by its hex).</summary>
    public static void ApplyAccent(string hex)
    {
        var accent = KnownAccent(hex);
        if (accent == AccentHex) return;

        AccentHex = accent;
        Publish();
        Changed?.Invoke();
    }

    /// <summary>The accent as given when it's one of the choices, otherwise the default (a settings file edited by hand).</summary>
    public static string KnownAccent(string? hex) =>
        Accents.FirstOrDefault(a => a.Hex.Equals(hex, StringComparison.OrdinalIgnoreCase)).Hex ?? Accents[0].Hex;

    /// <summary>Dark or Light for a choice: System asks Windows (apps use the dark theme when it says so; dark if unknown).</summary>
    private static AppTheme Resolve(AppTheme theme)
    {
        if (theme != AppTheme.System) return theme;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light != 0 ? AppTheme.Light : AppTheme.Dark;
        }
        catch
        {
            return AppTheme.Dark;
        }
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

        // the accent: the same in both themes ("AccentBadge" is the see-through one of the status badges on the covers)
        var accent = Parse(AccentHex);
        brushes["Accent"] = Frozen(accent);
        brushes["AccentBadge"] = Frozen(Color.FromArgb(0xCC, accent.R, accent.G, accent.B));
        // (a little lighter: the Play button under the mouse)
        brushes["AccentHover"] = Frozen(Color.FromRgb(Lighter(accent.R), Lighter(accent.G), Lighter(accent.B)));
        static byte Lighter(byte c) => (byte)Math.Round(c + (255 - c) * 0.18);

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
    public static SolidColorBrush Accent => _brushes["Accent"];
    public static Color AccentColor => Accent.Color;

    private static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
