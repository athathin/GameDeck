using System.Runtime.InteropServices;

namespace GameDeck.Services;

/// <summary>
/// Switches off a wireless controller (the "Power Off Controller" entry of the Power menu). Windows has no call for that, so the
/// controller's Bluetooth connection is cut: a controller that loses its host switches itself off. Wired controllers can't be.
/// </summary>
public static class ControllerPower
{
    // the Bluetooth service that carries a controller's buttons (HumanInterfaceDeviceServiceClass)
    private static Guid _hidService = new("00001124-0000-1000-8000-00805F9B34FB");

    private const uint ServiceDisable = 0;
    private const uint ServiceEnable = 1;

    /// <summary>Cuts the connection of the connected Bluetooth controller(s). False when there is none (it is wired, or not Bluetooth).</summary>
    public static bool TurnOff(string controllerName)
    {
        try
        {
            var done = new List<(IntPtr Radio, BluetoothDeviceInfo Device)>();
            var radioParams = new FindRadioParams { Size = (uint)Marshal.SizeOf<FindRadioParams>() };

            var findRadio = BluetoothFindFirstRadio(ref radioParams, out var radio);
            if (findRadio == IntPtr.Zero) return false;

            try
            {
                do
                {
                    var disabledHere = false;

                    foreach (var device in ConnectedDevices(radio))
                    {
                        if (!LooksLikeController(device, controllerName)) continue;

                        var info = device;
                        if (BluetoothSetServiceState(radio, ref info, ref _hidService, ServiceDisable) == 0)
                        {
                            done.Add((radio, info));
                            disabledHere = true;
                        }
                    }

                    // (a radio stays open until its controllers are allowed to connect again, below)
                    if (!disabledHere) CloseHandle(radio);
                }
                while (BluetoothFindNextRadio(findRadio, out radio));
            }
            finally
            {
                BluetoothFindRadioClose(findRadio);
            }

            if (done.Count == 0) return false;

            // once it is off, the controller must be able to connect again when its button is pressed
            _ = Task.Run(async () =>
            {
                await Task.Delay(3000);

                foreach (var (r, device) in done)
                {
                    var info = device;
                    try { BluetoothSetServiceState(r, ref info, ref _hidService, ServiceEnable); }
                    catch { }

                    CloseHandle(r);
                }
            });

            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            return false;
        }
    }

    private static bool LooksLikeController(BluetoothDeviceInfo device, string controllerName)
    {
        // class of device: major class 5 (peripheral), minor class joystick (1) or gamepad (2)
        var major = (device.ClassOfDevice >> 8) & 0x1F;
        var minor = (device.ClassOfDevice >> 2) & 0xF;
        if (major == 5 && minor is 1 or 2) return true;

        var name = device.Name ?? "";
        return name.Length > 0 && ((controllerName.Length > 0 && name.Equals(controllerName, StringComparison.OrdinalIgnoreCase)) ||
                                   name.Contains("controller", StringComparison.OrdinalIgnoreCase) ||
                                   name.Contains("gamepad", StringComparison.OrdinalIgnoreCase));
    }

    private static List<BluetoothDeviceInfo> ConnectedDevices(IntPtr radio)
    {
        var found = new List<BluetoothDeviceInfo>();

        var search = new DeviceSearchParams
        {
            Size = (uint)Marshal.SizeOf<DeviceSearchParams>(),
            ReturnConnected = 1,
            Radio = radio
        };
        var info = new BluetoothDeviceInfo { Size = (uint)Marshal.SizeOf<BluetoothDeviceInfo>() };

        var find = BluetoothFindFirstDevice(ref search, ref info);
        if (find == IntPtr.Zero) return found;

        try
        {
            do
            {
                if (info.Connected != 0) found.Add(info);
            }
            while (BluetoothFindNextDevice(find, ref info));
        }
        finally
        {
            BluetoothFindDeviceClose(find);
        }

        return found;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FindRadioParams
    {
        public uint Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceSearchParams
    {
        public uint Size;
        public int ReturnAuthenticated;
        public int ReturnRemembered;
        public int ReturnUnknown;
        public int ReturnConnected;
        public int IssueInquiry;
        public byte TimeoutMultiplier;
        public IntPtr Radio;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8, CharSet = CharSet.Unicode)]
    private struct BluetoothDeviceInfo
    {
        public uint Size;
        public ulong Address;
        public uint ClassOfDevice;
        public int Connected;
        public int Remembered;
        public int Authenticated;
        public SystemTime LastSeen;
        public SystemTime LastUsed;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string Name;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemTime
    {
        public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
    }

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern IntPtr BluetoothFindFirstRadio(ref FindRadioParams parameters, out IntPtr radio);

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern bool BluetoothFindNextRadio(IntPtr find, out IntPtr radio);

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern bool BluetoothFindRadioClose(IntPtr find);

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern IntPtr BluetoothFindFirstDevice(ref DeviceSearchParams parameters, ref BluetoothDeviceInfo device);

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern bool BluetoothFindNextDevice(IntPtr find, ref BluetoothDeviceInfo device);

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern bool BluetoothFindDeviceClose(IntPtr find);

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern uint BluetoothSetServiceState(IntPtr radio, ref BluetoothDeviceInfo device, ref Guid service, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
