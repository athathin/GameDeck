using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using GameDeck.Models;
using GameDeck.Services;

namespace GameDeck.Views;

public partial class BigPictureWindow
{
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

    /// <summary>The main window's library changed (an automatic scan found games new or gone): show it, and the stats with it.</summary>
    public void LibraryChanged()
    {
        if (_closing) return;

        ReloadLibrary();
        if (_tab == StatsTab)
            RefreshStats();
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
