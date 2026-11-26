using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace GameDeck.Services;

/// <summary>A screen resolution and refresh rate, e.g. 1920 × 1080 at 144 Hz.</summary>
public sealed record DisplayMode(int Width, int Height, int Hz)
{
    /// <summary>"1920 × 1080 at 144 Hz".</summary>
    [JsonIgnore]
    public string Text => $"{Width} × {Height} at {Hz} Hz";
}

/// <summary>
/// The main display's resolution and refresh rate, for a game's launch routine: the modes it offers, and a change made only
/// while the game runs. The change is never saved in Windows' settings, so the normal mode comes back by itself after a restart;
/// GameDeck puts it back when the game ends, and at its next start if it stopped before it could (crash, closed while playing).
/// </summary>
public static class DisplayModes
{
    private static readonly string PendingFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppInfo.DataFolderName, "pending-display.txt");

    /// <summary>Every mode the main display offers (full color only), the biggest first and the fastest first within a size.</summary>
    public static List<DisplayMode> Available()
    {
        var modes = new HashSet<DisplayMode>();
        var dm = NewDevMode();

        for (var i = 0; EnumDisplaySettings(null, i, ref dm); i++)
        {
            if (dm.dmBitsPerPel >= 24 && dm.dmPelsWidth > 0 && dm.dmPelsHeight > 0 && dm.dmDisplayFrequency > 1)
                modes.Add(new DisplayMode(dm.dmPelsWidth, dm.dmPelsHeight, dm.dmDisplayFrequency));
        }

        return Order(modes);
    }

    /// <summary>Biggest first, then fastest first (separate so the tests can check it).</summary>
    internal static List<DisplayMode> Order(IEnumerable<DisplayMode> modes) =>
        modes.Distinct()
             .OrderByDescending(m => m.Width * m.Height).ThenByDescending(m => m.Width).ThenByDescending(m => m.Hz)
             .ToList();

    /// <summary>The main display's mode now, or null if Windows won't say.</summary>
    public static DisplayMode? Current()
    {
        var dm = NewDevMode();
        return EnumDisplaySettings(null, CurrentSettings, ref dm) && dm.dmPelsWidth > 0
            ? new DisplayMode(dm.dmPelsWidth, dm.dmPelsHeight, dm.dmDisplayFrequency)
            : null;
    }

    /// <summary>
    /// Switches the main display to <paramref name="mode"/> until <see cref="Restore"/> (not saved in Windows' settings).
    /// False when nothing was changed: it is the mode already, the display doesn't offer it, or Windows refused.
    /// </summary>
    public static bool Apply(DisplayMode mode)
    {
        if (Current() == mode) return false;   // nothing to change, so nothing to put back
        if (!Available().Contains(mode)) return false;

        var dm = NewDevMode();
        dm.dmPelsWidth = mode.Width;
        dm.dmPelsHeight = mode.Height;
        dm.dmDisplayFrequency = mode.Hz;
        dm.dmFields = FieldPelsWidth | FieldPelsHeight | FieldDisplayFrequency;

        if (ChangeDisplaySettingsEx(null, ref dm, IntPtr.Zero, ChangeTest, IntPtr.Zero) != ChangeSuccessful) return false;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PendingFile)!);
            File.WriteAllText(PendingFile, mode.Text);   // written first: put back at the next start even after a crash
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;   // can't promise to put it back, so don't change it
        }

        if (ChangeDisplaySettingsEx(null, ref dm, IntPtr.Zero, 0, IntPtr.Zero) == ChangeSuccessful)
            return true;

        TryDeletePending();
        return false;
    }

    /// <summary>Back to the mode saved in Windows' display settings.</summary>
    public static void Restore()
    {
        ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
        TryDeletePending();
    }

    /// <summary>Puts the screen back if GameDeck stopped while a game had changed it. Call at start-up.</summary>
    public static void RestorePending()
    {
        try
        {
            if (File.Exists(PendingFile))
                Restore();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // nothing more to do (a restart brings the normal mode back anyway)
        }
    }

    private static void TryDeletePending()
    {
        try { File.Delete(PendingFile); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // ---------- Windows ----------

    private const int CurrentSettings = -1;   // ENUM_CURRENT_SETTINGS
    private const int ChangeTest = 0x2;      // CDS_TEST: only check that the mode would work
    private const int ChangeSuccessful = 0;  // DISP_CHANGE_SUCCESSFUL
    private const int FieldPelsWidth = 0x80000, FieldPelsHeight = 0x100000, FieldDisplayFrequency = 0x400000;

    private static DevMode NewDevMode() => new() { dmSize = (short)Marshal.SizeOf<DevMode>() };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplaySettingsW")]
    private static extern bool EnumDisplaySettings(string? deviceName, int modeNumber, ref DevMode devMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "ChangeDisplaySettingsExW")]
    private static extern int ChangeDisplaySettingsEx(string? deviceName, ref DevMode devMode, IntPtr window, int flags, IntPtr param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "ChangeDisplaySettingsExW")]
    private static extern int ChangeDisplaySettingsEx(string? deviceName, IntPtr devMode, IntPtr window, int flags, IntPtr param);
}
