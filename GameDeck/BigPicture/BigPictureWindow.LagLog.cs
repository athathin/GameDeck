#if BETA
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Threading;
using GameDeck.Services;

namespace GameDeck.Views;

/// <summary>
/// GameDeck Beta only: finds where the controller's presses are slow. Writes a line to "input-lag.log" in the Beta's data
/// folder whenever a press takes long to handle, reading the controller takes long, the controller wasn't read for a while
/// (and what kept the screen busy meanwhile: every piece of work on the screen's thread that took long, by name), or the
/// screen is laid out again and again while nothing happens.
/// </summary>
public partial class BigPictureWindow
{
    private readonly Stopwatch _lagClock = Stopwatch.StartNew();
    private long _lastTickMs = -1;
    private int _layouts;
    private long _layoutsSinceMs;
    private long _opStartMs = -1;
    private DispatcherOperation? _opRunning;

    private static readonly string LagLogFile =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppInfo.DataFolderName, "input-lag.log");

    private static readonly FieldInfo? OperationMethod =
        typeof(DispatcherOperation).GetField("_method", BindingFlags.NonPublic | BindingFlags.Instance);

    private string LagWhere() =>
        $"{_overlay}{(_overlay == Overlay.GameMenu && _menuPages.Count > 0 ? " / " + _menuPages.Peek().Title : "")}";

    private void LagLine(string text)
    {
        try { File.AppendAllText(LagLogFile, $"{DateTime.Now:HH:mm:ss.fff}  {text}  [{LagWhere()}]{Environment.NewLine}"); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private long LagNow => _lagClock.ElapsedMilliseconds;

    /// <summary>The name of what a piece of work runs: "Class.Method", for a timer the methods its Tick runs.</summary>
    private static string OperationName(DispatcherOperation operation)
    {
        if (OperationMethod?.GetValue(operation) is not Delegate method)
            return "?";

        // a DispatcherTimer's tick: the handlers it calls
        if (method.Target is DispatcherTimer timer &&
            typeof(DispatcherTimer).GetField("Tick", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(timer) is Delegate tick)
            return "timer (" + timer.Interval.TotalMilliseconds + " ms): " +
                   string.Join(", ", tick.GetInvocationList().Select(d => $"{d.Method.DeclaringType?.Name}.{d.Method.Name}"));

        return $"{method.Method.DeclaringType?.Name}.{method.Method.Name}";
    }

    private void StartLagLog()
    {
        LayoutUpdated += (_, _) => _layouts++;
        _layoutsSinceMs = LagNow;

        Dispatcher.Hooks.OperationStarted += (_, e) =>
        {
            _opRunning = e.Operation;
            _opStartMs = LagNow;
        };
        Dispatcher.Hooks.OperationCompleted += (_, e) =>
        {
            if (!ReferenceEquals(e.Operation, _opRunning) || _opStartMs < 0) return;

            var took = LagNow - _opStartMs;
            _opStartMs = -1;
            if (took > 50)
                LagLine($"busy {took} ms in {OperationName(e.Operation)} ({e.Operation.Priority})");
        };

        LagLine("Big Picture opened (logging slow controller input, detailed)");
    }

    /// <summary>At the start of each controller read: was the last one long ago? And how often was the screen laid out?</summary>
    private void LagTick()
    {
        if (_layoutsSinceMs == 0)
            StartLagLog();

        var now = LagNow;
        if (_lastTickMs >= 0 && now - _lastTickMs > 100)
            LagLine($"controller not read for {now - _lastTickMs} ms");
        _lastTickMs = now;

        if (now - _layoutsSinceMs >= 1000)
        {
            if (_layouts > 40)
                LagLine($"{_layouts} layouts in the last second");
            _layouts = 0;
            _layoutsSinceMs = now;
        }
    }

    /// <summary>Reading the controller (SDL) took this long.</summary>
    private void LagPoll(long startMs)
    {
        var took = LagNow - startMs;
        if (took > 30)
            LagLine($"reading the controller took {took} ms");
    }

    /// <summary>One press handled: how long it took.</summary>
    private void LagPress(PadButton button, long startMs)
    {
        var took = LagNow - startMs;
        if (took > 30)
            LagLine($"{button} took {took} ms");
    }
}
#endif
