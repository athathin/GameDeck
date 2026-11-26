using System.Windows.Threading;
using GameDeck.Models;
using GameDeck.Views;

namespace GameDeck.Services;

/// <summary>
/// "You've been playing for 2 hours": a notice every so many minutes of one session of a game started from GameDeck
/// (Settings, off by default). Checked once a minute; a new session starts the count again.
/// </summary>
public sealed class BreakReminder(Func<(Game Game, DateTime StartUtc)?> runningSession) : IDisposable
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(1) };
    private DateTime? _sessionStart;   // the session counted
    private int _shown;                // reminders shown in it

    public void Start()
    {
        _timer.Tick += (_, _) => Check();
        _timer.Start();
    }

    public void Dispose() => _timer.Stop();

    private void Check()
    {
        var every = SettingsService.BreakReminderMinutes;
        if (every == 0 || runningSession() is not { } session)
        {
            _sessionStart = null;
            return;
        }

        if (_sessionStart != session.StartUtc)
        {
            _sessionStart = session.StartUtc;   // another session: counted from its start
            _shown = 0;
        }

        var played = DateTime.UtcNow - session.StartUtc;
        if (Due(played, every, _shown) is not { } count) return;

        _shown = count;
        GameToastWindow.ShowMessage($"You've been playing for {Describe(played)}", session.Game.DisplayName,
            "Maybe time for a short break: stretch, drink some water, rest your eyes.", TimeSpan.FromSeconds(12));
    }

    /// <summary>The number of reminders that should have been shown by now, when it is more than <paramref name="shown"/>.</summary>
    internal static int? Due(TimeSpan played, int everyMinutes, int shown)
    {
        var due = (int)(played.TotalMinutes / everyMinutes);
        return due > shown ? due : null;
    }

    /// <summary>"1 hour", "2 hours", "1 hour 30 minutes", "45 minutes".</summary>
    internal static string Describe(TimeSpan played)
    {
        var hours = (int)played.TotalHours;
        var minutes = played.Minutes;

        var h = hours == 1 ? "1 hour" : $"{hours} hours";
        var m = minutes == 1 ? "1 minute" : $"{minutes} minutes";
        return hours == 0 ? m : minutes == 0 ? h : $"{h} {m}";
    }
}
