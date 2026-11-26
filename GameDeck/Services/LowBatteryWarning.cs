using System.Windows.Threading;
using GameDeck.Views;

namespace GameDeck.Services;

/// <summary>
/// Warns with a notice in the corner when a connected Xbox controller's battery runs low: at 15% and again at 5%, once each,
/// starting over once it has been charged above 25%. Checked once a minute in the background (also during games), from the
/// battery Windows' Bluetooth settings show (<see cref="BluetoothBattery"/>), which takes milliseconds.
/// </summary>
public sealed class LowBatteryWarning : IDisposable
{
    private static readonly int[] Levels = [5, 15];   // the lowest first
    private const int ChargedAgain = 25;
    private const int None = 101;                      // no warning given yet

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(1) };
    private int _warnedAt = None;   // the lowest level already warned about
    private bool _checking;

    public LowBatteryWarning() => _timer.Tick += (_, _) => _ = CheckAsync();

    public void Start()
    {
        _timer.Start();
        _ = CheckAsync();
    }

    public void Dispose() => _timer.Stop();

    private async Task CheckAsync()
    {
        // (the Beta leaves it to the normal GameDeck while that one runs: one warning is enough)
        if (_checking || !SettingsService.LowBatteryWarning || AppInfo.NormalGameDeckRunning) return;

        _checking = true;
        try
        {
            if (await BluetoothBattery.ConnectedControllerPercentAsync("Xbox") is not { } percent) return;

            var (warn, warnedAt) = Decide(percent, _warnedAt);
            _warnedAt = warnedAt;

            if (warn)
                GameToastWindow.ShowMessage("Controller battery low", $"{percent}% left",
                    percent <= Levels[0] ? "Charge it now, or it will turn off soon." : "Time to charge it soon.");
        }
        finally
        {
            _checking = false;
        }
    }

    /// <summary>Whether to warn at this battery level, and the lowest level warned about from now on.</summary>
    internal static (bool Warn, int WarnedAt) Decide(int percent, int warnedAt)
    {
        if (percent > ChargedAgain) return (false, None);   // charged: the next time it runs low is warned about again

        foreach (var level in Levels)
        {
            if (percent <= level && warnedAt > level)
                return (true, level);
        }

        return (false, warnedAt);
    }
}
