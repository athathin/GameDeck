using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using SDL2;

namespace GameDeck.Services;

/// <summary>
/// The controller while Big Picture isn't the window in front:
///  - opens Big Picture mode while GameDeck sits in the tray: press the controller's Home button (Xbox, PlayStation or Home
///    button), or hold View + Menu (Back + Start) for a second;
///  - runs the Home-button shortcuts (<see cref="ControllerShortcuts"/>: volume, songs, Alt + Tab...), also during games.
///
/// It is careful not to get in the way of games:
///  - it only opens Big Picture when <c>canWake</c> says so (no game started from GameDeck is running, Big Picture isn't open,
///    the setting is on) and never while a full-screen program is in front;
///  - it only reads the controller, with SDL's gentle drivers (XInput / DirectInput). The special drivers for PlayStation and
///    Switch controllers, which switch them into another mode, are only used by Big Picture itself;
///  - when neither may happen (both settings off, Big Picture in front), SDL is shut down and the controllers are let go.
/// </summary>
public sealed class ControllerWakeService : IDisposable
{
    private static readonly TimeSpan HoldTime = TimeSpan.FromSeconds(1);

    // 20 times a second: a quick tap of a button (about a tenth of a second) is never missed
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly Func<bool> _canWake;
    private readonly Func<bool> _canUseShortcuts;
    private readonly Func<bool> _canOpenOverGame;
    private readonly List<(IntPtr Handle, int InstanceId)> _pads = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly HashSet<PadButton> _held = [];
    private readonly HashSet<PadButton> _padHeld = [];

    private bool _started;
    private bool _unavailable;      // SDL can't be loaded: stop trying
    private bool _primed;           // the first reading after starting only records what is already held
    private bool _comboFired;
    private TimeSpan? _comboSince;

    /// <summary>Raised on the UI thread when the controller asked for Big Picture mode.</summary>
    public event Action? WakeRequested;

    /// <param name="canWake">May a controller open Big Picture now (asked many times a second)?</param>
    /// <param name="canUseShortcuts">May the Home-button shortcuts run now (games included)?</param>
    /// <param name="canOpenOverGame">
    /// Is a game started from GameDeck running, so a plain Home press opens Big Picture over it (its full screen is expected
    /// then, so it doesn't stop it; holding View + Menu doesn't, as a game may use that)?
    /// </param>
    public ControllerWakeService(Func<bool> canWake, Func<bool> canUseShortcuts, Func<bool> canOpenOverGame)
    {
        _canWake = canWake;
        _canUseShortcuts = canUseShortcuts;
        _canOpenOverGame = canOpenOverGame;
        _timer.Tick += (_, _) => Tick();
    }

    /// <summary>Start checking (20 times a second; it costs next to nothing while it has nothing to listen to).</summary>
    public void Begin() => _timer.Start();

    /// <summary>Lets go of the controllers right now (Big Picture mode is about to take them, with its own settings).</summary>
    public void Suspend() => Stop();

    public void Dispose()
    {
        _timer.Stop();
        Stop();
        ControllerShortcuts.Reset();   // GameDeck is closing: never leave Alt held down
    }

    private void Tick()
    {
        try
        {
            if (_unavailable)
            {
                _timer.Stop();
                return;
            }

            var shortcuts = _canUseShortcuts();
            var overGame = _canOpenOverGame();
            var wake = !overGame && _canWake() && !FullScreenInFront();

            if (!shortcuts && !wake && !overGame)
            {
                Stop();   // let the controllers go: nothing to listen for (or Big Picture has them)
                return;
            }

            if (!_started && !Start()) return;

            HandleEvents();
            Check(wake, overGame, shortcuts);
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            _unavailable = true;
            _started = false;
        }
    }

    // ---------- SDL ----------

    private bool Start()
    {
        SDL.SDL_SetHint(SDL.SDL_HINT_JOYSTICK_HIDAPI, "0");              // no mode-switching drivers for PlayStation / Switch pads
        SDL.SDL_SetHint("SDL_JOYSTICK_RAWINPUT", "0");
        SDL.SDL_SetHint(SDL.SDL_HINT_JOYSTICK_ALLOW_BACKGROUND_EVENTS, "1");

        if (SDL.SDL_Init(SDL.SDL_INIT_GAMECONTROLLER) != 0)
        {
            _unavailable = true;
            return false;
        }

        _started = true;
        _primed = false;

        for (var i = 0; i < SDL.SDL_NumJoysticks(); i++)
            TryOpen(i);

        return true;
    }

    private void Stop()
    {
        if (!_started) return;
        _started = false;

        foreach (var (handle, _) in _pads)
        {
            try { SDL.SDL_GameControllerClose(handle); }
            catch { }
        }

        _pads.Clear();

        try { SDL.SDL_QuitSubSystem(SDL.SDL_INIT_GAMECONTROLLER); }
        catch { }

        _comboFired = false;
        _comboSince = null;
    }

    private void HandleEvents()
    {
        while (SDL.SDL_PollEvent(out var e) == 1)
        {
            switch (e.type)
            {
                case SDL.SDL_EventType.SDL_CONTROLLERDEVICEADDED:
                    TryOpen(e.cdevice.which);
                    break;

                case SDL.SDL_EventType.SDL_CONTROLLERDEVICEREMOVED:
                    var gone = _pads.FindIndex(p => p.InstanceId == e.cdevice.which);
                    if (gone >= 0)
                    {
                        SDL.SDL_GameControllerClose(_pads[gone].Handle);
                        _pads.RemoveAt(gone);
                    }
                    break;
            }
        }
    }

    private void TryOpen(int deviceIndex)
    {
        if (SDL.SDL_IsGameController(deviceIndex) != SDL.SDL_bool.SDL_TRUE) return;

        var handle = SDL.SDL_GameControllerOpen(deviceIndex);
        if (handle == IntPtr.Zero) return;

        var instanceId = SDL.SDL_JoystickInstanceID(SDL.SDL_GameControllerGetJoystick(handle));
        if (_pads.Any(p => p.InstanceId == instanceId))
            SDL.SDL_GameControllerClose(handle);   // SDL also announces controllers that were there at start
        else
            _pads.Add((handle, instanceId));
    }

    /// <summary>
    /// Passes the held buttons to the Home-button shortcuts, and looks for a plain press of Home or View + Menu held for a
    /// second (both open Big Picture, only when <paramref name="wake"/> allows it). During a game (<paramref name="overGame"/>)
    /// only the plain Home press does.
    /// </summary>
    private void Check(bool wake, bool overGame, bool shortcuts)
    {
        // No controller found yet (SDL announces them a moment after starting): nothing is known to be held, so the shortcuts
        // aren't told "nothing held" either. Taking over from Big Picture with Home still held, that would read as Home let go.
        if (_pads.Count == 0) return;

        _held.Clear();
        var combo = false;
        foreach (var (handle, _) in _pads)
        {
            _padHeld.Clear();
            GamepadService.ReadButtons(handle, _padHeld);
            combo |= _padHeld.Contains(PadButton.Select) && _padHeld.Contains(PadButton.Start);   // (on one controller)
            _held.UnionWith(_padHeld);
        }

        var now = _clock.Elapsed;

        if (!_primed)
        {
            // whatever is held right now (a button pressed while connecting) doesn't count: only new presses do. (The first
            // reading after starting can also come back empty while the controller wakes up: it isn't passed on.)
            _primed = true;
            _comboFired = combo;
            _comboSince = combo ? now : null;
            return;
        }

        // a press of Home on its own (let go without a shortcut) opens Big Picture; with a shortcut it was the shortcut's
        // (with the shortcuts off, they are told nothing is held but Home, so Home still opens Big Picture)
        if (!shortcuts)
            _held.IntersectWith([PadButton.Home]);

        if (ControllerShortcuts.Update(_held) && (wake || overGame))
        {
            WakeRequested?.Invoke();
            return;
        }

        if (!wake || ControllerShortcuts.HomeHeld)
        {
            _comboSince = null;   // (View + Menu with Home held is a shortcut, not a request for Big Picture)
            return;
        }

        if (!combo)
        {
            _comboSince = null;
            _comboFired = false;
        }
        else if (_comboSince is null)
        {
            _comboSince = now;
        }
        else if (!_comboFired && now - _comboSince >= HoldTime)
        {
            _comboFired = true;
            WakeRequested?.Invoke();
        }
    }

    // ---------- is something full screen in front? ----------

    /// <summary>
    /// True when a game, a full-screen video or a presentation is in front, so a stray button press never throws up
    /// Big Picture mode over it. Uses Windows' own "is the user busy" answer plus a check for a borderless full-screen window.
    /// </summary>
    private static bool FullScreenInFront()
    {
        // 5 = free to be interrupted, 6 = quiet time (focus assist); anything else: screen saver, full-screen app, presentation...
        if (SHQueryUserNotificationState(out var state) == 0 && state is not (5 or 6))
            return true;

        var window = GetForegroundWindow();
        if (window == IntPtr.Zero) return false;

        var name = new StringBuilder(64);
        GetClassName(window, name, name.Capacity);
        if (name.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd")
            return false;   // the desktop itself covers the screen but isn't an app

        if (!GetWindowRect(window, out var rect)) return false;

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(window, 2 /* nearest */), ref info)) return false;

        return rect.Left <= info.Monitor.Left && rect.Top <= info.Monitor.Top &&
               rect.Right >= info.Monitor.Right && rect.Bottom >= info.Monitor.Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder text, int max);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out Rect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
