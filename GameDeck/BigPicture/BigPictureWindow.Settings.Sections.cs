using GameDeck.Services;

namespace GameDeck.Views;

public partial class BigPictureWindow
{
    // ---- the sections ----

    private static SettingRow Heading(string title) => new(title, IsHeader: true);

    private static SettingRow Toggle(string title, string? subtitle, Func<bool> isOn, Action<bool> set) =>
        new(title, subtitle, IsOn: isOn, Activate: () => set(!isOn()));

    private SettingRow ActionButton(string title, string? subtitle, string button, Func<Task<string?>> action, bool changesLibrary = true) =>
        new(title, subtitle, Button: button, Activate: () => RunSettingsAction(action, changesLibrary));

    private List<SettingRow> GeneralSettings()
    {
        var breaks = SettingsService.BreakReminderChoices;
        void StepBreaks(int step) => SettingsService.BreakReminderMinutes =
            breaks[(Array.IndexOf(breaks, SettingsService.BreakReminderMinutes) + step + breaks.Length) % breaks.Length];

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
                () => SettingsService.ShowLaunchToast, on => SettingsService.ShowLaunchToast = on),
            Toggle("Ask where I left off when a game closes", "After 10 minutes or more. Shown on the game's page and in Continue playing",
                () => SettingsService.AskWhereLeftOff, on => SettingsService.AskWhereLeftOff = on),
            Toggle("Show a weekly recap", "Once a week, a notice with the week before: how long, on how many days, your most played game",
                () => SettingsService.WeeklyRecap, on => SettingsService.WeeklyRecap = on),
            new("Remind me to take a break", "A notice after every so long in one session of a game started from GameDeck",
                Value: () => SettingsService.BreakReminderMinutes switch
                {
                    0 => "Off",
                    var m when m % 60 == 0 => m == 60 ? "Every hour" : $"Every {m / 60} hours",
                    var m => $"Every {m / 60} h {m % 60} min"
                },
                Adjust: StepBreaks, Activate: () => StepBreaks(1))
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
        Toggle("Include playtime from your launchers", "The hours Steam, GOG Galaxy or itch counted, when they are more than GameDeck's. Read from this PC",
            () => SettingsService.ImportPlaytime, on =>
            {
                SettingsService.ImportPlaytime = on;
                _actions.PlaytimeSourcesChanged();
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

        var accents = ThemeService.Accents;
        int AccentIndex() => Math.Max(0, Array.FindIndex(accents, a => a.Hex == ThemeService.AccentHex));
        void StepAccent(int step)
        {
            var hex = accents[(AccentIndex() + step + accents.Length) % accents.Length].Hex;
            SettingsService.AccentColor = hex;
            ThemeService.ApplyAccent(hex);   // this screen and the desktop window change at once
        }

        var savers = SettingsService.ScreenSaverChoices;
        void StepSaver(int step) => SettingsService.ScreenSaverMinutes =
            savers[(Array.IndexOf(savers, SettingsService.ScreenSaverMinutes) + step + savers.Length) % savers.Length];

        return
        [
            Heading("Look"),
            new("Theme", "The colors of GameDeck, here and on the desktop", Value: () => Themes[ThemeIndex()].Name,
                Adjust: StepTheme, Activate: () => StepTheme(1)),
            new("Size", "Bigger is easier to read on a TV across the room", Value: () => Scales[ScaleIndex()].Name,
                Adjust: StepScale, Activate: () => StepScale(1)),
            new("Accent color", "The color of the selection rings, buttons and highlights", Value: () => accents[AccentIndex()].Name,
                Adjust: StepAccent, Activate: () => StepAccent(1)),

            Heading("When idle"),
            new("Screen saver", "Fades to black with your covers moving around, against burn-in on OLED TVs. Any button wakes it",
                Value: () => SettingsService.ScreenSaverMinutes == 0 ? "Off" : $"After {SettingsService.ScreenSaverMinutes} minutes",
                Adjust: StepSaver, Activate: () => StepSaver(1))
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
            Activate: OpenShortcuts),

        Heading("Battery"),
        Toggle("Warn when the battery is low", "A notice at 15% and at 5%, also during games (Xbox controllers over Bluetooth)",
            () => SettingsService.LowBatteryWarning, on => SettingsService.LowBatteryWarning = on)
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

    private List<SettingRow> SystemSettings()
    {
        var rows = SystemRows();

        // the daily backup in a folder: after Import backup
        var at = rows.FindIndex(r => r.Title == "Import backup") + 1;
        rows.Insert(at, new("Save a backup every day", SettingsService.BackupFolder is { } folder
                ? "In " + BackupService.FolderIn(folder) + ". The last 7 days are kept"
                : "Off. In a folder you choose: OneDrive, a USB drive... Your data survives a reinstall or a new PC",
            Button: SettingsService.BackupFolder is null ? "Choose" : "Change", Activate: OpenBackupFolderBrowse));

        if (SettingsService.BackupFolder is not null)
            rows.Insert(at + 1, new("Stop the daily backup", "The backups already saved stay", Button: "Turn off",
                Activate: () => SettingsService.BackupFolder = null));

        return rows;
    }

    private List<SettingRow> SystemRows() =>
    [
        Heading("Updates"),
        new("Version", null, Value: () => UpdateService.CurrentVersionText + (AppInfo.IsBeta ? "  Beta" : "")),
        Toggle("Install updates automatically", "When GameDeck starts, from github.com/athathin/GameDeck",
            () => SettingsService.AutoUpdate, on => SettingsService.AutoUpdate = on),
        ActionButton("Check for updates", null, "Check now", () => _actions.CheckForUpdates(ShowSettingsStatus), changesLibrary: false),
        ActionButton("Support GameDeck", "GameDeck is free. If you enjoy it, buy its maker a coffee through PayPal", "Donate",
            () => Task.FromResult<string?>(AppInfo.OpenWebPage(AppInfo.DonateUrl)
                ? "PayPal is open in your browser"
                : "No browser could be opened: the page is " + AppInfo.DonateUrl), changesLibrary: false),

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
}
