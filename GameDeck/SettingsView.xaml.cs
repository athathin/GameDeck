using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GameDeck.Services;

namespace GameDeck.Views;

/// <summary>
/// The settings (shown in their own window, SettingsWindow, opened with the gear in the library header): sections on the left,
/// their settings on the right, like Big Picture's settings. Every setting takes effect immediately; scanning, the backups,
/// updates and uninstalling are handled by the main window.
/// </summary>
public partial class SettingsView : UserControl
{
    public event EventHandler? AddGamesRequested;
    public event EventHandler? ScanRequested;
    public event EventHandler? DuplicatesRequested;
    public event EventHandler? OnlineCoversChanged;
    public event EventHandler? PlaytimeSourcesChanged;
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
        StartInBigPictureCheckBox.IsChecked = SettingsService.StartInBigPicture;

        var action = SettingsService.CloseAction;
        TrayCheckBox.IsChecked = action == CloseAction.MinimizeToTray;
        AskHint.Visibility = action == CloseAction.Ask ? Visibility.Visible : Visibility.Collapsed;

        OnlineCoversCheckBox.IsChecked = SettingsService.OnlineCovers;
        ImportPlaytimeCheckBox.IsChecked = SettingsService.ImportPlaytime;

        DarkThemeRadio.IsChecked = SettingsService.Theme == AppTheme.Dark;
        LightThemeRadio.IsChecked = SettingsService.Theme == AppTheme.Light;
        SystemThemeRadio.IsChecked = SettingsService.Theme == AppTheme.System;

        var scale = SettingsService.BigPictureScale;
        ScaleNormalRadio.IsChecked = scale < 1.1;
        ScaleLargeRadio.IsChecked = scale is >= 1.1 and < 1.3;
        ScaleHugeRadio.IsChecked = scale >= 1.3;
        BuildMinuteChoices();
        BuildAccentChoices();

        GridKeyBox.Text = SettingsService.SteamGridDbKey ?? "";
        IgdbIdBox.Text = SettingsService.IgdbClientId ?? "";
        IgdbSecretBox.Password = SettingsService.IgdbClientSecret ?? "";

        AutoUpdateCheckBox.IsChecked = SettingsService.AutoUpdate;
        VersionText.Text = $"You have GameDeck {UpdateService.CurrentVersionText}. New versions come from github.com/athathin/GameDeck.";

        MinimizeOnLaunchCheckBox.IsChecked = SettingsService.MinimizeOnLaunch;
        LaunchToastCheckBox.IsChecked = SettingsService.ShowLaunchToast;
        LeftOffCheckBox.IsChecked = SettingsService.AskWhereLeftOff;
        WeeklyRecapCheckBox.IsChecked = SettingsService.WeeklyRecap;

        ControllerWakeCheckBox.IsChecked = SettingsService.ControllerWake;
        ControllerShortcutsCheckBox.IsChecked = SettingsService.ControllerShortcuts;
        LowBatteryCheckBox.IsChecked = SettingsService.LowBatteryWarning;
        NavSoundsCheckBox.IsChecked = SettingsService.NavSounds;
        ShowSoundVolume();
        SwapButtonsCheckBox.IsChecked = SettingsService.SwapConfirmBack;
        QuickLaunchCheckBox.IsChecked = SettingsService.QuickLaunchEnabled;
        StopCapturing();
        ShowQuickLaunchOptions();
        ShowScreenshotFolder();
        ShowBackupFolder();

        _updating = false;
    }

    // ---------- the daily backup in a folder ----------

    private void ShowBackupFolder()
    {
        BackupFolderText.Text = SettingsService.BackupFolder is { } folder
            ? $"On: {BackupService.FolderIn(folder)}. The last 7 days are kept; Import backup restores any of them."
            : "Off. Choose a folder in OneDrive, Google Drive or on a USB drive, and your data survives a reinstall or a new PC.";
        BackupOffButton.Visibility = SettingsService.BackupFolder is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void BackupFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Save a GameDeck backup every day in",
            InitialDirectory = SettingsService.BackupFolder is { } chosen && System.IO.Directory.Exists(chosen)
                ? chosen : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        SettingsService.BackupFolder = dialog.FolderName;   // (the first backup is saved there at once)
        ShowBackupFolder();
    }

    private void BackupOffButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsService.BackupFolder = null;   // (the backups already saved stay)
        ShowBackupFolder();
    }

    // ---------- where the controller's screenshots go ----------

    private void ShowScreenshotFolder()
    {
        ScreenshotFolderText.Text = ScreenshotService.SaveFolderText;
        ScreenshotDefaultButton.Visibility = SettingsService.ScreenshotFolder is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ScreenshotFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (ScreenshotService.ChooseSaveFolder(Window.GetWindow(this)))
            ShowScreenshotFolder();
    }

    private void ScreenshotDefaultButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsService.ScreenshotFolder = null;
        ShowScreenshotFolder();
    }

    // ---------- quick launch shortcut ----------

    // True while the shortcut button waits for the user to press the new keys. The main window checks this so Esc
    // cancels the capture instead of leaving the settings screen.
    public bool IsCapturingShortcut { get; private set; }

    private void NavSoundsCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_updating) return;

        SettingsService.NavSounds = NavSoundsCheckBox.IsChecked == true;   // Big Picture checks this each time a sound would play
        ShowSoundVolume();
    }

    private void ControllerShortcutsCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_updating) return;

        SettingsService.ControllerShortcuts = ControllerShortcutsCheckBox.IsChecked == true;   // the listeners check this on their own
    }

    private void LowBatteryCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_updating) return;

        SettingsService.LowBatteryWarning = LowBatteryCheckBox.IsChecked == true;   // (checked on its own each minute)
    }

    /// <summary>
    /// Puts the volume slider on the saved volume and names it ("Volume: 75%") in its tooltip and the line under "Volume", which
    /// also says why the slider is faded while the sounds are off.
    /// </summary>
    private void ShowSoundVolume()
    {
        SoundVolumeSlider.Value = SettingsService.StepIndex(SettingsService.SoundVolumes, SettingsService.SoundVolume);

        var percent = $"{SettingsService.SoundVolume * 100:0}%";
        SoundVolumeSlider.ToolTip = $"Volume: {percent}";
        VolumeNote.Text = NavSoundsCheckBox.IsChecked == true
            ? $"{percent}. Each change plays a sample."
            : $"{percent}. Turn on the sounds above to change it.";
    }

    private void SoundVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating) return;   // (Reload putting it on the saved volume)

        SettingsService.SoundVolume = SettingsService.StepAt(SettingsService.SoundVolumes, e.NewValue);
        ShowSoundVolume();
        NavSounds.Play(NavSound.Select);   // a sample at the new volume
    }

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

    private void StartInBigPictureCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_updating) return;

        SettingsService.StartInBigPicture = StartInBigPictureCheckBox.IsChecked == true;
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

    private void LeftOffCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_updating) return;

        SettingsService.AskWhereLeftOff = LeftOffCheckBox.IsChecked == true;
    }

    private void WeeklyRecapCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_updating) return;

        SettingsService.WeeklyRecap = WeeklyRecapCheckBox.IsChecked == true;   // (checked on its own every hour)
    }

    private void OnlineCoversCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_updating) return;

        SettingsService.OnlineCovers = OnlineCoversCheckBox.IsChecked == true;
        OnlineCoversChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ImportPlaytimeCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_updating) return;

        SettingsService.ImportPlaytime = ImportPlaytimeCheckBox.IsChecked == true;
        PlaytimeSourcesChanged?.Invoke(this, EventArgs.Empty);
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

    /// <summary>The accent colors as round swatches (the chosen one ringed); a click changes the color everywhere at once.</summary>
    private void BuildAccentChoices()
    {
        AccentChoices.Children.Clear();

        foreach (var (name, hex) in ThemeService.Accents)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var chosen = hex == ThemeService.AccentHex;

            var swatch = new Border
            {
                Width = 34, Height = 34, CornerRadius = new CornerRadius(17), Margin = new Thickness(0, 0, 12, 0), Padding = new Thickness(3),
                BorderThickness = new Thickness(2), BorderBrush = chosen ? ThemeService.TextPrimary : Brushes.Transparent,
                Cursor = Cursors.Hand, ToolTip = name,
                Child = new Border { CornerRadius = new CornerRadius(13), Background = new SolidColorBrush(color) }
            };

            swatch.MouseLeftButtonUp += (_, _) =>
            {
                SettingsService.AccentColor = hex;
                ThemeService.ApplyAccent(hex);   // every open screen changes at once
                BuildAccentChoices();
            };

            AccentChoices.Children.Add(swatch);
        }
    }

    /// <summary>Off, 5 min, 10 min... for the screen saver and the break reminder: the saved one ticked.</summary>
    private void BuildMinuteChoices()
    {
        Fill(ScreenSaverChoices, "ScreenSaver", SettingsService.ScreenSaverChoices, SettingsService.ScreenSaverMinutes,
            minutes => SettingsService.ScreenSaverMinutes = minutes);
        Fill(BreakChoices, "BreakReminder", SettingsService.BreakReminderChoices, SettingsService.BreakReminderMinutes,
            minutes => SettingsService.BreakReminderMinutes = minutes);

        void Fill(Panel panel, string group, int[] choices, int saved, Action<int> save)
        {
            panel.Children.Clear();

            foreach (var minutes in choices)
            {
                var radio = new RadioButton
                {
                    Content = minutes == 0 ? "Off" : minutes % 60 == 0 && minutes >= 60 ? $"{minutes / 60} h" : $"{minutes} min",
                    GroupName = group, FontSize = 15, Style = (Style)FindResource("DarkRadioButton"), IsChecked = minutes == saved,
                    Margin = new Thickness(panel.Children.Count == 0 ? 0 : 32, 0, 0, 0)
                };
                radio.Checked += (_, _) => save(minutes);   // (attached after IsChecked was set)
                panel.Children.Add(radio);
            }
        }
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

    /// <summary>
    /// The IGDB keys were changed: saved, checked with Twitch (the line under them says whether they work), and once they do,
    /// the games without details, background or cover are looked for again.
    /// </summary>
    private async void IgdbKeys_LostFocus(object sender, RoutedEventArgs e)
    {
        var id = IgdbIdBox.Text.Trim();
        var secret = IgdbSecretBox.Password.Trim();
        if (_updating || (id == (SettingsService.IgdbClientId ?? "") && secret == (SettingsService.IgdbClientSecret ?? ""))) return;

        SettingsService.IgdbClientId = id;
        SettingsService.IgdbClientSecret = secret;

        if (id.Length == 0 && secret.Length == 0)
        {
            IgdbService.Reset();
            IgdbStatus.Visibility = Visibility.Collapsed;
            return;
        }

        IgdbStatus.Text = "Checking the keys with Twitch…";
        IgdbStatus.Visibility = Visibility.Visible;

        var problem = await IgdbService.CheckKeysAsync();
        IgdbStatus.Text = problem ?? "The keys work: GameDeck asks IGDB for the games the stores don't know.";

        if (problem is null)
            OnlineCoversChanged?.Invoke(this, EventArgs.Empty);   // the missing details, backgrounds and covers are looked for again
    }

    /// <summary>Opens the page of Twitch's developer console where an app (and so the IGDB keys) is registered.</summary>
    private void IgdbConsoleButton_Click(object sender, RoutedEventArgs e)
    {
        if (AppInfo.OpenWebPage("https://dev.twitch.tv/console/apps/create")) return;

        IgdbStatus.Text = "No browser could be opened: go to dev.twitch.tv/console/apps/create yourself.";
        IgdbStatus.Visibility = Visibility.Visible;
    }

    private void CheckUpdatesButton_Click(object sender, RoutedEventArgs e) => CheckUpdatesRequested?.Invoke(this, EventArgs.Empty);

    private void DonateButton_Click(object sender, RoutedEventArgs e)
    {
        if (!AppInfo.OpenWebPage(AppInfo.DonateUrl))
            DonateText.Text = "No browser could be opened: the page is " + AppInfo.DonateUrl;
    }

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

    // ---------- the sections (left column) ----------

    /// <summary>A section was chosen on the left: its title and its panel ("Quick launch" → QuicklaunchSection) are shown.</summary>
    private void SectionNav_Checked(object sender, RoutedEventArgs e)
    {
        // (the first one is checked while the XAML is still being read: the panels don't exist yet, General shows by default)
        if (sender is not RadioButton { Content: string name } || SectionTitle is null) return;

        SectionTitle.Text = name;
        var chosen = FindName(name.Replace(" ", "") + "Section");

        // (every section panel sits in the same grid: a new section needs no line here)
        foreach (var panel in ((Panel)GeneralSection.Parent).Children.OfType<FrameworkElement>())
            panel.Visibility = ReferenceEquals(panel, chosen) ? Visibility.Visible : Visibility.Collapsed;

        SectionScroll.ScrollToTop();
    }

    /// <summary>Opens on a section ("General", "Library"...), like right after a click on it.</summary>
    public void ShowSection(string name)
    {
        foreach (var item in SectionNav.Children.OfType<RadioButton>())
            if (item.Content as string == name)
                item.IsChecked = true;
    }

    private void ExportButton_Click(object sender, RoutedEventArgs e) => ExportRequested?.Invoke(this, EventArgs.Empty);

    private void ImportButton_Click(object sender, RoutedEventArgs e) => ImportRequested?.Invoke(this, EventArgs.Empty);
}
