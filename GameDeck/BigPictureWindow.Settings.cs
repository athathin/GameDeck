using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GameDeck.Models;
using GameDeck.Services;

namespace GameDeck.Views;

public partial class BigPictureWindow
{
    // ---------- settings ----------

    private List<FilterRow> _settingsRows = [];
    private int _settingsIndex;

    private void OpenSettings()
    {
        _settingsIndex = 1;   // row 0 is a heading
        _capturingShortcut = false;
        ShowSettingsStatus(null);
        ShowOverlay(Overlay.Settings);
        RenderSettings();
        FillSettingsHints();
    }

    private void FillSettingsHints()
    {
        var l = Labels();
        FillHints(SettingsHints, (l.Confirm, "Change"), (l.Back, "Close"));
    }

    /// <summary>The settings that make sense from the couch. Everything else is on the desktop settings page.</summary>
    private void RenderSettings()
    {
        // the same settings as the desktop page, in the same order
        var rows = new List<FilterRow>
        {
            new("GAMES", IsHeader: true),
            new("Scan for games", Activate: () => RunSettingsAction(_actions.Scan)),
            new("Add games…  (browse with the controller)", Activate: OpenBrowse),
            new("Add games with the Windows file picker…  (keyboard and mouse)",
                Activate: () => RunSettingsAction(() => Task.FromResult(_actions.AddGames(this)))),
            new("Find duplicates…", Activate: () => RunSettingsAction(() => Task.FromResult(_actions.FindDuplicates(this)))),

            new("APPEARANCE", IsHeader: true),
            new("Dark theme", IsOn: () => SettingsService.Theme == AppTheme.Dark, IsRadio: true,
                Activate: () => SetTheme(AppTheme.Dark)),
            new("Light theme", IsOn: () => SettingsService.Theme == AppTheme.Light, IsRadio: true,
                Activate: () => SetTheme(AppTheme.Light)),
            new("Follow Windows", IsOn: () => SettingsService.Theme == AppTheme.System, IsRadio: true,
                Activate: () => SetTheme(AppTheme.System)),

            new("SIZE", IsHeader: true),
            new("Normal", IsOn: () => SettingsService.BigPictureScale < 1.1, IsRadio: true, Activate: () => SetScale(1.0)),
            new("Large", IsOn: () => SettingsService.BigPictureScale is >= 1.1 and < 1.3, IsRadio: true, Activate: () => SetScale(1.2)),
            new("Extra large (for a TV)", IsOn: () => SettingsService.BigPictureScale >= 1.3, IsRadio: true,
                Activate: () => SetScale(1.4)),

            new("GENERAL", IsHeader: true),
            new("Launch GameDeck when Windows starts", IsOn: () => StartupService.IsEnabled,
                Activate: () => StartupService.SetEnabled(!StartupService.IsEnabled))
        };

        if (StartupService.IsEnabled)   // only makes sense while launching with Windows is on
            rows.Add(new("Launch GameDeck minimized to the system tray", IsOn: () => SettingsService.StartMinimized,
                Activate: () => SettingsService.StartMinimized = !SettingsService.StartMinimized));

        rows.AddRange(
        [
            new("Open GameDeck in Big Picture mode  (like a console)", IsOn: () => SettingsService.StartInBigPicture,
                Activate: () => SettingsService.StartInBigPicture = !SettingsService.StartInBigPicture),
            new("Minimize to the system tray when closing GameDeck", IsOn: () => SettingsService.CloseAction == CloseAction.MinimizeToTray,
                Activate: () => SettingsService.CloseAction = SettingsService.CloseAction == CloseAction.MinimizeToTray
                    ? CloseAction.Exit
                    : CloseAction.MinimizeToTray),
            new("Minimize GameDeck to the system tray when a game starts", IsOn: () => SettingsService.MinimizeOnLaunch,
                Activate: () => SettingsService.MinimizeOnLaunch = !SettingsService.MinimizeOnLaunch),
            new("Show a notice in the corner when a game starts", IsOn: () => SettingsService.ShowLaunchToast,
                Activate: () => SettingsService.ShowLaunchToast = !SettingsService.ShowLaunchToast),
            new("Download cover art from the internet", IsOn: () => SettingsService.OnlineCovers,
                Activate: () =>
                {
                    SettingsService.OnlineCovers = !SettingsService.OnlineCovers;
                    _onlineCoversChanged();
                }),

            new("Install updates automatically when GameDeck starts  (version " + UpdateService.CurrentVersionText + ")",
                IsOn: () => SettingsService.AutoUpdate,
                Activate: () => SettingsService.AutoUpdate = !SettingsService.AutoUpdate),

            new("Check for updates", Activate: () => RunSettingsAction(() => _actions.CheckForUpdates(ShowSettingsStatus),
                                                                       changesLibrary: false)),

            new("QUICK LAUNCH", IsHeader: true),
            new("Open a quick launch box with a keyboard shortcut", IsOn: () => SettingsService.QuickLaunchEnabled,
                Activate: () =>
                {
                    SettingsService.QuickLaunchEnabled = !SettingsService.QuickLaunchEnabled;
                    _quickLaunchChanged();
                })
        ]);

        if (SettingsService.QuickLaunchEnabled)   // the keys are typed on a keyboard
            rows.Add(new(_capturingShortcut
                ? "Shortcut: press the new keys on the keyboard…  (Esc to cancel)"
                : "Shortcut: " + GlobalHotkey.Describe(SettingsService.QuickLaunchModifiers, SettingsService.QuickLaunchKey) +
                  "  (choose to change it)", Activate: StartCapturingShortcut));

        rows.AddRange(
        [
            new("Confirm with B and go back with A (swap the two buttons)", IsOn: () => SettingsService.SwapConfirmBack,
                Activate: () =>
                {
                    SettingsService.SwapConfirmBack = !SettingsService.SwapConfirmBack;
                    BuildMainHints();   // the hints name the buttons the other way round now
                    BuildOverlayHints();
                }),
            new("Sounds  (moving, selecting, going back, starting a game)", IsOn: () => SettingsService.NavSounds,
                Activate: () => SettingsService.NavSounds = !SettingsService.NavSounds)
        ]);

        if (SettingsService.NavSounds)   // (like the desktop slider, only while the sounds are on)
            rows.Add(new($"Sound volume  {SettingsService.SoundVolume * 100:0}%  (left / right)",
                Steps: () => (SettingsService.StepIndex(SettingsService.SoundVolumes, SettingsService.SoundVolume), SettingsService.SoundVolumes.Length),
                Adjust: step => ChangeVolume(step, wrap: false),
                Activate: () => ChangeVolume(1, wrap: true)));

        rows.AddRange(
        [
            new("Open Big Picture mode from a controller", IsOn: () => SettingsService.ControllerWake,
                Activate: () => SettingsService.ControllerWake = !SettingsService.ControllerWake),
            new("Controller shortcuts: hold Home and press a button  (also in games; the list: Power menu)",
                IsOn: () => SettingsService.ControllerShortcuts,
                Activate: () => SettingsService.ControllerShortcuts = !SettingsService.ControllerShortcuts),
            new("Screenshots are saved in: " + ScreenshotService.SaveFolderText + "  (choose another folder…)",
                Activate: () => ScreenshotService.ChooseSaveFolder(this))
        ]);

        if (SettingsService.ScreenshotFolder is not null)   // (only a chosen folder can go back to the default places)
            rows.Add(new("Save screenshots in the default places again", Activate: () => SettingsService.ScreenshotFolder = null));

        rows.AddRange(
        [
            new("BACKUP", IsHeader: true),
            new("Export backup…", Activate: () => RunSettingsAction(() =>
            {
                _actions.ExportBackup(this);
                return Task.FromResult<string?>(null);
            }, changesLibrary: false)),
            new("Import backup…", Activate: () => RunSettingsAction(() => Task.FromResult(_actions.ImportBackup(this)))),

            new("UNINSTALL", IsHeader: true),
            new("Uninstall GameDeck…", Activate: () => RunSettingsAction(() =>
            {
                _actions.Uninstall(this);
                return Task.FromResult<string?>(null);
            }, changesLibrary: false))
        ]);

        _settingsRows = rows;

        _ = RenderRows(SettingsList, _settingsRows, _settingsIndex, index =>
        {
            _settingsIndex = index;
            ActivateSettingsRow();
        }, index =>
        {
            if (_settingsIndex == index || _capturingShortcut) return;

            _settingsIndex = index;
            RenderOnHover(RenderSettings);
        });
    }

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

    private void HandleSettings(PadButton button)
    {
        if (_busy) return;   // a scan is running

        if (_capturingShortcut)
        {
            if (button == PadButton.Back)
                StopCapturingShortcut();   // the keys themselves come from the keyboard (Window_PreviewKeyDown)
            return;
        }

        switch (button)
        {
            case PadButton.Up: MoveSetting(-1); break;
            case PadButton.Down: MoveSetting(1); break;
            case PadButton.Left or PadButton.Right when _settingsRows[_settingsIndex].Adjust is { } adjust:
                adjust(button == PadButton.Left ? -1 : 1);
                RenderSettings();
                break;
            case PadButton.Confirm: ActivateSettingsRow(); break;
            case PadButton.Back: CloseOverlay(); break;
        }
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
        if (_tab != StatsTab && _items.Count > 0 && (before is null || !_items.Contains(before)))
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

    private void MoveSetting(int step)
    {
        var next = _settingsIndex;

        do
        {
            next += step;
            if (next < 0 || next >= _settingsRows.Count) return;
        }
        while (_settingsRows[next].IsHeader);

        _settingsIndex = next;
        RenderSettings();
    }

    private void ActivateSettingsRow()
    {
        _settingsRows[_settingsIndex].Activate?.Invoke();

        if (!_closing)          // ("All settings" closed the screen)
            RenderSettings();   // the ticks change
    }
}
