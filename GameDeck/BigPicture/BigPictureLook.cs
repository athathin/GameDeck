using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using GameDeck.Services;

namespace GameDeck.Views;

/// <summary>
/// Big Picture's look: its colors and the small pieces it draws in code (button chips, round buttons, the controllers' logos,
/// hint bars, switches). Nothing here knows about the screen's state, so every part of Big Picture can use it
/// (<c>using static GameDeck.Views.BigPictureLook;</c>).
/// </summary>
internal static class BigPictureLook
{
    // ---------- colors ----------

    // Frozen brushes are cheaper for WPF (no change tracking) and can be shared by every element that uses them
    public static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public static readonly SolidColorBrush Warning = Frozen(0xF0, 0x7A, 0x5A);   // a battery that is nearly empty

    // the rest follow the theme (these are the shared brushes of ThemeService: their color changes with the theme)
    public static SolidColorBrush Accent => ThemeService.Accent;            // the accent color chosen in Settings (purple at first)
    public static SolidColorBrush TextGrey => ThemeService.TextSecondary;
    public static SolidColorBrush Dark => ThemeService.SelectFg;            // text on the selected row
    public static SolidColorBrush KeyBackground => ThemeService.RaisedBg;
    public static SolidColorBrush Muted => ThemeService.TextMuted;
    public static SolidColorBrush Divider => ThemeService.AppBg;

    // ---------- buttons and hints ----------

    /// <summary>A small rounded label like a button cap: "A", "✕", "LB"...</summary>
    public static Border Chip(string text) => new()
    {
        Background = KeyBackground,
        CornerRadius = new CornerRadius(12),
        Padding = new Thickness(11, 3, 11, 3),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = text, Foreground = ThemeService.TextPrimary, FontSize = 15, FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        }
    };

    /// <summary>A round button in the screen's dark key color with the controller's label ("A", "B", "✕", "○")...; the keyboard key when there is no controller.</summary>
    public static UIElement RoundButton(string text, bool onController) =>
        onController ? Round(new TextBlock { Text = text, FontSize = 18 }) : Chip(text);

    public static Border Round(TextBlock content)
    {
        content.Foreground = ThemeService.TextPrimary;
        content.FontWeight = FontWeights.Bold;
        content.HorizontalAlignment = HorizontalAlignment.Center;
        content.VerticalAlignment = VerticalAlignment.Center;

        return new Border
        {
            Width = 34, Height = 34, CornerRadius = new CornerRadius(17), Background = KeyBackground,
            VerticalAlignment = VerticalAlignment.Center, Child = content
        };
    }

    /// <summary>The controller's Menu / Options button: three lines.</summary>
    public static UIElement OptionsIcon() => new TextBlock
    {
        Text = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), Foreground = ThemeService.TextPrimary, FontSize = 30,
        VerticalAlignment = VerticalAlignment.Center
    };

    // The Xbox and PlayStation marks as vector shapes (24 x 24, from the Simple Icons set, CC0), so they stay sharp at any size.
    // "F1" = the usual (non-zero) fill rule of SVG, which WPF doesn't use by default.
    public static readonly Geometry XboxLogo = FrozenGeometry(
        "F1 M4.102 21.033C6.211 22.881 8.977 24 12 24c3.026 0 5.789-1.119 7.902-2.967 1.877-1.912-4.316-8.709-7.902-11.417-3.582 2.708-9.779 9.505-7.898 11.417zm11.16-14.406c2.5 2.961 7.484 10.313 6.076 12.912C23.002 17.48 24 14.861 24 12.004c0-3.34-1.365-6.362-3.57-8.536 0 0-.027-.022-.082-.042-.063-.022-.152-.045-.281-.045-.592 0-1.985.434-4.805 3.246zM3.654 3.426c-.057.02-.082.041-.086.042C1.365 5.642 0 8.664 0 12.004c0 2.854.998 5.473 2.661 7.533-1.401-2.605 3.579-9.951 6.08-12.91-2.82-2.813-4.216-3.245-4.806-3.245-.131 0-.223.021-.281.046v-.002zM12 3.551S9.055 1.828 6.755 1.746c-.903-.033-1.454.295-1.521.339C7.379.646 9.659 0 11.984 0H12c2.334 0 4.605.646 6.766 2.085-.068-.046-.615-.372-1.52-.339C14.946 1.828 12 3.545 12 3.545v.006z");

    public static readonly Geometry PlayStationLogo = FrozenGeometry(
        "F1 M8.984 2.596v17.547l3.915 1.261V6.688c0-.69.304-1.151.794-.991.636.18.76.814.76 1.505v5.875c2.441 1.193 4.362-.002 4.362-3.152 0-3.237-1.126-4.675-4.438-5.827-1.307-.448-3.728-1.186-5.39-1.502zm4.656 16.241l6.296-2.275c.715-.258.826-.625.246-.818-.586-.192-1.637-.139-2.357.123l-4.205 1.5V14.98l.24-.085s1.201-.42 2.913-.615c1.696-.18 3.785.03 5.437.661 1.848.601 2.04 1.472 1.576 2.072-.465.6-1.622 1.036-1.622 1.036l-8.544 3.107V18.86zM1.807 18.6c-1.9-.545-2.214-1.668-1.352-2.32.801-.586 2.16-1.052 2.16-1.052l5.615-2.013v2.313L4.205 17c-.705.271-.825.632-.239.826.586.195 1.637.15 2.343-.12L8.247 17v2.074c-.12.03-.256.044-.39.073-1.939.331-3.996.196-6.038-.479z");

    private static Geometry FrozenGeometry(string data)
    {
        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }

    public static UIElement Logo(Geometry shape, double size) => new Path
    {
        Data = shape, Fill = ThemeService.TextPrimary, Stretch = Stretch.Uniform, Width = size, Height = size,
        VerticalAlignment = VerticalAlignment.Center
    };

    /// <summary>A row of hints in a panel: a chip with the button, then what it does.</summary>
    public static void FillHints(Panel panel, params (string Button, string Action)[] hints)
    {
        panel.Children.Clear();

        foreach (var (button, action) in hints)
        {
            panel.Children.Add(Chip(button));
            panel.Children.Add(new TextBlock
            {
                Text = action, Foreground = TextGrey, FontSize = 16, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 30, 0)
            });
        }
    }

    /// <summary>The bar at the bottom of the screen: each button's icon, then what it does in capitals.</summary>
    public static void FillBar(Panel panel, params (UIElement Icon, string Text)[] items)
    {
        panel.Children.Clear();

        foreach (var (icon, text) in items)
        {
            panel.Children.Add(icon);
            panel.Children.Add(new TextBlock
            {
                Text = text, Foreground = ThemeService.TextPrimary, FontSize = 16, FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 34, 0)
            });
        }

        // no gap after the last one, so the bar lines up with the screen's edge
        if (panel.Children.Count > 0 && panel.Children[^1] is TextBlock last)
            last.Margin = new Thickness(12, 0, 0, 0);
    }

    // ---------- controls of the settings ----------

    /// <summary>The dark box of a choice or a button (the same in a selected card).</summary>
    public static Border ControlBox(UIElement content) => new()
    {
        Background = Divider, CornerRadius = new CornerRadius(4), Padding = new Thickness(18, 9, 18, 9), Width = 300,
        VerticalAlignment = VerticalAlignment.Center, Child = content
    };

    /// <summary>An on / off switch: the knob on the right and the accent color when on.</summary>
    public static Border Switch(bool on) => new()
    {
        Width = 56, Height = 30, CornerRadius = new CornerRadius(15), VerticalAlignment = VerticalAlignment.Center,
        Background = on ? Accent : Muted, Opacity = on ? 1 : 0.75,
        Child = new Ellipse
        {
            Width = 22, Height = 22, Fill = Brushes.White, Margin = new Thickness(4, 0, 4, 0),
            HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left
        }
    };

    // ---------- the visual tree ----------

    /// <summary>The first element of type <typeparamref name="T"/> inside <paramref name="parent"/> (depth first), or null.</summary>
    public static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;

            var deeper = FindChild<T>(child);
            if (deeper is not null) return deeper;
        }

        return null;
    }
}
