using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace GameDeck.Services;

/// <summary>
/// The Home-button shortcuts, like Steam's: hold the controller's Home button (Xbox, PS, Home) and press another button.
///   Home + up / down (left stick or d-pad)   volume up / down (held: keeps going)
///   Home + right / left                      next / previous song (media keys)
///   Home + left stick pressed in             play / pause
///   Home + View                              Windows' on-screen keyboard (press again: closes it)
///   Home + Menu                              Alt + Tab (Alt stays held while Home is: Menu again goes on to the next window)
///   Home + RT                                screenshot
/// Home pressed and let go on its own keeps its usual job (Big Picture's Power menu, opening Big Picture from the tray):
/// <see cref="Update"/> says when that happened.
///
/// One set of shortcuts for the whole program: Big Picture feeds it while it is in front (<see cref="GamepadService"/>), the
/// background listener the rest of the time, games included (<see cref="ControllerWakeService"/>). Call from the UI thread.
/// </summary>
public static class ControllerShortcuts
{
    private static readonly TimeSpan RepeatDelay = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan RepeatInterval = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan Silence = TimeSpan.FromSeconds(1);   // nobody reading the controller: let go of Alt

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly HashSet<PadButton> Before = [];               // the buttons held at the last update
    private static readonly Dictionary<PadButton, TimeSpan> HeldSince = [];
    private static readonly Dictionary<PadButton, TimeSpan> LastRepeat = [];

    private static bool _homeHeld;
    private static bool _used;       // a shortcut was used during this press of Home
    private static bool _altDown;    // Alt is held down for Alt + Tab
    private static TimeSpan _lastUpdate;
    private static DispatcherTimer? _altGuard;

    /// <summary>Takes a screenshot (set by the main window: it knows which game is running).</summary>
    public static Action? TakeScreenshot { get; set; }

    /// <summary>True while Home is held: the other buttons are shortcuts then, not presses for Big Picture.</summary>
    public static bool HomeHeld => _homeHeld;

    /// <summary>
    /// Takes the buttons held right now (any rate; Big Picture 60 times a second, the background listener 20). Runs the
    /// shortcut of every button newly pressed while Home is held (volume repeats while held).
    /// </summary>
    /// <returns>True when Home was let go and no shortcut was used during that press: a plain press of Home.</returns>
    public static bool Update(IReadOnlySet<PadButton> held)
    {
        var now = Clock.Elapsed;
        _lastUpdate = now;

        var tapped = false;
        var home = held.Contains(PadButton.Home);

        if (home && !_homeHeld)
        {
            _homeHeld = true;
            _used = false;
        }
        else if (!home && _homeHeld)
        {
            _homeHeld = false;
            ReleaseAlt();
            tapped = !_used;
        }

        if (_homeHeld && SettingsService.ControllerShortcuts)
        {
            foreach (var button in held)
            {
                if (button == PadButton.Home) continue;

                if (!Before.Contains(button))
                {
                    HeldSince[button] = now;
                    LastRepeat[button] = now;
                    Run(button);
                }
                else if (button is PadButton.Up or PadButton.Down && HeldSince.TryGetValue(button, out var since) &&
                         now - since >= RepeatDelay && now - LastRepeat[button] >= RepeatInterval)
                {
                    LastRepeat[button] = now;   // the volume keeps going while the stick is held
                    Run(button);
                }
            }
        }

        foreach (var button in Before.Where(b => !held.Contains(b)).ToList())
        {
            HeldSince.Remove(button);
            LastRepeat.Remove(button);
        }

        Before.Clear();
        Before.UnionWith(held);
        return tapped;
    }

    /// <summary>Forgets what is held and lets go of Alt (GameDeck is closing, or nobody reads the controller any more).</summary>
    public static void Reset()
    {
        ReleaseAlt();
        _homeHeld = false;
        Before.Clear();
        HeldSince.Clear();
        LastRepeat.Clear();
    }

    private static void Run(PadButton button)
    {
        switch (button)
        {
            case PadButton.Up: Tap(VkVolumeUp, extended: true); break;
            case PadButton.Down: Tap(VkVolumeDown, extended: true); break;
            case PadButton.Right: Tap(VkMediaNext, extended: true); break;
            case PadButton.Left: Tap(VkMediaPrevious, extended: true); break;
            case PadButton.LeftStick: Tap(VkMediaPlayPause, extended: true); break;
            case PadButton.Select: ToggleKeyboard(); break;
            case PadButton.Start: AltTab(); break;
            case PadButton.RightTrigger: TakeScreenshot?.Invoke(); break;
            default: return;   // no shortcut on this button: Home still counts as a plain press
        }

        _used = true;
    }

    // ---------- Alt + Tab ----------

    private static void AltTab()
    {
        if (!_altDown)
        {
            Key(VkAlt, up: false);
            _altDown = true;

            // if the controller goes quiet while Alt is down (unplugged, Big Picture handing over...), Alt is let go
            _altGuard ??= new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Normal, (_, _) =>
            {
                if (Clock.Elapsed - _lastUpdate > Silence) ReleaseAlt();
            }, Dispatcher.CurrentDispatcher);
            _altGuard.Start();
        }

        Tap(VkTab, extended: false);
    }

    private static void ReleaseAlt()
    {
        _altGuard?.Stop();
        if (!_altDown) return;

        _altDown = false;
        Key(VkAlt, up: true);
    }

    // ---------- Windows' on-screen keyboard ----------

    /// <summary>Opens Windows' on-screen keyboard, or closes it when it is open.</summary>
    private static void ToggleKeyboard()
    {
        try
        {
            var open = Process.GetProcessesByName("osk");
            if (open.Length > 0)
            {
                foreach (var keyboard in open)
                {
                    using (keyboard) keyboard.CloseMainWindow();
                }

                return;
            }

            // a 32-bit GameDeck on 64-bit Windows sees System32 redirected: Sysnative is the real one
            var system = !Environment.Is64BitProcess && Environment.Is64BitOperatingSystem ? "Sysnative" : "System32";
            var osk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), system, "osk.exe");
            Process.Start(new ProcessStartInfo(osk) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            CrashLog.Write(ex, "On-screen keyboard");
        }
    }

    // ---------- pressing keys (SendInput) ----------

    private const ushort VkTab = 0x09, VkAlt = 0x12;
    private const ushort VkVolumeDown = 0xAE, VkVolumeUp = 0xAF;
    private const ushort VkMediaNext = 0xB0, VkMediaPrevious = 0xB1, VkMediaPlayPause = 0xB3;

    private const uint InputKeyboard = 1, KeyExtended = 0x0001, KeyUp = 0x0002;

    private static void Tap(ushort key, bool extended)
    {
        Send(Input(key, extended ? KeyExtended : 0), Input(key, (extended ? KeyExtended : 0) | KeyUp));
    }

    private static void Key(ushort key, bool up) => Send(Input(key, up ? KeyUp : 0));

    private static void Send(params NativeInput[] inputs) => SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>());

    private static NativeInput Input(ushort key, uint flags) => new()
    {
        Type = InputKeyboard,
        Data = new InputData { Keyboard = new KeyboardInput { Key = key, Flags = flags } }
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput
    {
        public uint Type;
        public InputData Data;
    }

    // (the mouse member is the largest: it gives the union the size Windows expects)
    [StructLayout(LayoutKind.Explicit)]
    private struct InputData
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X, Y;
        public uint Data, Flags, Time;
        public IntPtr Extra;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort Key, Scan;
        public uint Flags, Time;
        public IntPtr Extra;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, NativeInput[] inputs, int size);
}
