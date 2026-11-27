using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GameDeck.Models;
using GameDeck.Services;
using GameDeck.Views;
using Microsoft.Win32;

namespace GameDeck;

public partial class MainWindow
{
    // ---------- stats ----------

    public record TopGame(string Name, string Time, double Fraction);

    private void ShowStats()
    {
        var played = _library.Games
            .Where(g => g.PlaytimeSeconds > 0)
            .OrderByDescending(g => g.PlaytimeSeconds)
            .ToList();

        var total = played.Sum(g => g.PlaytimeSeconds);
        StatTotal.Text = total <= 0 ? "0 min" : Game.FormatPlaytime(total);
        StatPlayed.Text = played.Count.ToString();
        StatLibrary.Text = _library.Games.Count(g => !g.IsAddOn).ToString();

        var most = played.FirstOrDefault()?.PlaytimeSeconds ?? 1;
        TopGamesList.ItemsSource = played
            .Take(10)
            .Select(g => new TopGame(g.DisplayName, Game.FormatPlaytime(g.PlaytimeSeconds), (double)g.PlaytimeSeconds / most))
            .ToList();

        NoStatsText.Visibility = played.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // over time: needs the dated sessions, so it only covers play since GameDeck started recording them
        StatWeek.Text = FormatHours(_sessions.SecondsThisWeek());

        var (current, longest) = _sessions.Streaks();
        StatStreak.Text = FormatDays(current);
        StatLongestStreak.Text = FormatDays(longest);

        var hidden = _library.Games.Where(g => g.IsHidden).Select(g => g.LaunchTarget).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var weekly = _sessions.ThisWeek(hidden);
        WeekHeadline.Text = weekly.Headline;
        WeekCompare.Text = weekly.Compare;
        WeekLines.ItemsSource = weekly.Lines;

        var yearly = _sessions.ThisYear(hidden);
        YearHeadline.Text = yearly.Headline;
        YearCompare.Text = yearly.Compare;
        YearLines.ItemsSource = yearly.Lines;

        var disk = DiskUsage.Summary(_library.Games.Where(g => !g.IsHidden), DateTime.UtcNow);
        DiskPanel.Visibility = disk is null ? Visibility.Collapsed : Visibility.Visible;
        if (disk is not null)
        {
            DiskHeadline.Text = disk.Headline;
            DiskCompare.Text = disk.Compare;
            DiskLines.ItemsSource = disk.Lines;
        }

        DaysChart.ItemsSource = _sessions.LastDays(7);
        WeeksChart.ItemsSource = _sessions.LastWeeks(8);

        var recent = _sessions.Recent(10);
        RecentSessionsList.ItemsSource = recent;
        NoSessionsText.Visibility = recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // (a quick registry read, done once; Refresh looks again, for a launcher installed since)
        _installedLaunchers ??= LauncherStats.InstalledLaunchers();
        var launchers = LauncherStats.Lines(_library.Games, _installedLaunchers);
        LauncherLines.ItemsSource = launchers;
        LaunchersPanel.Visibility = launchers.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        // playtime from before sessions were recorded counts in the totals but can't appear in the charts
        UndatedNote.Visibility = total - _sessions.LoggedSeconds > 60 ? Visibility.Visible : Visibility.Collapsed;

        UpdateStatsLayout();
    }

    private HashSet<string>? _installedLaunchers;

    private void RefreshStatsButton_Click(object sender, RoutedEventArgs e)
    {
        _installedLaunchers = null;
        ShowStats();
    }

    /// <summary>
    /// The visible width changed (window resized, scroll bar shown or hidden). Not SizeChanged: when that fires, ViewportWidth
    /// still holds the old width, so maximizing kept the small layout and restoring kept the big one.
    /// </summary>
    private void StatsPanel_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, StatsPanel) && e.ViewportWidthChange != 0)
            UpdateStatsLayout();
    }

    private const double StatsWideWidth = 1240;

    /// <summary>The page's distance from the window's left edge: the window's margin (24) plus the page's own (8).</summary>
    private const double StatsEdge = 32;

    /// <summary>
    /// Like a game's page: when wide, play over time (boxes, this week, this year, charts) on the left and the lists (most played,
    /// sessions, launchers, space on disk) on the right, filling the window: the gap between the columns is as wide as the space
    /// at the window's left edge, and the right column reaches as close to the right edge (its scroll bar counts as edge).
    /// Otherwise one column.
    /// </summary>
    private void UpdateStatsLayout()
    {
        var space = StatsPanel.ViewportWidth > 0 ? StatsPanel.ViewportWidth : StatsPanel.ActualWidth;
        var wide = space >= StatsWideWidth;

        // the window's 24 px margin and the scroll bar already lie right of the page: only the rest of the 32 px is added
        var scrollBar = StatsPanel.ViewportWidth > 0 ? Math.Max(0, StatsPanel.ActualWidth - StatsPanel.ViewportWidth) : 0;
        var right = Math.Max(0, StatsEdge - 24 - scrollBar);

        StatsRoot.MaxWidth = wide ? double.PositiveInfinity : 760;
        StatsRoot.HorizontalAlignment = wide ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        StatsRoot.Margin = new Thickness(8, 4, wide ? right : 8, 0);

        StatsGapColumn.Width = new GridLength(wide ? StatsEdge : 0);
        StatsSideColumn.Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

        Grid.SetColumn(StatsSidePanel, wide ? 2 : 0);
        Grid.SetRow(StatsSidePanel, wide ? 0 : 1);
        StatsSideHeader.Margin = new Thickness(0, wide ? 0 : 28, 0, 10);   // (level with the boxes when it's a column of its own)
    }

    private static string FormatHours(long seconds) =>
        seconds <= 0 ? "0 min" : seconds < 3600 ? $"{seconds / 60} min" : $"{seconds / 3600.0:0.#} h";

    private static string FormatDays(int days) => days == 1 ? "1 day" : $"{days} days";

    // ---------- buttons ----------

    private async Task ScanAsync()
    {
        SettingsPanel?.SetScanning(true);
        StatusText.Text = "Scanning...";

        CoverService.ClearMisses();              // retry covers that weren't found before
        await Task.Run(_library.Scan);           // background thread keeps the window responsive

        Refresh();
        _ = LoadLauncherPlaytimeAsync();            // (newly found games get their hours too)
        _ = LoadAchievementsAsync();                // (and their achievements)
        _ = LoadSteamAppsAsync();                   // (and which are DLC or soundtracks)
        FetchGenres();                           // (and their genres)
        MeasureSizes();                          // (and their size on disk)
        SettingsPanel?.SetScanning(false);
        SettingsPanel?.SetGamesStatus($"Scan finished: {_library.Games.Count(g => !g.IsAddOn)} games in your library.");
    }

    // The settings actions below run from the desktop settings page and from Big Picture's settings panel. Each takes the
    // window its dialogs belong to and returns a line to show as the result (null = nothing to say).

    private string? FindDuplicates(Window owner)
    {
        var groups = DuplicateFinder.Find(_library.Games);
        if (groups.Count == 0)
            return "No duplicate games found.";

        var dialog = new DuplicatesDialog(groups, (keep, others) =>
        {
            _userData.MergeDuplicates(keep, others);
            _sessions.Reassign(others, keep);   // the session history follows the playtime
        }) { Owner = owner };
        dialog.ShowDialog();

        if (dialog.MergedGroups == 0)
            return null;

        Refresh();   // the merged duplicates are hidden now
        return dialog.MergedGroups == 1 ? "Merged 1 duplicate." : $"Merged {dialog.MergedGroups} duplicates.";
    }

    private void SettingsPanel_DuplicatesRequested(object? sender, EventArgs e)
    {
        if (FindDuplicates(SettingsOwner) is { } status)
            SettingsPanel?.SetGamesStatus(status);
    }

    private async void SettingsPanel_ScanRequested(object? sender, EventArgs e) => await ScanAsync();

    private string? AddGames(Window owner)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select the games' .exe files",
            Filter = "Programs (*.exe)|*.exe",
            Multiselect = true
        };

        if (dialog.ShowDialog(owner) != true) return null;

        return AddFiles(dialog.FileNames);
    }

    /// <summary>Adds programs as games (the file picker above, and Big Picture's file browser). Returns the result line.</summary>
    private string? AddFiles(IReadOnlyList<string> files)
    {
        var added = 0;
        foreach (var file in files)
        {
            if (_library.Games.Any(g => g.LaunchTarget.Equals(file, StringComparison.OrdinalIgnoreCase)))
                continue;   // already in the library

            _library.AddManual(new Game
            {
                Name = Path.GetFileNameWithoutExtension(file),
                Source = "Manual",
                LaunchTarget = file
            });
            added++;
        }

        Refresh();

        if (added == 0) return files.Count == 1 ? "That game is already in the library." : "Those games are already in the library.";
        return added == 1 ? "Added 1 game." : $"Added {added} games.";
    }

    private void SettingsPanel_AddGamesRequested(object? sender, EventArgs e)
    {
        if (AddGames(SettingsOwner) is { } status)
            SettingsPanel?.SetGamesStatus(status);
    }

    private void PickRandomGame()
    {
        var games = RealGames.ToList();
        if (games.Count == 0) return;

        var game = games[Random.Shared.Next(games.Count)];
        GameList.SelectedItem = game;
        GameList.ScrollIntoView(game);
        StatusText.Text = $"How about {game.DisplayName}?";
    }

    /// <summary>The "pick a game for me" tile has no right-click menu.</summary>
    private void Tile_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is Game { IsRandomTile: true })
            e.Handled = true;
    }

    /// <summary>
    /// The "Check for updates" button (Settings, and Big Picture's settings): looks for a newer release and, when there is one,
    /// installs it and restarts GameDeck. <paramref name="report"/> shows what is going on; the result is the final line to show.
    /// </summary>
    private async Task<string?> CheckForUpdatesAsync(Action<string> report)
    {
        if (!UpdateService.CanUpdate)
            return "This copy of GameDeck can't update itself: only the released program, in a folder you can write to, can.";

        report("Checking for updates…");
        var check = await UpdateService.CheckDetailedAsync(TimeSpan.FromSeconds(10));
        if (check.Failed) return "Couldn't check for updates. Is this PC connected to the internet?";
        if (check.Update is not { } update) return $"GameDeck {UpdateService.CurrentVersionText} is the latest version.";

        var version = $"{update.Version.Major}.{update.Version.Minor}.{update.Version.Build}";

        // a restart would stop the timing of a game that was started from GameDeck
        if (_tracker.IsTracking)
            return $"Version {version} is available. Close the game you started from GameDeck, then check again.";

        report($"Downloading version {version}…");
        var installed = await UpdateService.InstallAsync(update, new Progress<double>(p => report($"Downloading version {version}… {p:P0}")));
        if (!installed) return "The update couldn't be installed, so nothing was changed. Try again later.";

        report("Restarting GameDeck…");
        try
        {
            UpdateService.Restart(Environment.GetCommandLineArgs().Skip(1), waitForThisProgram: true);
        }
        catch
        {
            return $"Version {version} is installed, but GameDeck couldn't restart by itself. Start it again.";
        }

        _exiting = true;   // a normal exit: the playtime so far is saved, and no "minimize or close?" question
        Application.Current.Shutdown();
        return null;
    }

    private async void SettingsPanel_CheckUpdatesRequested(object? sender, EventArgs e)
    {
        // (the window may be closed while it checks: then there's nothing to show it in)
        SettingsPanel?.SetUpdateStatus("", busy: true);

        var result = await CheckForUpdatesAsync(text => SettingsPanel?.SetUpdateStatus(text, busy: true));
        if (result is not null)
            SettingsPanel?.SetUpdateStatus(result, busy: false);
    }

    private void SettingsPanel_OnlineCoversChanged(object? sender, EventArgs e)
    {
        if (!SettingsService.OnlineCovers) return;

        CoverService.ClearMisses();
        _ = LoadVisibleCoversAsync();   // fetch covers that were skipped while offline
        FetchGenres();                  // and the genres
    }

    /// <summary>Asks the Steam store, in the background, for the genres of the games that have none yet (see GenreService).</summary>
    private void FetchGenres() => _ = GenreService.FetchMissingAsync(_library.Games.ToList(), () => Refresh());

    private void SettingsPanel_PlaytimeSourcesChanged(object? sender, EventArgs e) => _ = LoadLauncherPlaytimeAsync();

    /// <summary>
    /// Reads the playtime Steam, GOG Galaxy and the itch app counted (their files on this PC, see LauncherPlaytime) in the
    /// background and shows it on the games (the higher of the launcher's and GameDeck's). With "Include playtime from your launchers" off, only GameDeck's own playtime is shown.
    /// </summary>
    private async Task LoadLauncherPlaytimeAsync()
    {
        try
        {
            var games = _library.Games.ToList();   // (a copy: the files are read in the background)
            var played = SettingsService.ImportPlaytime
                ? await Task.Run(() => LauncherPlaytime.Read(games))
                : new Dictionary<string, SteamPlaytime.Played>();

            _userData.SetExternalPlaytime(played);
            Refresh();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            CrashLog.Write(ex, "Steam playtime");   // (the games keep GameDeck's own playtime)
        }
    }

    /// <summary>The achievements unlocked per game, from Steam's (and GOG Galaxy's) files on this PC (see Achievements), read in the background.</summary>
    private async Task LoadAchievementsAsync()
    {
        try
        {
            var games = _library.Games.ToList();
            var counts = await Task.Run(() => Achievements.Read(games));

            foreach (var game in games)
                game.AchievementsText = counts.TryGetValue(game.LaunchTarget, out var count) ? count.ToString() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            CrashLog.Write(ex, "Achievements");   // (the game pages just don't show them)
        }
    }

    /// <summary>Reads, in the background, what kind of app each Steam game is (Steam's appinfo.vdf), so DLC and soundtracks stay apart.</summary>
    private async Task LoadSteamAppsAsync()
    {
        var ids = _library.Games.Select(g => g.SteamAppId).OfType<string>().ToList();
        if (ids.Count == 0) return;

        _steamApps = await Task.Run(() => SteamAppInfo.Read(ids));
        Refresh();
    }

    /// <summary>Measures the games' install folders in the background (sizes older than a few days, new games; see DiskUsage).</summary>
    private async void MeasureSizes()
    {
        try
        {
            await DiskUsage.MeasureAsync(_library.Games.ToList(), () =>
            {
                _sizesTimer.Stop();
                _sizesTimer.Start();   // (one redraw once a few sizes came in, not one per game)
            }, busy: () => _tracker.IsTracking);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            CrashLog.Write(ex, "Disk usage");
        }
    }

    // redraws what shows the sizes (the "Largest first" order, the Stats tab) shortly after new sizes came in
    private readonly System.Windows.Threading.DispatcherTimer _sizesTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    private void SizesTimer_Tick(object? sender, EventArgs e)
    {
        _sizesTimer.Stop();
        if (_sort == SortMode.Size || _view == "Stats")
            Refresh(reapplyUserData: false);
    }

    // ---------- the settings window ----------

    private SettingsWindow? _settingsWindow;

    /// <summary>The settings, while their window is open (null otherwise).</summary>
    private SettingsView? SettingsPanel => _settingsWindow?.View;

    /// <summary>The window the settings' dialogs (file pickers, duplicates, backup...) belong to: the settings window when open.</summary>
    private Window SettingsOwner => (Window?)_settingsWindow ?? this;

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => OpenSettings();

    /// <summary>Opens the settings window (or brings it to the front when it is already open).</summary>
    private void OpenSettings()
    {
        if (_settingsWindow is { } open)
        {
            if (open.WindowState == WindowState.Minimized)
                open.WindowState = WindowState.Normal;
            open.Activate();
            return;
        }

        var window = new SettingsWindow(this);
        var view = window.View;
        view.AddGamesRequested += SettingsPanel_AddGamesRequested;
        view.OnlineCoversChanged += SettingsPanel_OnlineCoversChanged;
        view.PlaytimeSourcesChanged += SettingsPanel_PlaytimeSourcesChanged;
        view.QuickLaunchChanged += SettingsPanel_QuickLaunchChanged;
        view.UninstallRequested += SettingsPanel_UninstallRequested;
        view.ScanRequested += SettingsPanel_ScanRequested;
        view.DuplicatesRequested += SettingsPanel_DuplicatesRequested;
        view.ExportRequested += SettingsPanel_ExportRequested;
        view.CheckUpdatesRequested += SettingsPanel_CheckUpdatesRequested;
        view.ImportRequested += SettingsPanel_ImportRequested;

        window.Closed += (_, _) =>
        {
            _settingsWindow = null;

            // back to the library, not to whatever window was behind (not when GameDeck itself is closing or hiding)
            if (IsVisible && !_exiting)
            {
                try { Activate(); }
                catch (InvalidOperationException) { }   // (the main window is closing: nothing to bring back)
            }
        };

        _settingsWindow = window;
        window.Show();
    }

    private void Uninstall(Window owner)
    {
        var dialog = new UninstallDialog { Owner = owner };
        if (dialog.ShowDialog() != true) return;

        try
        {
            UninstallService.Uninstall(dialog.KeepData);
        }
        catch (Exception ex)
        {
            MessageDialog.Show(owner, ex.Message, "Couldn't uninstall GameDeck");
            return;
        }

        ExitApp();   // the cleanup script finishes the job once GameDeck has closed
    }

    private void SettingsPanel_UninstallRequested(object? sender, EventArgs e) => Uninstall(SettingsOwner);

    private void ExportBackup(Window owner)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export GameDeck backup",
            Filter = "GameDeck backup (*.json)|*.json",
            FileName = $"GameDeck-backup-{DateTime.Now:yyyy-MM-dd}.json"
        };

        if (dialog.ShowDialog(owner) != true) return;

        try
        {
            BackupService.Export(dialog.FileName, _userData, _library);
            MessageDialog.Show(owner, "Your backup was saved.", "Backup saved");
        }
        catch (Exception ex)
        {
            MessageDialog.Show(owner, ex.Message, "Couldn't save the backup");
        }
    }

    private void SettingsPanel_ExportRequested(object? sender, EventArgs e) => ExportBackup(SettingsOwner);

    private void ImportBackup(Window owner)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import GameDeck backup",
            Filter = "GameDeck backup (*.json)|*.json"
        };

        if (dialog.ShowDialog(owner) != true) return;

        try
        {
            var count = BackupService.Import(dialog.FileName, _userData, _library);
            Refresh();
            MessageDialog.Show(owner, $"Your backup was restored ({count} games).", "Backup restored");
        }
        catch (Exception ex)
        {
            MessageDialog.Show(owner, ex.Message, "Couldn't restore the backup");
        }
    }

    private void SettingsPanel_ImportRequested(object? sender, EventArgs e) => ImportBackup(SettingsOwner);

    /// <summary>What Big Picture's settings panel can ask of the main window (the same actions as the desktop settings page).</summary>
    private BigPictureSettingsActions BigPictureActions() => new(
        Scan: async () =>
        {
            await ScanAsync();
            return $"Scan finished: {_library.Games.Count} games in your library.";
        },
        AddGames: AddGames,
        AddFiles: AddFiles,
        FindDuplicates: FindDuplicates,
        ExportBackup: ExportBackup,
        CheckForUpdates: CheckForUpdatesAsync,
        ImportBackup: owner =>
        {
            ImportBackup(owner);
            return null;
        },
        Uninstall: Uninstall,
        GetLibrary: () => (_library.Games.ToList(),
                           _library.Games.Where(g => g.IsHidden).Select(g => g.LaunchTarget)
                               .ToHashSet(StringComparer.OrdinalIgnoreCase)),
        PlaytimeSourcesChanged: () => _ = LoadLauncherPlaytimeAsync());

    // ---------- clicking on the grid ----------

    private void RootGrid_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Clicks on tiles and buttons are handled by those controls, so this only
        // fires for empty space: deselect the current game.
        GameList.SelectedItem = null;
        Keyboard.ClearFocus();
    }

    // The tile the left button went down on. Double-clicking the title bar maximizes the window, and the
    // button release that follows lands on whatever tile is now under the mouse: without a matching press
    // on the same tile, that release is not a click.
    private Game? _pressedGame;

    private Game? TileUnder(MouseButtonEventArgs e) =>
        e.OriginalSource is DependencyObject source &&
        ItemsControl.ContainerFromElement(GameList, source) is ListBoxItem { DataContext: Game game }
            ? game
            : null;

    private void GameList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        _pressedGame = TileUnder(e);

    private void GameList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // Only react when the click landed on a tile, not on empty space or the scrollbar
        var game = TileUnder(e);
        var pressed = _pressedGame;
        _pressedGame = null;

        if (game is null || !ReferenceEquals(game, pressed))
            return;

        if (game.IsRandomTile)
        {
            PickRandomGame();
            return;
        }

        OpenDetails(game);
    }

    // ---------- a game's page ----------

    private void OpenDetails(Game game) => NavigateTo(new Page(PageKind.Details, game));

    private void CloseDetails() => GoBack();

    // ---------- going back and forward (like a browser: Back button, Esc, the mouse's side buttons) ----------

    private enum PageKind { Library, Details }   // (the settings have a window of their own: SettingsWindow)

    private record Page(PageKind Kind, Game? Game = null);

    private Page _page = new(PageKind.Library);
    private readonly Stack<Page> _backPages = new();
    private readonly Stack<Page> _forwardPages = new();

    /// <summary>Opens a page and remembers the current one, so Back returns to it.</summary>
    private void NavigateTo(Page page)
    {
        _backPages.Push(_page);
        _forwardPages.Clear();   // like a browser: a new page ends the "forward" history
        ShowPage(page);
    }

    private void GoBack()
    {
        if (_backPages.Count == 0) return;

        _forwardPages.Push(_page);
        ShowPage(_backPages.Pop());
    }

    private void GoForward()
    {
        if (_forwardPages.Count == 0) return;

        _backPages.Push(_page);
        ShowPage(_forwardPages.Pop());
    }

    private void ShowPage(Page page)
    {
        // a game that was removed from the library in the meantime has no page any more
        if (page.Kind == PageKind.Details && (page.Game is null || !_library.Games.Contains(page.Game)))
            page = new Page(PageKind.Library);

        _page = page;

        LibraryView.Visibility = page.Kind == PageKind.Library ? Visibility.Visible : Visibility.Collapsed;
        DetailsPanel.Visibility = page.Kind == PageKind.Details ? Visibility.Visible : Visibility.Collapsed;

        if (page.Kind == PageKind.Details)
        {
            var ranked = _library.Games
                .Where(g => g.PlaytimeSeconds > 0)
                .OrderByDescending(g => g.PlaytimeSeconds)
                .ToList();

            DetailsPanel.Show(page.Game!, ranked.IndexOf(page.Game!) + 1, ranked.Count, _sessions.Recent(8, page.Game),
                _sessions.History(page.Game!), SteamAppInfo.AddOnsOf(_library.Games, page.Game!));
        }
    }

    /// <summary>The mouse's side buttons: Back (usually the lower one) and Forward.</summary>
    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.XButton1)
            GoBack();
        else if (e.ChangedButton == MouseButton.XButton2)
            GoForward();
        else
            return;

        e.Handled = true;
    }

    private void DetailsPanel_BackRequested(object? sender, EventArgs e) => CloseDetails();

    private void DetailsPanel_ScreenshotRequested(object? sender, (IReadOnlyList<string> Files, int Index) e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Files[e.Index]) { UseShellExecute = true });   // the picture viewer Windows uses for it
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException or InvalidOperationException)
        {
            StatusText.Text = "Couldn't open that picture.";
        }
    }

    private async void DetailsPanel_PlayRequested(object? sender, Game game) => await LaunchGameAsync(game);

    private async Task LaunchGameAsync(Game game, ResumeMode? from = null)
    {
        var launched = await LaunchCoreAsync(game, from);

        // Started from Big Picture, which made the desktop window step aside: decide what shows now
        if (from != ResumeMode.BigPicture || !_mainHiddenForBigPicture) return;

        if (!launched)
        {
            OpenBigPicture(_bigPictureTab, _bigPictureGame);   // it didn't start (cancelled...): back to where we were
            return;
        }

        StepAsideForGame();
    }

    /// <summary>
    /// Big Picture closed for a game (started, or resumed) while the desktop window was hidden behind it: the window goes to
    /// the tray, or comes back behind the game without taking the keyboard from it.
    /// </summary>
    private void StepAsideForGame()
    {
        if (!_mainHiddenForBigPicture) return;

        _mainHiddenForBigPicture = false;

        if (SettingsService.MinimizeOnLaunch)
        {
            StartInTray();   // stays out of the way; the tray icon is how to find it again
        }
        else
        {
            ShowActivated = false;   // visible again behind the game, without taking the keyboard from it
            Show();
            ShowActivated = true;
        }
    }

    /// <summary>"Resume" in Big Picture (which has closed by now): back to the running game's window.</summary>
    private void ResumeRunningGame(Game game)
    {
        StepAsideForGame();

        if (game.TrackingFolder is not { } folder || !RunningGame.BringToFront(folder))
            GameToastWindow.ShowMessage("Couldn't find the game's window", game.DisplayName, "Switch to it with Alt + Tab.");
    }

    /// <returns>True when the game was started.</returns>
    private async Task<bool> LaunchCoreAsync(Game game, ResumeMode? from)
    {
        if (_launching) return false;

        // where to come back to when the game closes: Big Picture if it started there, the window if it was on screen,
        // and nothing if GameDeck was sitting in the tray (quick launch, tray menu)
        var resume = from ?? (IsVisible ? ResumeMode.Desktop : ResumeMode.None);

        // Riot games need Riot Vanguard, which only has to run while playing: start it now, then launch.
        // (false = the user cancelled Windows' admin prompt, so don't launch.)
        if (VanguardService.IsNeededBy(game))
        {
            _launching = true;
            try
            {
                if (!await VanguardService.EnsureRunningAsync()) return false;
            }
            finally
            {
                _launching = false;
            }
        }

        // The game's launch routine (close / open apps, power plan). Runs while a second click on Play is ignored.
        // false = the user chose Cancel in the "close these apps?" question, so the game doesn't start.
        _launching = true;
        LaunchRoutineService.PrepareResult routine;
        try
        {
            routine = await _routines.PrepareAsync(game, ConfirmClosing);
            if (!routine.Proceed) return false;
        }
        finally
        {
            _launching = false;
        }

        var openLauncherWindows = LauncherSilencer.Snapshot(game);   // windows already open stay untouched
        if (!LaunchService.Launch(game))
        {
            await _routines.FinishAsync(game);   // the game never started: undo what the routine did
            return false;
        }

        _resume[game] = resume;
        _userData.RecordLaunch(game);   // "last played"
        _tracker.Track(game);           // playtime: starts counting once the game's process appears
        _ = LauncherSilencer.MinimizeNewWindowsAsync(game, openLauncherWindows);   // hide the launcher that pops up

        if (_view == "Recent" || _sort == SortMode.LastPlayed)
            Refresh();                  // the game moves to the front of the list

        // GameDeck ends up in the tray when it hides itself below, or when it was there already. Started from Big Picture,
        // the hidden window comes back afterwards unless it minimizes (LaunchGameAsync).
        var hiddenForBigPicture = from == ResumeMode.BigPicture && _mainHiddenForBigPicture;
        var inTray = SettingsService.MinimizeOnLaunch || (!IsVisible && !hiddenForBigPicture);

        if (SettingsService.ShowLaunchToast)
            GameToastWindow.ShowFor(game.DisplayName, routine.Summary, inTray);

        // get out of the way: GameDeck waits in the tray (it keeps timing the game from there)
        if (SettingsService.MinimizeOnLaunch && IsVisible)
            HideToTray();

        return true;
    }
}
