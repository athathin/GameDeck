using System.Windows.Threading;
using GameDeck.Views;

namespace GameDeck.Services;

/// <summary>
/// "Your week in games": once a week, a notice in the corner with the week before (its playtime, on how many days, the most
/// played game, compared with the week before). Shown the first time GameDeck runs in a new week, and checked every hour
/// while it runs; never while a game from GameDeck is running (it waits until the game is over). A week with no play shows
/// nothing. Settings › General turns it off. Like the stats, it only knows games started from GameDeck.
/// </summary>
public sealed class WeeklyRecapNotice(SessionLog sessions, Func<IReadOnlySet<string>> hiddenKeys, Func<bool> gameRunning) : IDisposable
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromHours(1) };

    public void Start()
    {
        _timer.Tick += (_, _) => Check();
        _timer.Start();
        Check();
    }

    public void Dispose() => _timer.Stop();

    /// <summary>Also called when a game is over: a recap that waited for it shows then.</summary>
    public void Check()
    {
        if (!SettingsService.WeeklyRecap || gameRunning()) return;

        var lastWeek = Due(DateOnly.FromDateTime(DateTime.Now), SettingsService.LastRecapWeek);
        if (lastWeek is null) return;

        SettingsService.LastRecapWeek = lastWeek;   // once per week, also when there is nothing to show

        if (sessions.RecapOf(lastWeek.Value, hiddenKeys()) is { } recap)
            GameToastWindow.ShowMessage(recap.Headline, recap.Text, recap.Detail.Length > 0 ? recap.Detail : null, TimeSpan.FromSeconds(15));
    }

    /// <summary>The Monday of the week before <paramref name="today"/>'s, when its recap hasn't been shown yet; otherwise null.</summary>
    internal static DateOnly? Due(DateOnly today, DateOnly? shown)
    {
        var lastWeek = SessionLog.MondayOf(today).AddDays(-7);
        return shown is { } done && done >= lastWeek ? null : lastWeek;
    }
}
