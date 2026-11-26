using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GameDeck.Services;
using static GameDeck.Views.BigPictureLook;

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
                : _padInUse
                    ? _pad.ActiveName
                    : "Keyboard and mouse";   // (a controller is connected, but the keyboard or mouse was used last)
        InputGlyph.Text = _pad.ActiveKind != ControllerKind.None && !_padInUse ? "" : "";   // a keyboard / a controller
        UpdateBattery();   // (another controller: its battery)

        BuildMainHints();
        BuildOverlayHints();
        BuildTabs();
    }

    /// <summary>The controller the hints are drawn for; none while the keyboard or mouse was used last.</summary>
    private ControllerKind ShownKind => _padInUse ? _pad.ActiveKind : ControllerKind.None;

    /// <summary>The button names of what is in use now (the controller, or the keyboard).</summary>
    private ButtonLabels Labels() => ButtonLabels.For(ShownKind);

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

        if (_stripFocus && _shelves.Count > 1)
            items.Add((RoundButton(l.X, onController), "NEXT SHELF"));

        if (_tab == StatsTab && !_topFocus)
            items.Add((RoundButton(l.X, onController), "REFRESH"));

        // LT / RT page through the Stats and Media tabs when they are longer than the screen (known once laid out)
        if (_tab is StatsTab or MediaTab)
        {
            QueueHintRefresh();
            if (Scrollable(_tab == StatsTab ? StatsScroll : MediaScroll))
                items.Add((Chip($"{l.LeftTrigger} / {l.RightTrigger}"), "SCROLL"));
        }

        if (_tab == MediaTab && !_topFocus)
        {
            items.Add((Chip(l.Select), "BY GAME"));
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

    /// <summary>
    /// The hints of the filters panel, and the buttons drawn inside the on-screen keyboard's keys (it has no hint row: each key
    /// shows its own button). The Power menu needs none.
    /// </summary>
    private void BuildOverlayHints()
    {
        var l = Labels();

        UpdateKeyboardSelection();   // the keys' buttons follow the controller in use
        ShowCaretHints();            // (and the text cursor's)
        FillFilterHints();
    }

    private void FillFilterHints()
    {
        var l = Labels();
        var hints = new List<(string, string)> { (l.Confirm, "Choose"), (l.Back, "Close") };
        AddScrollHint(hints, Scrollable(FilterScroll));
        FillHints(FilterHints, hints.ToArray());
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
                BorderBrush = selected ? Accent : Brushes.Transparent,
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
}
