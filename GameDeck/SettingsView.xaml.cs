using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GameDeck.Services;

namespace GameDeck.Views;

/// <summary>
/// The settings screen (opened with the gear in the library header). The two checkboxes take effect immediately;
/// the back button and the backup buttons are handled by the main window.
/// </summary>
public partial class SettingsView : UserControl
{
    public event EventHandler? BackRequested;
    public event EventHandler? AddGamesRequested;
    public event EventHandler? ScanRequested;
    public event EventHandler? DuplicatesRequested;
    public event EventHandler? OnlineCoversChanged;
    public event EventHandler? QuickLaunchChanged;
    public event EventHandler? UninstallRequested;
    public event EventHandler? ExportRequested;
    public event EventHandler? ImportRequested;
    public event EventHandler? CheckUpdatesRequested;

    // True while the code (not the user) changes a checkbox, so that doesn't count as a choice.
    // The checkboxes react to Checked / Unchecked rather than Click so that every way of toggling works
    // (mouse, keyboard, screen readers).
    private bool _updating;

    public SettingsView()
    {
        InitializeComponent();
    }

    /// <summary>Shows the current values. Call every time the screen is opened.</summary>
    public void Reload()
    {
        GamesStatus.Visibility = Visibility.Collapsed;
        _updating = true;

        StartupCheckBox.IsChecked = StartupService.IsEnabled;
        StartMinimizedCheckBox.IsChecked = SettingsService.StartMinimized;
        ShowStartMinimizedOption();

        var action = SettingsService.CloseAction;
        TrayCheckBox.IsChecked = action == CloseAction.MinimizeToTray;
        AskHint.Visibility = action == CloseAction.Ask ? Visibility.Visible : Visibility.Collapsed;

        OnlineCoversCheckBox.IsChecked = SettingsService.OnlineCovers;

        DarkThemeRadio.IsChecked = SettingsService.Theme == AppTheme.Dark;
        LightThemeRadio.IsChecked = SettingsService.Theme == AppTheme.Light;
        SystemThemeRadio.IsChecked = SettingsService.Theme == AppTheme.System;

        var scale = SettingsService.BigPictureScale;
        ScaleNormalRadio.IsChecked = scale < 1.1;
        ScaleLargeRadio.IsChecked = scale is >= 1.1 and < 1.3;
        ScaleHugeRadio.IsChecked = scale >= 1.3;

        GridKeyBox.Text = SettingsService.SteamGridDbKey ?? "";

        AutoUpdateCheckBox.IsChecked = SettingsService.AutoUpdate;
        VersionText.Text = $"You have GameDeck {UpdateService.CurrentVersionText}. New versions come from github.com/athathin/GameDeck.";

        MinimizeOnLaunchCheckBox.IsChecked = SettingsService.MinimizeOnLaunch;
        LaunchToastCheckBox.IsChecked = SettingsService.ShowLaunchToast;

        ControllerWakeCheckBox.IsChecked = SettingsService.ControllerWake;
        SwapButtonsCheckBox.IsChecked = SettingsService.SwapConfirmBack;
        QuickLaunchCheckBox.IsChecked = SettingsService.QuickLaunchEnabled;
        StopCapturing();
        ShowQuickLaunchOptions();

        _updating = false;
    }

    // ---------- quick launch shortcut ----------

    // True while the shortcut button waits for the user to press the new keys. The main window checks this so Esc
    // cancels the capture instead of leaving the settings screen.
    public bool IsCapturingShortcut { get; private set; }

    private void ControllerWakeCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_updating) return;

        SettingsService.ControllerWake = ControllerWakeCheckBox.IsChecked == true;   // the listener checks this on its own
    }

    private void SwapButtonsCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_updating) return;

        SettingsService.SwapConfirmBack = SwapButtonsCheckBox.IsChecked == true;   // read at every button press
    }

    private void QuickLaunchCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_updating) return;

        SettingsService.QuickLaunchEnabled = QuickLaunchCheckBox.IsChecked == true;
        ShowQuickLaunchOptions();
        QuickLaunchChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The shortcut button only matters while the option is on.</summary>
    private void ShowQuickLaunchOptions()
    {
        ShortcutPanel.Visibility = QuickLaunchCheckBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (!IsCapturingShortcut)
            ShortcutButton.Content = GlobalHotkey.Describe(SettingsService.QuickLaunchModifiers, SettingsService.QuickLaunchKey);
    }

    private void ShortcutButton_Click(object sender, RoutedEventArgs e)
    {
        IsCapturingShortcut = true;
        ShortcutButton.Content = "Press the new shortcut…";
        ShortcutButton.Focus();
    }

    private void ShortcutButton_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsCapturingShortcut) return;   // Enter / Space on the focused button start the capture through Click

        e.Handled = true;

        // Alt combinations arrive as "System" keys
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape)
        {
            StopCapturing();
            return;
        }

        // a modifier on its own isn't a shortcut yet: keep waiting for the real key
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.System or Key.None)
            return;

        uint modifiers = 0;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) modifiers |= GlobalHotkey.Control;
        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0) modifiers |= GlobalHotkey.Alt;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) modifiers |= GlobalHotkey.Shift;
        if ((Keyboard.Modifiers & ModifierKeys.Windows) != 0) modifiers |= GlobalHotkey.Win;

        // Ctrl or Alt (or both) is required: Shift or Win alone, or a bare key, would take over normal typing
        if ((modifiers & (GlobalHotkey.Control | GlobalHotkey.Alt)) == 0)
        {
            ShortcutButton.Content = "Needs Ctrl or Alt — try again";
            return;
        }

        SettingsService.SetQuickLaunchShortcut(modifiers, (uint)KeyInterop.VirtualKeyFromKey(key));
        StopCapturing();
        QuickLaunchChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ShortcutButton_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => StopCapturing();

    private void StopCapturing()
    {
        IsCapturingShortcut = false;
        ShortcutButton.Content = GlobalHotkey.Describe(SettingsService.QuickLaunchModifiers, SettingsService.QuickLaunchKey);
    }

    private void StartupCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_updating) return;

        StartupService.SetEnabled(StartupCheckBox.IsChecked == true);

        _updating = true;
        StartupCheckBox.IsChecked = StartupService.IsEnabled;   // shows the truth if Windows refused
        _updating = false;

        ShowStartMinimizedOption();
    }

    private void StartMinimizedCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_updating) return;

        SettingsService.StartMinimized = StartMinimizedCheckBox.IsChecked == true;
    }

    /// <summary>The "minimized to the tray" sub-option only makes sense while launching with Windows is on.</summary>
    private void ShowStartMinimizedOption() =>
        StartMinimizedPanel.Visibility = StartupCheckBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    private void TrayCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_updating) return;

        SettingsService.CloseAction = TrayCheckBox.IsChecked == true
            ? CloseAction.MinimizeToTray
            : CloseAction.Exit;

        AskHint.Visibility = Visibility.Collapsed;
    }

    private void ScanButton_Click(object sender, RoutedEventArgs e) => ScanRequested?.Invoke(this, EventArgs.Empty);

    private void DuplicatesButton_Click(object sender, RoutedEventArgs e) => DuplicatesRequested?.Invoke(this, EventArgs.Empty);

    private void AddGamesButton_Click(object sender, RoutedEventArgs e) => AddGamesRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Disables the scan button (and says so) while a scan is running.</summary>
    public void SetScanning(bool scanning)
    {
        ScanButton.IsEnabled = !scanning;
        if (scanning)
            SetGamesStatus("Scanning...");
    }

    /// <summary>A short result line under the Scan / Add buttons.</summary>
    public void SetGamesStatus(string text)
    {
        GamesStatus.Text = text;
        GamesStatus.Visibility = Visibility.Visible;
    }

    private void MinimizeOnLaunchCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_updating) return;

        SettingsService.MinimizeOnLaunch = MinimizeOnLaunchCheckBox.IsChecked == true;
    }

    private void LaunchToastCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_updating) return;

        SettingsService.ShowLaunchToast = LaunchToastCheckBox.IsChecked == true;
    }

    private void OnlineCoversCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_updating) return;

        SettingsService.OnlineCovers = OnlineCoversCheckBox.IsChecked == true;
        OnlineCoversChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ThemeRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_updating) return;

        var theme = SystemThemeRadio.IsChecked == true ? AppTheme.System
            : LightThemeRadio.IsChecked == true ? AppTheme.Light
            : AppTheme.Dark;

        SettingsService.Theme = theme;
        ThemeService.Apply(theme);   // every open screen changes at once
    }

    private void ScaleRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_updating) return;

        SettingsService.BigPictureScale = ScaleHugeRadio.IsChecked == true ? 1.4
            : ScaleLargeRadio.IsChecked == true ? 1.2
            : 1.0;   // Big Picture reads it when it opens
    }

    private void GridKeyBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_updating || (GridKeyBox.Text.Trim() == (SettingsService.SteamGridDbKey ?? ""))) return;

        SettingsService.SteamGridDbKey = GridKeyBox.Text;
        CoverService.ResetKeyCheck();
        OnlineCoversChanged?.Invoke(this, EventArgs.Empty);   // covers that were missing are looked for again
    }

    private void CheckUpdatesButton_Click(object sender, RoutedEventArgs e) => CheckUpdatesRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Shows what the update check is doing / found, and keeps the button off while it works.</summary>
    public void SetUpdateStatus(string text, bool busy)
    {
        UpdateStatusText.Text = text;
        UpdateStatusText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        CheckUpdatesButton.IsEnabled = !busy;
    }

    private void AutoUpdateCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_updating) return;

        SettingsService.AutoUpdate = AutoUpdateCheckBox.IsChecked == true;
    }

    private void UninstallButton_Click(object sender, RoutedEventArgs e) => UninstallRequested?.Invoke(this, EventArgs.Empty);

    private void BackButton_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);

    private void ExportButton_Click(object sender, RoutedEventArgs e) => ExportRequested?.Invoke(this, EventArgs.Empty);

    private void ImportButton_Click(object sender, RoutedEventArgs e) => ImportRequested?.Invoke(this, EventArgs.Empty);
}
