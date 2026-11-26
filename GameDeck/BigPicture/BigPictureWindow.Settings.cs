using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GameDeck.Services;
using static GameDeck.Views.BigPictureLook;

namespace GameDeck.Views;

public partial class BigPictureWindow
{
    // ---------- settings: full screen, the sections on the left, the chosen section's settings on the right ----------

    private List<SettingsSection>? _settingsSections;
    private int _settingsSection;          // the section shown
    private bool _settingsInSection;       // false: the stick moves through the sections; true: through the section's settings
    private List<SettingRow> _settingsRows = [];
    private int _settingsIndex = -1;

    private List<SettingsSection> SettingsSections => _settingsSections ??=
    [
        new("General", "", GeneralSettings),
        new("Library", "", LibrarySettings),
        new("Appearance", "", AppearanceSettings),
        new("Controller", "", ControllerSettings),
        new("Sound", "", SoundSettings),
        new("Screenshots", "", ScreenshotSettings),
        new("Quick launch", "", QuickLaunchSettings),
        new("System", "", SystemSettings)
    ];

    /// <param name="keepPlace">Back from a screen opened in here (the controller's file browser): the same section and setting.</param>
    private void OpenSettings(bool keepPlace = false)
    {
        if (!keepPlace)
        {
            _settingsSection = 0;
            _settingsInSection = false;
            _settingsIndex = -1;
        }

        _capturingShortcut = false;
        ShowSettingsStatus(null);
        ShowOverlay(Overlay.Settings);
        SettingsScroll.ScrollToTop();
        RenderSettings();
    }

    private void FillSettingsHints()
    {
        var l = Labels();

        if (!_settingsInSection)
        {
            FillHints(SettingsHints, (l.Confirm, "Open"), (l.Back, "Close"));
            return;
        }

        var row = _settingsIndex >= 0 && _settingsIndex < _settingsRows.Count ? _settingsRows[_settingsIndex] : null;
        var hints = new List<(string, string)>();

        if (row?.Adjust is not null)
            hints.Add((ShownKind == ControllerKind.None ? "← →" : "◀ ▶", "Change"));
        if (row?.Activate is not null)
            hints.Add((l.Confirm, row.IsOn is not null ? "Turn on / off" : row.Button ?? "Change"));

        hints.Add((l.Back, "Back"));
        FillHints(SettingsHints, hints.ToArray());
    }

    // ---- drawing ----

    /// <summary>Draws the sections and the chosen section's settings.</summary>
    private void RenderSettings()
    {
        var sections = SettingsSections;
        _settingsSection = Math.Clamp(_settingsSection, 0, sections.Count - 1);

        RenderSettingsNav(sections);

        var section = sections[_settingsSection];
        SettingsPageTitle.Text = section.Name;
        _settingsRows = section.Rows();

        // (a row may have gone: "Start minimized" when launching with Windows was turned off)
        if (_settingsInSection && (_settingsIndex < 0 || _settingsIndex >= _settingsRows.Count || !_settingsRows[_settingsIndex].Selectable))
            _settingsIndex = NextSelectable(Math.Min(Math.Max(_settingsIndex, 0), _settingsRows.Count - 1), 1) ??
                             NextSelectable(_settingsRows.Count - 1, -1) ?? -1;

        SettingsList.Children.Clear();
        FrameworkElement? selectedCard = null;

        for (var i = 0; i < _settingsRows.Count; i++)
        {
            var row = _settingsRows[i];

            if (row.IsHeader)
            {
                SettingsList.Children.Add(new TextBlock
                {
                    Text = row.Title, Foreground = ThemeService.TextPrimary, FontSize = 20, FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(2, SettingsList.Children.Count == 0 ? 14 : 30, 0, 10)
                });
                continue;
            }

            var selected = _settingsInSection && i == _settingsIndex;
            var card = SettingCard(row, selected, i);
            SettingsList.Children.Add(card);
            if (selected) selectedCard = card;
        }

        if (selectedCard is not null && !_renderingOnHover)
            selectedCard.BringIntoView();

        FillSettingsHints();
    }

    private void RenderSettingsNav(List<SettingsSection> sections)
    {
        SettingsNav.Children.Clear();

        for (var i = 0; i < sections.Count; i++)
        {
            var index = i;
            var chosen = i == _settingsSection;
            var focused = chosen && !_settingsInSection;   // the stick is on the sections
            var foreground = focused ? Dark : chosen ? ThemeService.TextPrimary : TextGrey;

            var item = new Border
            {
                Background = focused ? ThemeService.SelectBg : chosen ? KeyBackground : Brushes.Transparent,
                Padding = new Thickness(44, 14, 20, 14), Margin = new Thickness(0, 1, 0, 1), Cursor = Cursors.Hand,
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = sections[i].Glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 21,
                            Foreground = foreground, Width = 40, VerticalAlignment = VerticalAlignment.Center
                        },
                        new TextBlock
                        {
                            Text = sections[i].Name, FontSize = 21, Foreground = foreground, VerticalAlignment = VerticalAlignment.Center,
                            FontWeight = chosen ? FontWeights.SemiBold : FontWeights.Normal
                        }
                    }
                }
            };

            item.MouseLeftButtonUp += (_, _) => ChooseSection(index);
            SettingsNav.Children.Add(item);
        }
    }

    /// <summary>One setting as a card: its name and a line about it on the left, its control on the right.</summary>
    private FrameworkElement SettingCard(SettingRow row, bool selected, int index)
    {
        var text = selected ? Dark : ThemeService.TextPrimary;

        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(new TextBlock { Text = row.Title, FontSize = 20, Foreground = text, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(row.Subtitle))
            words.Children.Add(new TextBlock
            {
                Text = row.Subtitle, FontSize = 15, Foreground = selected ? Dark : TextGrey, Opacity = selected ? 0.8 : 1,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0)
            });

        var line = new DockPanel();
        if (SettingControl(row, selected) is { } control)
        {
            DockPanel.SetDock(control, Dock.Right);
            control.Margin = new Thickness(24, 0, 0, 0);
            line.Children.Add(control);
        }
        line.Children.Add(words);

        var card = new Border
        {
            Background = selected ? ThemeService.SelectBg : KeyBackground, CornerRadius = new CornerRadius(6),
            Padding = new Thickness(22, 14, 18, 14), Margin = new Thickness(0, 0, 0, 6), MinHeight = 66,
            Cursor = row.Selectable ? Cursors.Hand : null, Child = line
        };

        if (row.Selectable)
        {
            card.MouseEnter += (_, _) =>
            {
                if (!PointerIsChoosing || _capturingShortcut || (_settingsInSection && _settingsIndex == index)) return;

                _settingsInSection = true;
                _settingsIndex = index;
                RenderOnHover(RenderSettings);
            };
            card.MouseLeftButtonUp += (_, _) =>
            {
                _settingsInSection = true;
                _settingsIndex = index;
                ActivateSettingsRow();
            };
        }

        return card;
    }

    /// <summary>What sits on the right of a card: a switch, a "‹ choice ›", a bar, a button or a value. Null: nothing.</summary>
    private FrameworkElement? SettingControl(SettingRow row, bool selected)
    {
        if (row.IsOn is not null)
            return Switch(row.IsOn());

        if (row.Steps is not null)
        {
            var (current, count) = row.Steps();
            var bar = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            for (var step = 0; step < count; step++)
                bar.Children.Add(new Border
                {
                    Width = 40, Height = 9, CornerRadius = new CornerRadius(4), Margin = new Thickness(6, 0, 0, 0),
                    Background = step <= current ? Purple : Muted, Opacity = step <= current ? 1 : 0.5
                });

            bar.Children.Add(new TextBlock
            {
                Text = row.Value?.Invoke() ?? "", FontSize = 18, Foreground = selected ? Dark : ThemeService.TextPrimary,
                Width = 64, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center
            });
            return bar;
        }

        if (row.Adjust is not null && row.Value is not null)
            return ControlBox(new DockPanel
            {
                Children =
                {
                    Arrow("", Dock.Left), Arrow("", Dock.Right),
                    new TextBlock
                    {
                        Text = row.Value(), FontSize = 18, Foreground = ThemeService.TextPrimary, TextAlignment = TextAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                }
            });

        if (row.Button is not null)
            return ControlBox(new TextBlock
            {
                Text = row.Button, FontSize = 18, Foreground = ThemeService.TextPrimary, HorizontalAlignment = HorizontalAlignment.Center
            });

        if (row.Value is not null)
            return new TextBlock
            {
                Text = row.Value(), FontSize = 18, Foreground = selected ? Dark : TextGrey, VerticalAlignment = VerticalAlignment.Center
            };

        return null;

        static TextBlock Arrow(string glyph, Dock side)
        {
            var arrow = new TextBlock
            {
                Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 14, Foreground = ThemeService.TextSecondary,
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(arrow, side);
            return arrow;
        }
    }

    // ---- moving around ----

    private void ChooseSection(int index)
    {
        if (_capturingShortcut) return;

        if (index != _settingsSection)
        {
            _settingsSection = index;
            _settingsIndex = -1;
            ShowSettingsStatus(null);   // (the last action's result belongs to the other section)
            SettingsScroll.ScrollToTop();
        }

        _settingsInSection = false;
        RenderSettings();
    }

    private void EnterSection()
    {
        var first = NextSelectable(0, 1);
        if (first is null) return;   // (a section with nothing to change)

        _settingsInSection = true;
        _settingsIndex = first.Value;
        RenderSettings();
    }

    /// <summary>The first selectable row from <paramref name="start"/> on, going in <paramref name="step"/>'s direction.</summary>
    private int? NextSelectable(int start, int step)
    {
        for (var i = start; i >= 0 && i < _settingsRows.Count; i += step)
        {
            if (_settingsRows[i].Selectable) return i;
        }

        return null;
    }

    private void HandleSettings(PadButton button)
    {
        if (_busy) return;   // a scan is running

        if (_capturingShortcut)
        {
            if (button == PadButton.Back)
                StopCapturingShortcut();   // the keys themselves come from the keyboard (Window_PreviewKeyDown)
            return;
        }

        if (!_settingsInSection)
        {
            switch (button)
            {
                case PadButton.Up when _settingsSection > 0: ChooseSection(_settingsSection - 1); break;
                case PadButton.Down when _settingsSection < SettingsSections.Count - 1: ChooseSection(_settingsSection + 1); break;
                case PadButton.Confirm or PadButton.Right: EnterSection(); break;
                case PadButton.Back: CloseOverlay(); break;
            }
            return;
        }

        var row = _settingsRows[_settingsIndex];
        switch (button)
        {
            case PadButton.Up: MoveSetting(-1); break;
            case PadButton.Down: MoveSetting(1); break;

            case PadButton.Left or PadButton.Right when row.Adjust is { } adjust:
                adjust(button == PadButton.Left ? -1 : 1);
                RenderSettings();
                break;

            case PadButton.Confirm: ActivateSettingsRow(); break;
            case PadButton.Back or PadButton.Left: ChooseSection(_settingsSection); break;   // back to the sections
        }
    }

    private void MoveSetting(int step)
    {
        if (NextSelectable(_settingsIndex + step, step) is not { } next)
        {
            if (step < 0) SettingsScroll.ScrollToTop();   // (the section's first heading comes back into sight)
            return;
        }

        _settingsIndex = next;
        RenderSettings();
    }

    private void ActivateSettingsRow()
    {
        if (_settingsIndex < 0 || _settingsIndex >= _settingsRows.Count) return;

        _settingsRows[_settingsIndex].Activate?.Invoke();

        // the switches change (unless the action opened another screen: the browser, the shortcuts list...)
        if (!_closing && _overlay == Overlay.Settings)
            RenderSettings();
    }
}
