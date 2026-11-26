using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GameDeck.Services;

namespace GameDeck.Views;

public partial class BigPictureWindow
{
    // ---------- the controller shown on screen, and the button hints ----------

    private void UpdateClock() => ClockText.Text = DateTime.Now.ToString("t");

    private void UpdateControllerUi()
    {
        StatusText.Text = !_pad.Available
            ? "Controller support isn't available. The keyboard works."
            : _pad.ActiveKind == ControllerKind.None
                ? "No controller found. Connect one, or use the keyboard."
                : _pad.ActiveName;
        UpdateBattery();   // (another controller: its battery)

        BuildMainHints();
        BuildOverlayHints();
        BuildTabs();
    }

    // built once: the labels are asked for every time a hint bar, a tab row or a panel is drawn
    private static readonly ButtonLabels PlayStationLabels = new("✕", "○", "□", "△", "L1", "R1", "L2", "R2", "Share", "Options", "PS");
    private static readonly ButtonLabels NintendoLabels = new("A", "B", "X", "Y", "L", "R", "ZL", "ZR", "−", "+", "Home");
    private static readonly ButtonLabels XboxLabels = new("A", "B", "X", "Y", "LB", "RB", "LT", "RT", "View", "Menu", "Xbox");
    // no controller: the keys of the main screen (PgUp / PgDn move the text cursor in the search box, see Window_PreviewKeyDown)
    private static readonly ButtonLabels KeyboardLabels = new("Enter", "Esc", "F", "S", "PgUp", "PgDn", "PgUp", "PgDn", "Tab", "M", "P");

    // the same with the confirm and back buttons swapped (the keyboard is never swapped)
    private static readonly ButtonLabels PlayStationSwapped = PlayStationLabels with { Confirm = PlayStationLabels.Back, Back = PlayStationLabels.Confirm };
    private static readonly ButtonLabels NintendoSwapped = NintendoLabels with { Confirm = NintendoLabels.Back, Back = NintendoLabels.Confirm };
    private static readonly ButtonLabels XboxSwapped = XboxLabels with { Confirm = XboxLabels.Back, Back = XboxLabels.Confirm };

    /// <summary>The controller the hints are drawn for; none while the keyboard or mouse was used last.</summary>
    private ControllerKind ShownKind => _padInUse ? _pad.ActiveKind : ControllerKind.None;

    private ButtonLabels Labels() => LabelsFor(ShownKind);

    /// <summary>The button names of a controller family (the keyboard's keys for None), with confirm and back swapped if set.</summary>
    private static ButtonLabels LabelsFor(ControllerKind kind)
    {
        var swap = SettingsService.SwapConfirmBack;

        return kind switch
        {
            ControllerKind.PlayStation => swap ? PlayStationSwapped : PlayStationLabels,
            ControllerKind.Nintendo => swap ? NintendoSwapped : NintendoLabels,
            ControllerKind.Xbox or ControllerKind.Generic => swap ? XboxSwapped : XboxLabels,
            _ => KeyboardLabels
        };
    }

    private static void FillHints(Panel panel, params (string Button, string Action)[] hints)
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

    /// <summary>
    /// The bar at the bottom: Power on the left (the controller's Home button), then the game menu, Select and Back on the right,
    /// each with a round button in this screen's own colors.
    /// </summary>
    private void BuildMainHints()
    {
        var l = Labels();
        var onController = ShownKind != ControllerKind.None;

        FillBar(HintsLeft, (HomeIcon(l, onController), "POWER"));

        var items = new List<(UIElement Icon, string Text)>();

        if (IsGamesTab && !_topFocus && !_stripFocus)
            items.Add((onController ? OptionsIcon() : Chip(l.Start), "GAME MENU"));

        if (_tab == StatsTab && !_topFocus)
            items.Add((RoundButton(l.X, onController), "REFRESH"));

        if (_tab == MediaTab && !_topFocus)
        {
            items.Add((RoundButton(l.Y, onController), "GAME PAGE"));
            items.Add((RoundButton(l.X, onController), "SHOW IN FOLDER"));
            items.Add((RoundButton(l.Confirm, onController), "VIEW"));
        }
        else if (_tab != StatsTab || _topFocus)
        {
            items.Add((RoundButton(l.Confirm, onController), "SELECT"));
        }

        items.Add((RoundButton(l.Back, onController), "BACK"));

        FillBar(HintsPanel, items.ToArray());
    }

    private static void FillBar(Panel panel, params (UIElement Icon, string Text)[] items)
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

    /// <summary>A round button in the screen's dark key color with the controller's label ("A", "B", "✕", "○")...; the keyboard key when there is no controller.</summary>
    private static UIElement RoundButton(string text, bool onController) =>
        onController ? Round(new TextBlock { Text = text, FontSize = 18 }) : Chip(text);

    private static Border Round(TextBlock content)
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

    /// <summary>The Home button of the controller in use (or of <paramref name="kind"/>): the Xbox button, the PS button, or Home (a house).</summary>
    private UIElement HomeIcon(ButtonLabels labels, bool onController, ControllerKind? kind = null)
    {
        if (!onController)
            return Chip(labels.Home);

        return (kind ?? ShownKind) switch
        {
            ControllerKind.Xbox => Logo(XboxLogo, 34),
            ControllerKind.PlayStation => Logo(PlayStationLogo, 30),
            _ => Round(new TextBlock { Text = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 17 })
        };
    }

    // The Xbox and PlayStation marks as vector shapes (24 x 24, from the Simple Icons set, CC0), so they stay sharp at any size.
    // "F1" = the usual (non-zero) fill rule of SVG, which WPF doesn't use by default.
    private static readonly Geometry XboxLogo = FrozenGeometry(
        "F1 M4.102 21.033C6.211 22.881 8.977 24 12 24c3.026 0 5.789-1.119 7.902-2.967 1.877-1.912-4.316-8.709-7.902-11.417-3.582 2.708-9.779 9.505-7.898 11.417zm11.16-14.406c2.5 2.961 7.484 10.313 6.076 12.912C23.002 17.48 24 14.861 24 12.004c0-3.34-1.365-6.362-3.57-8.536 0 0-.027-.022-.082-.042-.063-.022-.152-.045-.281-.045-.592 0-1.985.434-4.805 3.246zM3.654 3.426c-.057.02-.082.041-.086.042C1.365 5.642 0 8.664 0 12.004c0 2.854.998 5.473 2.661 7.533-1.401-2.605 3.579-9.951 6.08-12.91-2.82-2.813-4.216-3.245-4.806-3.245-.131 0-.223.021-.281.046v-.002zM12 3.551S9.055 1.828 6.755 1.746c-.903-.033-1.454.295-1.521.339C7.379.646 9.659 0 11.984 0H12c2.334 0 4.605.646 6.766 2.085-.068-.046-.615-.372-1.52-.339C14.946 1.828 12 3.545 12 3.545v.006z");

    private static readonly Geometry PlayStationLogo = FrozenGeometry(
        "F1 M8.984 2.596v17.547l3.915 1.261V6.688c0-.69.304-1.151.794-.991.636.18.76.814.76 1.505v5.875c2.441 1.193 4.362-.002 4.362-3.152 0-3.237-1.126-4.675-4.438-5.827-1.307-.448-3.728-1.186-5.39-1.502zm4.656 16.241l6.296-2.275c.715-.258.826-.625.246-.818-.586-.192-1.637-.139-2.357.123l-4.205 1.5V14.98l.24-.085s1.201-.42 2.913-.615c1.696-.18 3.785.03 5.437.661 1.848.601 2.04 1.472 1.576 2.072-.465.6-1.622 1.036-1.622 1.036l-8.544 3.107V18.86zM1.807 18.6c-1.9-.545-2.214-1.668-1.352-2.32.801-.586 2.16-1.052 2.16-1.052l5.615-2.013v2.313L4.205 17c-.705.271-.825.632-.239.826.586.195 1.637.15 2.343-.12L8.247 17v2.074c-.12.03-.256.044-.39.073-1.939.331-3.996.196-6.038-.479z");

    private static Geometry FrozenGeometry(string data)
    {
        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }

    private static UIElement Logo(Geometry shape, double size) => new System.Windows.Shapes.Path
    {
        Data = shape, Fill = ThemeService.TextPrimary, Stretch = Stretch.Uniform, Width = size, Height = size,
        VerticalAlignment = VerticalAlignment.Center
    };

    /// <summary>The controller's Menu / Options button: three lines.</summary>
    private static UIElement OptionsIcon() => new TextBlock
    {
        Text = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), Foreground = ThemeService.TextPrimary, FontSize = 30,
        VerticalAlignment = VerticalAlignment.Center
    };

    /// <summary>
    /// The hints of the filters panel, and the buttons drawn inside the on-screen keyboard's keys (it has no hint row: each key
    /// shows its own button). The Power menu needs none.
    /// </summary>
    private void BuildOverlayHints()
    {
        var l = Labels();

        UpdateKeyboardSelection();   // the keys' buttons follow the controller in use
        ShowCaretHints();            // (and the text cursor's)
        FillHints(FilterHints, (l.Confirm, "Choose"), (l.Back, "Close"));
    }

    /// <summary>
    /// The theme changed. What the XAML paints follows by itself; what this screen built in code (tabs, hints, keyboard, stats,
    /// the open settings list) holds the old colors, so it is built again.
    /// </summary>
    private void OnThemeChanged()
    {
        BuildTabs();
        BuildMainHints();
        BuildOverlayHints();
        UpdateTopBar();

        BuildKeyboard();
        UpdateKeyboardSelection();

        if (_tab == StatsTab)
            BuildStats();

        if (_tab == MediaTab)
        {
            ClearMedia();   // the grid's cells were built in the old colors
            BuildMedia();
        }

        if (_overlay == Overlay.Settings)
            RenderSettings();
    }

    private void BuildTabs()
    {
        var l = Labels();
        TabsPanel.Children.Clear();

        TabsPanel.Children.Add(Chip(l.Left));

        for (var i = 0; i < TabNames.Length; i++)
        {
            var index = i;
            var selected = i == _tab;

            var tab = new Border
            {
                Margin = new Thickness(22, 0, 0, 0),
                Padding = new Thickness(0, 4, 0, 6),
                BorderThickness = new Thickness(0, 0, 0, 3),
                BorderBrush = selected ? Purple : Brushes.Transparent,
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
                Child = new TextBlock
                {
                    Text = TabNames[i],
                    FontSize = 24,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = selected ? ThemeService.TextPrimary : Muted
                }
            };

            // under the mouse a tab that isn't the open one lights up: brighter text and a grey underline
            if (!selected)
            {
                var label = (TextBlock)tab.Child;

                tab.MouseEnter += (_, _) =>
                {
                    label.Foreground = ThemeService.TextPrimary;
                    tab.BorderBrush = Muted;
                };

                tab.MouseLeave += (_, _) =>
                {
                    label.Foreground = Muted;
                    tab.BorderBrush = Brushes.Transparent;
                };
            }

            tab.MouseLeftButtonUp += (_, _) =>
            {
                if (_overlay != Overlay.None) return;

                _tab = index;
                ApplyTab(preserveSelection: false);
            };

            TabsPanel.Children.Add(tab);
        }

        var right = Chip(l.Right);
        right.Margin = new Thickness(22, 0, 0, 0);
        TabsPanel.Children.Add(right);
    }

    /// <summary>A small rounded label like a button cap: "A", "✕", "LB"...</summary>
    private static Border Chip(string text) => new()
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

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
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
