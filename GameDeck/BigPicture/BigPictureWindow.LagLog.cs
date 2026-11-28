#if BETA
using System.Diagnostics;
using System.IO;
using GameDeck.Services;

namespace GameDeck.Views;

/// <summary>
/// GameDeck Beta only: finds where the controller's presses are slow. Writes a line to "input-lag.log" in the Beta's data
/// folder whenever a press takes long to handle, the controller wasn't read for a while (the screen was busy), or the screen
/// is laid out again and again while nothing happens.
/// </summary>
public partial class BigPictureWindow
{
    private readonly Stopwatch _lagClock = Stopwatch.StartNew();
    private long _lastTickMs = -1;
    private int _layouts;
    private long _layoutsSinceMs;

    private static readonly string LagLogFile =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppInfo.DataFolderName, "input-lag.log");

    private string LagWhere() =>
        $"{_overlay}{(_overlay == Overlay.GameMenu && _menuPages.Count > 0 ? " / " + _menuPages.Peek().Title : "")}";

    private void LagLine(string text)
    {
        try { File.AppendAllText(LagLogFile, $"{DateTime.Now:HH:mm:ss.fff}  {text}  [{LagWhere()}]{Environment.NewLine}"); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>At the start of each controller read: was the last one long ago? And how often was the screen laid out?</summary>
    private void LagTick()
    {
        if (_layoutsSinceMs == 0)
        {
            LayoutUpdated += (_, _) => _layouts++;
            _layoutsSinceMs = _lagClock.ElapsedMilliseconds;
            LagLine("Big Picture opened (logging slow controller input)");
        }

        var now = _lagClock.ElapsedMilliseconds;
        if (_lastTickMs >= 0 && now - _lastTickMs > 100)
            LagLine($"controller not read for {now - _lastTickMs} ms (the screen was busy)");
        _lastTickMs = now;

        if (now - _layoutsSinceMs >= 1000)
        {
            if (_layouts > 40)
                LagLine($"{_layouts} layouts in the last second");
            _layouts = 0;
            _layoutsSinceMs = now;
        }
    }

    /// <summary>One press handled: how long it took.</summary>
    private void LagPress(PadButton button, long startMs)
    {
        var took = _lagClock.ElapsedMilliseconds - startMs;
        if (took > 30)
            LagLine($"{button} took {took} ms");
    }

    private long LagNow => _lagClock.ElapsedMilliseconds;
}
#endif
