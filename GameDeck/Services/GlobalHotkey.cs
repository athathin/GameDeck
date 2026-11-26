using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace GameDeck.Services;

/// <summary>
/// A system-wide keyboard shortcut that works even while GameDeck sits in the tray or another program is in front.
/// Uses its own invisible message-only window, so it doesn't depend on the main window having been shown.
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 0x6D44;
    private static readonly IntPtr MessageOnlyParent = new(-3);   // HWND_MESSAGE

    public const uint Alt = 0x0001;
    public const uint Control = 0x0002;
    public const uint Shift = 0x0004;
    public const uint Win = 0x0008;
    private const uint NoRepeat = 0x4000;   // holding the keys down fires once

    private HwndSource? _source;
    private bool _registered;

    public event Action? Pressed;

    /// <summary>Registers the shortcut. False when another program already owns it.</summary>
    public bool Register(uint modifiers, uint virtualKey)
    {
        Unregister();   // a previous shortcut stops working
        _source ??= CreateWindow();

        _registered = RegisterHotKey(_source.Handle, HotkeyId, modifiers | NoRepeat, virtualKey);
        return _registered;
    }

    /// <summary>Stops the shortcut from doing anything (the message window stays for the next Register).</summary>
    public void Unregister()
    {
        if (_source is not null && _registered)
            UnregisterHotKey(_source.Handle, HotkeyId);

        _registered = false;
    }

    /// <summary>The shortcut as text, e.g. "Ctrl + Alt + G".</summary>
    public static string Describe(uint modifiers, uint virtualKey)
    {
        var parts = new List<string>();
        if ((modifiers & Control) != 0) parts.Add("Ctrl");
        if ((modifiers & Alt) != 0) parts.Add("Alt");
        if ((modifiers & Shift) != 0) parts.Add("Shift");
        if ((modifiers & Win) != 0) parts.Add("Win");

        var key = KeyInterop.KeyFromVirtualKey((int)virtualKey);
        var name = key.ToString();
        if (name.Length == 2 && name[0] == 'D' && char.IsDigit(name[1]))
            name = name[1..];                                  // D5 -> 5
        else if (name.StartsWith("NumPad", StringComparison.Ordinal))
            name = "Num " + name["NumPad".Length..];           // NumPad5 -> Num 5

        parts.Add(name);
        return string.Join(" + ", parts);
    }

    private HwndSource CreateWindow()
    {
        var source = new HwndSource(new HwndSourceParameters("GameDeckHotkey") { ParentWindow = MessageOnlyParent });
        source.AddHook(WndProc);
        return source;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            Pressed?.Invoke();
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_source is null) return;

        Unregister();
        _source.Dispose();
        _source = null;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);
}
