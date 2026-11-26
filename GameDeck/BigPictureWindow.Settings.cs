using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using GameDeck.Models;
using GameDeck.Services;

namespace GameDeck.Views;

public partial class BigPictureWindow
{
    // ---------- settings: full screen, the sections on the left, the chosen section's settings on the right ----------

    /// <summary>
    /// One line of a settings section. What it shows on the right follows from what it has:
    /// <see cref="IsOn"/> a switch, <see cref="Value"/> with <see cref="Adjust"/> a "‹ choice ›", <see cref="Steps"/> a bar,
    /// <see cref="Button"/> a button, <see cref="Value"/> alone a plain value (not selectable). A heading has only a title.
    /// </summary>
    private sealed record SettingRow(
        string Title,
        string? Subtitle = null,
        bool IsHeader = false,
        Func<bool>? IsOn = null,
        Func<string>? Value = null,
        Action<int>? Adjust = null,
        Func<(int Index, int Count)>? Steps = null,
        string? Button = null,
        Action? Activate = null)
    {
        public bool Selectable => !IsHeader && (Activate is not null || Adjust is not null);
    }

    private sealed record SettingsSection(string Name, string Glyph, Func<List<SettingRow>> Rows);

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

    // ---- the sections ----

    private static SettingRow Heading(string title) => new(title, IsHeader: true);

    private static SettingRow Toggle(string title, string? subtitle, Func<bool> isOn, Action<bool> set) =>
        new(title, subtitle, IsOn: isOn, Activate: () => set(!isOn()));

    private SettingRow ActionButton(string title, string? subtitle, string button, Func<Task<string?>> action, bool changesLibrary = true) =>
        new(title, subtitle, Button: button, Activate: () => RunSettingsAction(action, changesLibrary));

    private List<SettingRow> GeneralSettings()
    {
        var rows = new List<SettingRow>
        {
            Heading("Starting GameDeck"),
            Toggle("Launch GameDeck when Windows starts", null, () => StartupService.IsEnabled, StartupService.SetEnabled)
        };

        if (StartupService.IsEnabled)   // only makes sense while launching with Windows is on
            rows.Add(Toggle("Start minimized to the system tray", "When Windows starts GameDeck, it waits in the tray instead of opening",
                () => SettingsService.StartMinimized, on => SettingsService.StartMinimized = on));

        rows.AddRange(
        [
            Toggle("Open GameDeck in Big Picture mode", "Like a console: opening GameDeck goes straight to this screen",
                () => SettingsService.StartInBigPicture, on => SettingsService.StartInBigPicture = on),

            Heading("Closing and playing"),
            Toggle("Minimize to the system tray when closing GameDeck", "When this is off, closing the window closes GameDeck completely",
                () => SettingsService.CloseAction == CloseAction.MinimizeToTray,
                on => SettingsService.CloseAction = on ? CloseAction.MinimizeToTray : CloseAction.Exit),
            Toggle("Minimize GameDeck when a game starts", "It waits in the tray while you play",
                () => SettingsService.MinimizeOnLaunch, on => SettingsService.MinimizeOnLaunch = on),
            Toggle("Show a notice when a game starts", "A small \"Game on!\" in the corner of the screen",
                () => SettingsService.ShowLaunchToast, on => SettingsService.ShowLaunchToast = on)
        ]);

        return rows;
    }

    private List<SettingRow> LibrarySettings() =>
    [
        Heading("Find games"),
        ActionButton("Scan for games", "Looks for the games of every launcher on this PC", "Scan", _actions.Scan),
        new("Add games", "Browse your drives with the controller and pick the games' programs", Button: "Browse", Activate: OpenBrowse),
        ActionButton("Add games with the Windows file picker", "For a keyboard and mouse", "Open",
            () => Task.FromResult(_actions.AddGames(this))),

        Heading("Playtime"),
        Toggle("Include playtime from Steam", "The hours Steam counted, when they are more than GameDeck's. Read from this PC",
            () => SettingsService.SteamPlaytime, on =>
            {
                SettingsService.SteamPlaytime = on;
                _actions.SteamPlaytimeChanged();
            }),

        Heading("Tidy up"),
        ActionButton("Find duplicates", "The same game found in more than one launcher", "Find",
            () => Task.FromResult(_actions.FindDuplicates(this))),

        Heading("Cover art"),
        Toggle("Download cover art from the internet", "From Steam, Epic and Microsoft's store",
            () => SettingsService.OnlineCovers, on =>
            {
                SettingsService.OnlineCovers = on;
                _onlineCoversChanged();
            })
    ];

    private static readonly (AppTheme Theme, string Name)[] Themes =
        [(AppTheme.Dark, "Dark"), (AppTheme.Light, "Light"), (AppTheme.System, "Follow Windows")];

    private static readonly (double Scale, string Name)[] Scales = [(1.0, "Normal"), (1.2, "Large"), (1.4, "Extra large")];

    private List<SettingRow> AppearanceSettings()
    {
        int ThemeIndex() => Math.Max(0, Array.FindIndex(Themes, t => t.Theme == SettingsService.Theme));
        int ScaleIndex() => SettingsService.BigPictureScale switch { < 1.1 => 0, < 1.3 => 1, _ => 2 };

        void StepTheme(int step) => SetTheme(Themes[(ThemeIndex() + step + Themes.Length) % Themes.Length].Theme);
        void StepScale(int step) => SetScale(Scales[(ScaleIndex() + step + Scales.Length) % Scales.Length].Scale);

        return
        [
            Heading("Look"),
            new("Theme", "The colors of GameDeck, here and on the desktop", Value: () => Themes[ThemeIndex()].Name,
                Adjust: StepTheme, Activate: () => StepTheme(1)),
            new("Size", "Bigger is easier to read on a TV across the room", Value: () => Scales[ScaleIndex()].Name,
                Adjust: StepScale, Activate: () => StepScale(1))
        ];
    }

    private List<SettingRow> ControllerSettings() =>
    [
        Heading("Buttons"),
        Toggle("Confirm with B, go back with A", "The Nintendo arrangement, on any controller",
            () => SettingsService.SwapConfirmBack, on =>
            {
                SettingsService.SwapConfirmBack = on;
                BuildMainHints();   // the hints name the buttons the other way round now
                BuildOverlayHints();
            }),

        Heading("Home button"),
        Toggle("Open Big Picture mode from a controller",
            "Home (or holding View + Menu) while GameDeck is in the tray. During a game started from GameDeck, Home opens Now Playing",
            () => SettingsService.ControllerWake, on => SettingsService.ControllerWake = on),
        Toggle("Controller shortcuts", "Hold Home and press a button: volume, songs, Alt + Tab, keyboard, screenshot. In games too",
            () => SettingsService.ControllerShortcuts, on => SettingsService.ControllerShortcuts = on),
        new("Controller shortcuts list", "Every Home-button shortcut and what each button does in here", Button: "Show",
            Activate: OpenShortcuts)
    ];

    private List<SettingRow> SoundSettings()
    {
        var rows = new List<SettingRow>
        {
            Heading("Sounds"),
            Toggle("Sounds", "Moving, selecting, going back, starting a game",
                () => SettingsService.NavSounds, on => SettingsService.NavSounds = on)
        };

        if (SettingsService.NavSounds)   // (like the desktop slider, only while the sounds are on)
            rows.Add(new("Volume", null, Value: () => $"{SettingsService.SoundVolume * 100:0}%",
                Steps: () => (SettingsService.StepIndex(SettingsService.SoundVolumes, SettingsService.SoundVolume), SettingsService.SoundVolumes.Length),
                Adjust: step => ChangeVolume(step, wrap: false),
                Activate: () => ChangeVolume(1, wrap: true)));

        return rows;
    }

    private List<SettingRow> ScreenshotSettings()
    {
        var rows = new List<SettingRow>
        {
            Heading("Saving"),
            new("Save the controller's screenshots in", ScreenshotService.SaveFolderText + "\nBrowse your drives with the controller and choose a folder",
                Button: "Browse", Activate: OpenSaveFolderBrowse),
            new("Choose the folder with the Windows folder picker", "For a keyboard and mouse", Button: "Open",
                Activate: () => ScreenshotService.ChooseSaveFolder(this))
        };

        if (SettingsService.ScreenshotFolder is not null)   // (only a chosen folder can go back to the default places)
            rows.Add(new("Go back to the default places", "With each game's screenshots, or Pictures › Screenshots", Button: "Reset",
                Activate: () => SettingsService.ScreenshotFolder = null));

        return rows;
    }

    private List<SettingRow> QuickLaunchSettings()
    {
        var rows = new List<SettingRow>
        {
            Heading("Quick launch"),
            Toggle("Quick launch box", "A keyboard shortcut opens a small search box that starts any game",
                () => SettingsService.QuickLaunchEnabled, on =>
                {
                    SettingsService.QuickLaunchEnabled = on;
                    _quickLaunchChanged();
                })
        };

        if (SettingsService.QuickLaunchEnabled)   // the keys are typed on a keyboard
            rows.Add(new("Shortcut", _capturingShortcut
                    ? "Press the new keys on the keyboard…  (Esc to cancel)"
                    : GlobalHotkey.Describe(SettingsService.QuickLaunchModifiers, SettingsService.QuickLaunchKey),
                Button: _capturingShortcut ? "Waiting…" : "Change", Activate: StartCapturingShortcut));

        return rows;
    }

    private List<SettingRow> SystemSettings() =>
    [
        Heading("Updates"),
        new("Version", null, Value: () => UpdateService.CurrentVersionText + (AppInfo.IsBeta ? "  Beta" : "")),
        Toggle("Install updates automatically", "When GameDeck starts, from github.com/athathin/GameDeck",
            () => SettingsService.AutoUpdate, on => SettingsService.AutoUpdate = on),
        ActionButton("Check for updates", null, "Check now", () => _actions.CheckForUpdates(ShowSettingsStatus), changesLibrary: false),

        Heading("Backup"),
        ActionButton("Export backup", "Favorites, playtime, categories and settings in one file", "Export", () =>
        {
            _actions.ExportBackup(this);
            return Task.FromResult<string?>(null);
        }, changesLibrary: false),
        ActionButton("Import backup", "From a file made with Export backup", "Import", () => Task.FromResult(_actions.ImportBackup(this))),

        Heading("Uninstall"),
        ActionButton("Uninstall GameDeck", "Removes GameDeck from this PC", "Uninstall", () =>
        {
            _actions.Uninstall(this);
            return Task.FromResult<string?>(null);
        }, changesLibrary: false)
    ];

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

    /// <summary>The dark box of a choice or a button (the same in a selected card).</summary>
    private static Border ControlBox(UIElement content) => new()
    {
        Background = Divider, CornerRadius = new CornerRadius(4), Padding = new Thickness(18, 9, 18, 9), Width = 300,
        VerticalAlignment = VerticalAlignment.Center, Child = content
    };

    /// <summary>An on / off switch: the knob on the right and the accent color when on.</summary>
    private static Border Switch(bool on) => new()
    {
        Width = 56, Height = 30, CornerRadius = new CornerRadius(15), VerticalAlignment = VerticalAlignment.Center,
        Background = on ? Purple : Muted, Opacity = on ? 1 : 0.75,
        Child = new Ellipse
        {
            Width = 22, Height = 22, Fill = Brushes.White, Margin = new Thickness(4, 0, 4, 0),
            HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left
        }
    };

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

    // ---- what the settings do ----

    private void SetScale(double scale)
    {
        SettingsService.BigPictureScale = scale;
        ApplyScale();   // this screen changes at once
    }

    /// <summary>Draws the whole screen bigger (or normal): everything in it, text included, is scaled together.</summary>
    private void ApplyScale()
    {
        var scale = SettingsService.BigPictureScale;
        RootGrid.LayoutTransform = scale == 1.0 ? Transform.Identity : new ScaleTransform(scale, scale);
    }

    private static void SetTheme(AppTheme theme)
    {
        SettingsService.Theme = theme;
        ThemeService.Apply(theme);   // this screen and the desktop window change at once
    }

    /// <summary>
    /// One step quieter (-1) or louder (+1), on the same steps as the desktop slider; <paramref name="wrap"/> goes from the loudest
    /// back to the quietest (pressing the row) instead of stopping. The sound of the press is played again at the new volume.
    /// </summary>
    private static void ChangeVolume(int step, bool wrap)
    {
        var steps = SettingsService.SoundVolumes;
        var current = SettingsService.StepIndex(steps, SettingsService.SoundVolume);
        var index = current + step;
        index = wrap ? (index + steps.Length) % steps.Length : Math.Clamp(index, 0, steps.Length - 1);
        if (index == current) return;   // already the quietest / loudest: nothing to save

        SettingsService.SoundVolume = steps[index];
        NavSounds.Play(wrap ? NavSound.Select : NavSound.Move);   // (it just rang at the old volume)
    }

    private bool _busy;

    /// <summary>Runs one of the actions of the desktop settings page and shows its result line at the bottom of the panel.</summary>
    private async void RunSettingsAction(Func<Task<string?>> action, bool changesLibrary = true)
    {
        if (_busy) return;

        _busy = true;
        ShowSettingsStatus("Working…");

        string? status;
        try
        {
            status = await action();
        }
        catch (Exception ex)
        {
            status = ex.Message;
        }
        finally
        {
            _busy = false;
        }

        if (_closing) return;   // (uninstalling closes GameDeck)

        // scanning, adding, merging or restoring changed the library: the screen behind shows the new one
        if (changesLibrary)
            ReloadLibrary();

        ShowSettingsStatus(status);

        if (_overlay == Overlay.Settings)
            RenderSettings();   // (an import can change the settings shown)
    }

    /// <summary>Takes the library as the main window has it now and redraws the screen behind the panel, keeping the selection.</summary>
    private void ReloadLibrary()
    {
        var index = Carousel.SelectedIndex;
        var before = Carousel.SelectedItem as Game;

        var (games, hiddenKeys) = _actions.GetLibrary();
        _library = games;
        _hiddenKeys = hiddenKeys;
        ApplyTab(preserveSelection: true);

        // the selected game may be gone (hidden, removed): stay at the same place in the row
        if (IsGamesTab && _items.Count > 0 && (before is null || !_items.Contains(before)))
            Carousel.SelectedIndex = Math.Clamp(index, 0, _items.Count - 1);
    }

    private void ShowSettingsStatus(string? text)
    {
        SettingsStatus.Text = text ?? "";
        SettingsStatus.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---- changing the quick launch shortcut (typed on the keyboard) ----

    private bool _capturingShortcut;

    private void StartCapturingShortcut()
    {
        _capturingShortcut = true;
        RenderSettings();
    }

    private void StopCapturingShortcut()
    {
        _capturingShortcut = false;
        RenderSettings();
    }

    /// <summary>Returns true when the key was used for the shortcut.</summary>
    private bool CaptureShortcut(KeyEventArgs e)
    {
        // Alt combinations arrive as "System" keys
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape)
        {
            StopCapturingShortcut();
            return true;
        }

        // a modifier on its own isn't a shortcut yet: keep waiting for the real key
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.System or Key.None)
            return true;

        uint modifiers = 0;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) modifiers |= GlobalHotkey.Control;
        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0) modifiers |= GlobalHotkey.Alt;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) modifiers |= GlobalHotkey.Shift;
        if ((Keyboard.Modifiers & ModifierKeys.Windows) != 0) modifiers |= GlobalHotkey.Win;

        // Ctrl or Alt (or both) is required: Shift or Win alone, or a bare key, would take over normal typing
        if ((modifiers & (GlobalHotkey.Control | GlobalHotkey.Alt)) == 0)
        {
            ShowSettingsStatus("The shortcut needs Ctrl or Alt. Try again.");
            return true;
        }

        SettingsService.SetQuickLaunchShortcut(modifiers, (uint)KeyInterop.VirtualKeyFromKey(key));
        _quickLaunchChanged();
        ShowSettingsStatus(null);
        StopCapturingShortcut();
        return true;
    }
}
