using System.Diagnostics;
using SDL2;

namespace GameDeck.Services;

/// <summary>The buttons Big Picture mode cares about, the same whichever controller is plugged in.</summary>
public enum PadButton
{
    Up,
    Down,
    Left,
    Right,
    Confirm,        // Xbox A, PlayStation ✕, Nintendo A
    Back,           // Xbox B, PlayStation ○, Nintendo B
    X,              // Xbox X, PlayStation □, Nintendo X
    Y,              // Xbox Y, PlayStation △, Nintendo Y
    LeftShoulder,   // LB / L1 / L
    RightShoulder,  // RB / R1 / R
    RightTrigger,   // RT / R2 / ZR
    Select,         // View / Share (Create) / −
    Start,          // Menu / Options / +
    Home            // Xbox button / PS button / Home
}

/// <summary>Which family the controller being used belongs to (decides the button names shown on screen).</summary>
public enum ControllerKind
{
    None,
    Xbox,
    PlayStation,
    Nintendo,
    Generic
}

/// <summary>
/// Reads game controllers through SDL2, which knows the layouts of Xbox, PlayStation (DualShock 4, DualSense) and
/// Nintendo Switch Pro controllers (wired or Bluetooth) and presents them all the same way.
/// Only started while Big Picture mode is open and in front, and shut down again afterwards, so SDL never switches a
/// controller into another mode while a game is using it.
/// </summary>
public sealed class GamepadService : IDisposable
{
    private static readonly TimeSpan RepeatDelay = TimeSpan.FromMilliseconds(420);
    private static readonly TimeSpan RepeatInterval = TimeSpan.FromMilliseconds(110);
    private const short StickThreshold = 16000;   // out of 32767: ignores a stick that doesn't rest exactly in the middle

    private sealed class Pad
    {
        public IntPtr Handle;
        public int InstanceId;
        public ControllerKind Kind;
        public string Name = "";
    }

    private readonly List<Pad> _pads = [];
    private Pad? _activePad;                                              // the controller used last
    private bool _primed;                                                 // false until the first reading after Start
    private readonly Dictionary<PadButton, TimeSpan> _heldSince = [];     // when a button went down
    private readonly Dictionary<PadButton, TimeSpan> _lastRepeat = [];    // when it last fired
    private readonly HashSet<PadButton> _down = [];                       // buffers reused by every Poll
    private readonly HashSet<PadButton> _padDown = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private bool _started;

    /// <summary>False when SDL couldn't be started (missing or blocked native file): the keyboard still works.</summary>
    public bool Available { get; private set; } = true;

    /// <summary>The family of the controller that was used last (None when no controller is connected).</summary>
    public ControllerKind ActiveKind { get; private set; }

    /// <summary>The name Windows / SDL gives the controller used last, e.g. "DualSense Wireless Controller".</summary>
    public string ActiveName { get; private set; } = "";

    /// <summary>Raised when a controller is plugged in, removed, or another one is used.</summary>
    public event Action? ControllerChanged;

    public bool Start()
    {
        if (_started) return true;

        try
        {
            // button labels as printed on the controller: A is the right-hand button on a Nintendo pad
            SDL.SDL_SetHint(SDL.SDL_HINT_GAMECONTROLLER_USE_BUTTON_LABELS, "1");
            SDL.SDL_SetHint(SDL.SDL_HINT_JOYSTICK_ALLOW_BACKGROUND_EVENTS, "1");
            SDL.SDL_SetHint(SDL.SDL_HINT_JOYSTICK_HIDAPI, "1");   // proper drivers for PlayStation and Switch Pro controllers
            SDL.SDL_SetHint("SDL_JOYSTICK_RAWINPUT", "1");        // (the background listener turns these off: set them again here)

            if (SDL.SDL_Init(SDL.SDL_INIT_GAMECONTROLLER) != 0)
            {
                Available = false;
                return false;
            }

            _started = true;
            _primed = false;
            Available = true;

            for (var i = 0; i < SDL.SDL_NumJoysticks(); i++)   // controllers that were already plugged in
                TryOpen(i);

            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            Available = false;
            return false;
        }
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false;

        foreach (var pad in _pads)
        {
            try { SDL.SDL_GameControllerClose(pad.Handle); }
            catch { }
        }

        _pads.Clear();
        _heldSince.Clear();
        _lastRepeat.Clear();

        try { SDL.SDL_QuitSubSystem(SDL.SDL_INIT_GAMECONTROLLER); }
        catch { }

        SetActive(null);
    }

    public void Dispose() => Stop();

    /// <summary>
    /// Call about 60 times a second. Returns the buttons that were pressed since the last call; a held direction
    /// repeats after a short delay so a long list can be scrolled by holding the stick or the d-pad.
    /// </summary>
    public List<PadButton> Poll()
    {
        var pressed = new List<PadButton>();
        if (!_started) return pressed;

        try
        {
            HandleEvents();

            var now = _clock.Elapsed;
            var down = _down;   // reused every call: this runs 60 times a second
            down.Clear();

            foreach (var pad in _pads)
            {
                _padDown.Clear();
                ReadButtons(pad, _padDown);
                if (_padDown.Count > 0 && !ReferenceEquals(pad, _activePad))
                    SetActive(pad);   // this controller is the one in use now

                down.UnionWith(_padDown);
            }

            // Buttons already held when the screen opens don't count as presses until they are let go. Otherwise opening
            // Big Picture by holding View + Menu on the controller would instantly press both in here too.
            if (!_primed)
            {
                _primed = true;
                foreach (var button in down)
                {
                    _heldSince[button] = now;
                    _lastRepeat[button] = now;
                }

                return pressed;
            }

            foreach (var button in down)
            {
                if (!_heldSince.ContainsKey(button))
                {
                    _heldSince[button] = now;       // freshly pressed
                    _lastRepeat[button] = now;
                    pressed.Add(button);
                }
                else if (IsDirection(button) && now - _heldSince[button] >= RepeatDelay && now - _lastRepeat[button] >= RepeatInterval)
                {
                    _lastRepeat[button] = now;      // held: repeat
                    pressed.Add(button);
                }
            }

            if (_heldSince.Count > 0)   // (nothing is allocated while no button is held: this runs 60 times a second)
            {
                foreach (var released in _heldSince.Keys.Where(b => !down.Contains(b)).ToList())
                {
                    _heldSince.Remove(released);
                    _lastRepeat.Remove(released);
                }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            Available = false;
            _started = false;
        }

        return pressed;
    }

    // ---------- SDL ----------

    /// <summary>Plugging a controller in or out while the screen is open.</summary>
    private void HandleEvents()
    {
        while (SDL.SDL_PollEvent(out var e) == 1)
        {
            switch (e.type)
            {
                case SDL.SDL_EventType.SDL_CONTROLLERDEVICEADDED:
                    TryOpen(e.cdevice.which);   // here "which" is the device index
                    break;

                case SDL.SDL_EventType.SDL_CONTROLLERDEVICEREMOVED:
                    Remove(e.cdevice.which);    // here "which" is the instance id
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
        {
            SDL.SDL_GameControllerClose(handle);   // already open (SDL also announces controllers that were there at start)
            return;
        }

        var name = SDL.SDL_GameControllerName(handle);
        var kind = KindOf(SDL.SDL_GameControllerGetType(handle), SDL.SDL_GameControllerGetVendor(handle), name);

        var pad = new Pad
        {
            Handle = handle,
            InstanceId = instanceId,
            Kind = kind,
            Name = ReadableName(name, kind)
        };

        _pads.Add(pad);
        SetActive(pad);
    }

    /// <summary>
    /// Some controllers report their name in a form that comes out as garbage (Chinese-looking characters). Only a name made
    /// of ordinary letters is shown as it is; otherwise the controller family is named instead.
    /// </summary>
    private static string ReadableName(string? name, ControllerKind kind)
    {
        if (!string.IsNullOrWhiteSpace(name) && name.All(c => c >= ' ' && c <= '~'))
            return name.Trim();

        return kind switch
        {
            ControllerKind.Xbox => "Xbox controller",
            ControllerKind.PlayStation => "PlayStation controller",
            ControllerKind.Nintendo => "Nintendo controller",
            _ => "Controller"
        };
    }

    private void Remove(int instanceId)
    {
        var pad = _pads.FirstOrDefault(p => p.InstanceId == instanceId);
        if (pad is null) return;

        SDL.SDL_GameControllerClose(pad.Handle);
        _pads.Remove(pad);

        SetActive(_pads.Count > 0 ? _pads[^1] : null);
    }

    /// <summary>
    /// SDL doesn't always know the exact model (an Xbox Elite over Bluetooth can come back as "unknown"), so when its type says
    /// nothing, the maker's USB vendor id decides (Microsoft, Sony, Nintendo), then the name.
    /// </summary>
    private static ControllerKind KindOf(SDL.SDL_GameControllerType type, ushort vendor, string? name)
    {
        var kind = KindOf(type);
        if (kind != ControllerKind.Generic)
            return kind;

        return vendor switch
        {
            0x045E => ControllerKind.Xbox,          // Microsoft
            0x054C => ControllerKind.PlayStation,   // Sony
            0x057E => ControllerKind.Nintendo,      // Nintendo
            _ when name?.Contains("xbox", StringComparison.OrdinalIgnoreCase) == true => ControllerKind.Xbox,
            _ => ControllerKind.Generic
        };
    }

    private static ControllerKind KindOf(SDL.SDL_GameControllerType type) => type switch
    {
        SDL.SDL_GameControllerType.SDL_CONTROLLER_TYPE_XBOX360 or
        SDL.SDL_GameControllerType.SDL_CONTROLLER_TYPE_XBOXONE => ControllerKind.Xbox,

        SDL.SDL_GameControllerType.SDL_CONTROLLER_TYPE_PS3 or
        SDL.SDL_GameControllerType.SDL_CONTROLLER_TYPE_PS4 or
        SDL.SDL_GameControllerType.SDL_CONTROLLER_TYPE_PS5 => ControllerKind.PlayStation,

        SDL.SDL_GameControllerType.SDL_CONTROLLER_TYPE_NINTENDO_SWITCH_PRO => ControllerKind.Nintendo,

        _ => ControllerKind.Generic
    };

    private void SetActive(Pad? pad)
    {
        _activePad = pad;

        var kind = pad?.Kind ?? ControllerKind.None;
        var name = pad?.Name ?? "";

        if (kind == ActiveKind && name == ActiveName) return;

        ActiveKind = kind;
        ActiveName = name;
        ControllerChanged?.Invoke();
    }

    private static void ReadButtons(Pad pad, HashSet<PadButton> down)
    {
        bool Held(SDL.SDL_GameControllerButton b) => SDL.SDL_GameControllerGetButton(pad.Handle, b) != 0;
        short Axis(SDL.SDL_GameControllerAxis a) => SDL.SDL_GameControllerGetAxis(pad.Handle, a);

        var stickX = Axis(SDL.SDL_GameControllerAxis.SDL_CONTROLLER_AXIS_LEFTX);
        var stickY = Axis(SDL.SDL_GameControllerAxis.SDL_CONTROLLER_AXIS_LEFTY);

        if (Held(SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_DPAD_UP) || stickY < -StickThreshold) down.Add(PadButton.Up);
        if (Held(SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_DPAD_DOWN) || stickY > StickThreshold) down.Add(PadButton.Down);
        if (Held(SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_DPAD_LEFT) || stickX < -StickThreshold) down.Add(PadButton.Left);
        if (Held(SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_DPAD_RIGHT) || stickX > StickThreshold) down.Add(PadButton.Right);

        // (by default A confirms and B goes back; with the "swap A and B" setting it is the other way round)
        var swap = SettingsService.SwapConfirmBack;
        if (Held(SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_A)) down.Add(swap ? PadButton.Back : PadButton.Confirm);
        if (Held(SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_B)) down.Add(swap ? PadButton.Confirm : PadButton.Back);
        if (Held(SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_X)) down.Add(PadButton.X);
        if (Held(SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_Y)) down.Add(PadButton.Y);
        if (Held(SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_LEFTSHOULDER)) down.Add(PadButton.LeftShoulder);
        if (Held(SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_RIGHTSHOULDER)) down.Add(PadButton.RightShoulder);
        if (Held(SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_START)) down.Add(PadButton.Start);
        if (Held(SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_BACK)) down.Add(PadButton.Select);
        if (Held(SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_GUIDE)) down.Add(PadButton.Home);
        if (Axis(SDL.SDL_GameControllerAxis.SDL_CONTROLLER_AXIS_TRIGGERRIGHT) > StickThreshold) down.Add(PadButton.RightTrigger);
    }

    private static bool IsDirection(PadButton button) => button is PadButton.Up or PadButton.Down or PadButton.Left or PadButton.Right;
}
