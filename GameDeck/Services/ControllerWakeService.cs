using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using SDL2;

namespace GameDeck.Services;

/// <summary>
/// Lets a controller open Big Picture mode while GameDeck sits in the tray: press the controller's Home button
/// (Xbox, PlayStation or Home button), or hold View + Menu (Back + Start) for a second.
///
/// It is careful not to get in the way of games:
///  - it only listens when <c>canListen</c> says so (no game started from GameDeck is running, Big Picture isn't open,
///    the setting is on) and never while a full-screen program is in front;
///  - it uses SDL's gentle drivers (XInput / DirectInput), which only read the controller. The special drivers for
///    PlayStation and Switch controllers, which switch them into another mode, are only used by Big Picture itself;
///  - the moment it must not listen, SDL is shut down and the controllers are let go.
/// </summary>
public sealed class ControllerWakeService : IDisposable
{
    private static readonly TimeSpan HoldTime = TimeSpan.FromSeconds(1);

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly Func<bool> _canListen;
    private readonly List<(IntPtr Handle, int InstanceId)> _pads = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private bool _started;
    private bool _unavailable;      // SDL can't be loaded: stop trying
    private bool _primed;           // the first reading after starting only records what is already held
    private bool _homeDown;
    private bool _comboFired;
    private TimeSpan? _comboSince;

    /// <summary>Raised on the UI thread when the controller asked for Big Picture mode.</summary>
    public event Action? WakeRequested;

    public ControllerWakeService(Func<bool> canListen)
    {
        _canListen = canListen;
        _timer.Tick += (_, _) => Tick();
    }

    /// <summary>Start checking (about ten times a second; it costs next to nothing while it has nothing to listen to).</summary>
    public void Begin() => _timer.Start();

    /// <summary>Lets go of the controllers right now (Big Picture mode is about to take them, with its own settings).</summary>
    public void Suspend() => Stop();

    public void Dispose()
    {
        _timer.Stop();
        Stop();
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

            if (!_canListen() || FullScreenInFront())
            {
                Stop();   // let the controllers go while a game (or a video, or Big Picture itself) has them
                return;
            }

            if (!_started && !Start()) return;

            HandleEvents();
            Check();
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

        _homeDown = false;
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

    /// <summary>Looks for the Home button, or View + Menu held for a second.</summary>
    private void Check()
    {
        bool Held(IntPtr pad, SDL.SDL_GameControllerButton button) => SDL.SDL_GameControllerGetButton(pad, button) != 0;

        var home = _pads.Any(p => Held(p.Handle, SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_GUIDE));
        var combo = _pads.Any(p => Held(p.Handle, SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_BACK) &&
                                   Held(p.Handle, SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_START));
        var now = _clock.Elapsed;

        if (!_primed)
        {
            // whatever is held right now (a button pressed while connecting) doesn't count: only new presses do
            _primed = true;
            _homeDown = home;
            _comboFired = combo;
            _comboSince = combo ? now : null;
            return;
        }

        if (home && !_homeDown)
        {
            _homeDown = true;
            WakeRequested?.Invoke();
            return;
        }

        _homeDown = home;

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
