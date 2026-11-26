using System.Windows.Threading;
using GameDeck.Services;
using GameDeck.Views;

namespace GameDeck;

public partial class MainWindow
{
    // ---------- while GameDeck keeps running: a look for a newer version once a day ----------

    // GameDeck looks for a newer version when it starts (App.StartAfterUpdateCheck). Left running for days (in the tray, started
    // with Windows), it also looks once a day, and says so when there is one: it installs at the next start (or at once from
    // Settings › Check for updates), never in the middle of something.
    private static readonly TimeSpan UpdateCheckEvery = TimeSpan.FromHours(24);
    private static readonly TimeSpan UpdateCheckRetry = TimeSpan.FromMinutes(30);   // (a game was running: look again later)

    private readonly DispatcherTimer _updateTimer = new() { Interval = UpdateCheckEvery };
    private string? _announcedVersion;   // a version is announced once

    private void StartDailyUpdateCheck()
    {
        _updateTimer.Tick += async (_, _) => await CheckForUpdateQuietlyAsync();
        _updateTimer.Start();   // (the first look is a day after the start: the start looked already)
    }

    private async Task CheckForUpdateQuietlyAsync()
    {
        if (!SettingsService.AutoUpdate || !UpdateService.CanUpdate) return;   // (the Beta and Debug builds never update)

        // not while a game started from GameDeck runs: no notice in the middle of a game
        if (_tracker.IsTracking)
        {
            _updateTimer.Interval = UpdateCheckRetry;
            return;
        }

        _updateTimer.Interval = UpdateCheckEvery;

        if (await UpdateService.CheckAsync(TimeSpan.FromSeconds(30)) is { } update)
            ShowUpdateReady($"{update.Version.Major}.{update.Version.Minor}.{update.Version.Build}");
    }

    /// <summary>Says that a newer version is there, once per version (it installs when GameDeck starts next).</summary>
    internal void ShowUpdateReady(string version)
    {
        if (version == _announcedVersion) return;

        _announcedVersion = version;
        GameToastWindow.ShowMessage($"GameDeck {version} is ready", "It installs the next time GameDeck starts.",
            "To get it now: Settings › Check for updates.", TimeSpan.FromSeconds(12));
    }
}
